using Microsoft.Extensions.Logging;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.NativeSources;

public sealed class NativeSourcePipeline(
    CardigannSearchCoordinator searchCoordinator,
    StreamAddonClient addons,
    IEnumerable<IStreamResolver> resolvers,
    DebridProviderOrchestrator debrid,
    ILogger<NativeSourcePipeline> logger
)
{
    private static readonly NativeReleaseAggregator Aggregator = new();
    private readonly IReadOnlyList<IStreamResolver> _resolvers = resolvers.ToList();

    /// <summary>Test/legacy shape: providers participate in registration order. Kept internal so DI never sees two same-arity constructors.</summary>
    internal NativeSourcePipeline(
        CardigannSearchCoordinator searchCoordinator,
        IEnumerable<IStreamResolver> resolvers,
        IEnumerable<IDebridProvider> debridProviders,
        ILogger<NativeSourcePipeline> logger,
        StreamAddonClient? addons = null
    )
        : this(searchCoordinator, addons!, resolvers, DebridProviderOrchestrator.FromProviders(debridProviders), logger)
    { }

    public DebridProviderOrchestrator Debrid => debrid;

    public async Task<NativeSearchResult> SearchAsync(
        NativeMediaQuery query,
        string? definitionId,
        CancellationToken cancellationToken,
        IndexerSearchBudget budget = IndexerSearchBudget.Routed
    )
    {
        var configuration = NebulaBridgePlugin.Instance?.Configuration;
        if (configuration?.EnableNativeScraper != true)
        {
            return new NativeSearchResult([], []);
        }

        return await DiscoverAsync(
                query,
                definitionId,
                configuration.StreamAddonUrls,
                TimeSpan.FromSeconds(configuration.StreamAddonTimeoutSeconds),
                cancellationToken,
                budget
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Stream addons and the indexer sweep are asked together. The addons answer from a
    /// pre-scraped database in about a second; when they know the title and a debrid provider
    /// holds any of it, that is the answer and the viewer does not wait on indexers. The sweep
    /// keeps running on its own and, as with any gated sweep, its finished result is served to
    /// the next discovery of the same title, which is what a partial answer schedules.
    /// </summary>
    internal async Task<NativeSearchResult> DiscoverAsync(
        NativeMediaQuery query,
        string? definitionId,
        IReadOnlyList<string> addonUrls,
        TimeSpan addonTimeout,
        CancellationToken cancellationToken,
        IndexerSearchBudget budget
    )
    {
        var sweep = searchCoordinator.SearchAsync(query, definitionId, cancellationToken, budget);

        // A search aimed at one indexer is that indexer's answer alone.
        var useAddons = addons is not null
            && addonUrls.Count > 0
            && string.IsNullOrWhiteSpace(definitionId)
            && !string.IsNullOrWhiteSpace(query.ImdbId);
        if (!useAddons)
        {
            return await AttachDebridAvailabilityAsync(await sweep.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var answered = await addons!.SearchAsync(addonUrls, query, addonTimeout, cancellationToken).ConfigureAwait(false);
        // Torrentio and Comet largely know the same hashes; one row per release across addons.
        answered = answered with
        {
            Candidates = Aggregator.Aggregate(answered.Candidates, perSourceLimit: 100, globalLimit: 200),
        };
        var early = budget == IndexerSearchBudget.Routed && !sweep.IsCompleted && answered.Candidates.Count > 0
            ? await AttachDebridAvailabilityAsync(answered, cancellationToken).ConfigureAwait(false)
            : null;
        if (early is not null && early.Candidates.Count > 0)
        {
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "source-addons-answered",
                fields: new Dictionary<string, object?>
                {
                    ["addons"] = answered.Candidates.Count,
                    ["playable"] = early.Candidates.Count,
                    ["elapsedMs"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds),
                });
            // The sweep goes on without a waiter; its result is kept by the coordinator.
            _ = sweep.ContinueWith(
                task => logger.LogDebug(task.Exception, "The indexer sweep behind an addon answer did not complete"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            return early with { Complete = false };
        }

        var swept = await sweep.ConfigureAwait(false);
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "source-addons-merged",
            fields: new Dictionary<string, object?>
            {
                ["addons"] = answered.Candidates.Count,
                ["indexers"] = swept.Candidates.Count,
                ["elapsedMs"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds),
            });
        // The sweep is already aggregated; an addon row only adds a release the sweep lacks.
        var known = swept.Candidates
            .Select(candidate => candidate.InfoHash)
            .Where(hash => hash is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = new NativeSearchResult(
            [.. swept.Candidates, .. answered.Candidates.Where(candidate => candidate.InfoHash is null || !known.Contains(candidate.InfoHash))],
            [.. answered.Failures, .. swept.Failures],
            swept.Complete
        );
        return await AttachDebridAvailabilityAsync(merged, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NativeSearchResult> AttachDebridAvailabilityAsync(
        NativeSearchResult search,
        CancellationToken cancellationToken
    )
    {
        var hashes = search.Candidates
            .Select(candidate => CardigannResultNormalizer.NormalizeInfoHash(candidate.InfoHash))
            .Where(hash => hash is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hashes.Length == 0)
        {
            return search with
            {
                Candidates = search.Candidates.Select(candidate => candidate with { Playable = candidate.Kind == "http" }).ToList(),
            };
        }

        var lookup = await debrid.CheckAvailabilityAsync(hashes, cancellationToken).ConfigureAwait(false);

        var failures = search.Failures.ToList();
        failures.AddRange(lookup.Providers.Select(check => check.Failure).OfType<NativeSourceFailure>());
        var candidates = search.Candidates
            .Select(candidate => AttachAvailability(candidate, lookup))
            // Direct HTTP releases remain eligible. Torrent releases must be cached now.
            .Where(candidate => candidate.Kind == "http" || candidate.Playable)
            .OrderByDescending(candidate => candidate.Kind == "http")
            .ThenByDescending(candidate => candidate.Availability?.Count(item => item.Cached) ?? 0)
            .ThenByDescending(candidate => candidate.Seeders ?? -1)
            .ThenByDescending(candidate => candidate.SizeBytes ?? -1)
            .ToList();
        return new NativeSearchResult(candidates, failures, search.Complete);
    }

    /// <summary>
    /// Resolves one already-known release without searching for it again.
    /// Used when the caller found the release earlier and only needs a
    /// playable URL for it.
    /// </summary>
    public async Task<IReadOnlyList<NativePreparedStream>> ResolveCandidateAsync(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    )
    {
        if (NebulaBridgePlugin.Instance?.Configuration.EnableNativeAggregation != true)
        {
            return [];
        }

        // The release was cached when it was found, but that can lapse, so
        // confirm availability before handing it to the player.
        var confirmed = await AttachDebridAvailabilityAsync(
                new NativeSearchResult([candidate], []),
                cancellationToken
            )
            .ConfigureAwait(false);

        return await ResolveCandidatesAsync(confirmed, query, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<NativeResolveResult> ResolveAsync(
        NativeMediaQuery query,
        CancellationToken cancellationToken,
        IndexerSearchBudget budget = IndexerSearchBudget.Routed
    )
    {
        if (NebulaBridgePlugin.Instance?.Configuration.EnableNativeAggregation != true)
        {
            return new NativeResolveResult([]);
        }

        var result = await SearchAsync(query, null, cancellationToken, budget).ConfigureAwait(false);
        var streams = await ResolveCandidatesAsync(result, query, cancellationToken)
            .ConfigureAwait(false);
        return new NativeResolveResult(streams, result.Complete);
    }

    private async Task<IReadOnlyList<NativePreparedStream>> ResolveCandidatesAsync(
        NativeSearchResult result,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    )
    {
        var streams = new List<NativePreparedStream>();
        var configuredLimit = NebulaBridgePlugin.Instance?.Configuration.NativeResolvedStreamLimit ?? 10;
        var streamLimit = Math.Clamp(configuredLimit, 1, 20);
        foreach (var candidate in result.Candidates)
        {
            if (candidate.Kind == "http")
            {
                foreach (var resolver in _resolvers)
                {
                    var resolved = await resolver
                        .ResolveAsync(candidate, query, cancellationToken)
                        .ConfigureAwait(false);
                    if (resolved is null)
                    {
                        continue;
                    }

                    streams.Add(
                        new NativePreparedStream(
                            resolved.SourceId,
                            resolved.Name,
                            resolved.SizeBytes,
                            resolved.Filename,
                            DirectUrl: resolved.Url
                        )
                    );
                    break;
                }
            }
            else
            {
                var prepared = PrepareDebridStream(candidate, query);
                if (prepared is not null)
                {
                    streams.Add(prepared);
                }
            }

            if (streams.Count >= streamLimit)
            {
                break;
            }
        }

        return streams;
    }

    /// <summary>
    /// Release ranking already chose the media; this only chooses transport. The
    /// highest-priority healthy provider holding the release serves it, the others are kept
    /// as alternates pinned to the exact file so a later failover can never change the bytes.
    /// </summary>
    internal NativePreparedStream? PrepareDebridStream(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query
    )
    {
        var route = debrid.SelectRoute(candidate);
        if (route is null)
        {
            return null;
        }

        var provider = route.Primary;
        var selection = DebridMediaFileSelector.SelectWithDiagnostics(route.Availability.Files, query);
        var file = selection.File;
        if (file is null)
        {
            logger.LogWarning(
                "Rejected cached candidate {CandidateTitle} from {SourceId}: {Reason} — {Message}",
                candidate.Title,
                candidate.SourceId,
                selection.Reason,
                selection.Message
            );
            return null;
        }

        var pinned = DebridFileIdentity.TryCreate(candidate.InfoHash, file.Name, file.SizeBytes ?? 0);
        var alternates = route.Alternates
            .Where(alternate => AlternateHoldsFile(candidate, alternate.Id, pinned))
            .Select(alternate => alternate.Id)
            .ToList();

        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Debug,
            "debrid-route-selected",
            fields: new Dictionary<string, object?>
            {
                ["candidate"] = candidate.SourceId,
                ["infoHash"] = candidate.InfoHash,
                ["file"] = pinned?.FilePath ?? file.Name,
                ["length"] = file.SizeBytes,
                ["chosen"] = provider.Id,
                ["alternates"] = string.Join(",", alternates),
            });

        return new NativePreparedStream(
            $"debrid:{candidate.SourceId}",
            candidate.Title,
            file.SizeBytes ?? candidate.SizeBytes,
            Path.GetFileName(file.Name),
            DebridRequest: new DebridPlaybackRequest(provider.Id, candidate, query, alternates, pinned)
        );
    }

    /// <summary>
    /// An alternate is retained only when it demonstrably holds the same file (same info hash and
    /// length, path equal up to the provider's root-folder convention) or has not enumerated files
    /// yet; identity is re-verified on resolve.
    /// </summary>
    private static bool AlternateHoldsFile(
        NativeReleaseCandidate candidate,
        string providerId,
        DebridFileIdentity? pinned
    )
    {
        var availability = candidate.Availability?.FirstOrDefault(item =>
            string.Equals(item.Provider, providerId, StringComparison.OrdinalIgnoreCase));
        if (availability is null || !availability.Cached)
            return false;
        if (pinned is null || availability.Files.Count == 0)
            return true;
        return availability.Files.Any(candidateFile =>
            DebridFileIdentity.TryCreate(candidate.InfoHash, candidateFile.Name, candidateFile.SizeBytes ?? 0) is { } identity
            && identity.Matches(pinned));
    }

    private static NativeReleaseCandidate AttachAvailability(
        NativeReleaseCandidate candidate,
        DebridAvailabilityLookup lookup
    )
    {
        if (candidate.Kind == "http")
        {
            return candidate with { Playable = true };
        }

        var hash = CardigannResultNormalizer.NormalizeInfoHash(candidate.InfoHash);
        var availability = lookup.For(hash);
        return candidate with
        {
            InfoHash = hash,
            Availability = availability,
            Playable = availability.Any(item => item.Cached),
        };
    }
}
