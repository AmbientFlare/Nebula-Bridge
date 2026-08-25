using Microsoft.Extensions.Logging;

namespace NebulaBridge.NativeSources;

public sealed class NativeSourcePipeline(
    CardigannSearchCoordinator searchCoordinator,
    IEnumerable<IStreamResolver> resolvers,
    IEnumerable<IDebridProvider> debridProviders,
    ILogger<NativeSourcePipeline> logger
)
{
    private readonly IReadOnlyList<IStreamResolver> _resolvers = resolvers.ToList();
    private readonly IReadOnlyList<IDebridProvider> _debridProviders = debridProviders.ToList();

    public async Task<NativeSearchResult> SearchAsync(
        NativeMediaQuery query,
        string? definitionId,
        CancellationToken cancellationToken
    )
    {
        if (NebulaBridgePlugin.Instance?.Configuration.EnableNativeScraper != true)
        {
            return new NativeSearchResult([], []);
        }

        var search = await searchCoordinator
            .SearchAsync(query, definitionId, cancellationToken)
            .ConfigureAwait(false);
        return await AttachDebridAvailabilityAsync(search, cancellationToken).ConfigureAwait(false);
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
            return new NativeSearchResult(
                search.Candidates.Select(candidate => candidate with { Playable = candidate.Kind == "http" }).ToList(),
                search.Failures
            );
        }

        var providers = _debridProviders.Where(provider => provider.Enabled).ToList();
        var checks = await Task.WhenAll(
                providers.Select(async provider =>
                    (Provider: provider, Result: await provider
                        .CheckCachedAsync(hashes, cancellationToken)
                        .ConfigureAwait(false)))
            )
            .ConfigureAwait(false);

        var failures = search.Failures.ToList();
        failures.AddRange(checks.Select(check => check.Result.Failure).OfType<NativeSourceFailure>());
        var candidates = search.Candidates
            .Select(candidate => AttachAvailability(candidate, checks))
            // Direct HTTP releases remain eligible. Torrent releases must be cached now.
            .Where(candidate => candidate.Kind == "http" || candidate.Playable)
            .OrderByDescending(candidate => candidate.Kind == "http")
            .ThenByDescending(candidate => candidate.Availability?.Count(item => item.Cached) ?? 0)
            .ThenByDescending(candidate => candidate.Seeders ?? -1)
            .ThenByDescending(candidate => candidate.SizeBytes ?? -1)
            .ToList();
        return new NativeSearchResult(candidates, failures);
    }

    public async Task<IReadOnlyList<NativePreparedStream>> ResolveAsync(
        NativeMediaQuery query,
        CancellationToken cancellationToken
    )
    {
        if (NebulaBridgePlugin.Instance?.Configuration.EnableNativeAggregation != true)
        {
            return [];
        }

        var result = await SearchAsync(query, null, cancellationToken).ConfigureAwait(false);
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

    internal NativePreparedStream? PrepareDebridStream(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query
    )
    {
        var cached = candidate.Availability?.FirstOrDefault(item => item.Cached);
        var provider = cached is null
            ? null
            : _debridProviders.FirstOrDefault(item => item.Id == cached.Provider);
        if (cached is null || provider is null)
        {
            return null;
        }

        var selection = DebridMediaFileSelector.SelectWithDiagnostics(cached.Files, query);
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

        return new NativePreparedStream(
            $"{provider.Id}:{candidate.SourceId}",
            $"{candidate.Title} · {provider.Name}",
            file.SizeBytes ?? candidate.SizeBytes,
            Path.GetFileName(file.Name),
            DebridRequest: new DebridPlaybackRequest(provider.Id, candidate, query)
        );
    }

    private static NativeReleaseCandidate AttachAvailability(
        NativeReleaseCandidate candidate,
        IEnumerable<(IDebridProvider Provider, DebridCacheCheckResult Result)> checks
    )
    {
        if (candidate.Kind == "http")
        {
            return candidate with { Playable = true };
        }

        var hash = CardigannResultNormalizer.NormalizeInfoHash(candidate.InfoHash);
        var availability = hash is null
            ? []
            : checks
                .Select(check => check.Result.Availability.GetValueOrDefault(hash))
                .OfType<DebridAvailability>()
                .ToList();
        return candidate with
        {
            InfoHash = hash,
            Availability = availability,
            Playable = availability.Any(item => item.Cached),
        };
    }
}
