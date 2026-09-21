using System.Collections.Concurrent;
using System.Diagnostics;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.NativeSources;

/// <summary>
/// The single place that knows more than one debrid provider exists. Owns ordering by admin
/// priority, health gating, bounded availability fan-out, short-lived availability caching
/// and transport (route) selection for a chosen release. Provider implementations never see
/// each other; playback and acquisition code only ever asks this class for a provider by id
/// or for the ordered routes able to serve a release.
/// </summary>
public sealed class DebridProviderOrchestrator
{
    private const int DefaultPerProviderConcurrency = 2;
    private static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultAvailabilityCacheTtl = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IDebridProvider> _providers;
    private readonly IDebridProviderSettingsProvider _settings;
    private readonly DebridProviderHealthTracker _health;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedAvailability> _availabilityCache = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _providerTimeout;
    private readonly TimeSpan _cacheTtl;

    public DebridProviderOrchestrator(
        IEnumerable<IDebridProvider> providers,
        IDebridProviderSettingsProvider settings,
        DebridProviderHealthTracker health
    )
        : this(providers, settings, health, () => DateTimeOffset.UtcNow, DefaultProviderTimeout, DefaultAvailabilityCacheTtl)
    { }

    public DebridProviderOrchestrator(
        IEnumerable<IDebridProvider> providers,
        IDebridProviderSettingsProvider settings,
        DebridProviderHealthTracker health,
        Func<DateTimeOffset> clock,
        TimeSpan providerTimeout,
        TimeSpan availabilityCacheTtl
    )
    {
        _providers = DedupeById(providers);
        _settings = settings;
        _health = health;
        _clock = clock;
        _providerTimeout = providerTimeout;
        _cacheTtl = availabilityCacheTtl;
    }

    /// <summary>
    /// Test/legacy convenience: priorities follow registration order and every registered
    /// provider that reports itself enabled and configured participates.
    /// </summary>
    public static DebridProviderOrchestrator FromProviders(IEnumerable<IDebridProvider> providers)
    {
        var list = DedupeById(providers);
        return new DebridProviderOrchestrator(list, new RegistrationOrderSettings(list), new DebridProviderHealthTracker());
    }

    public DebridProviderHealthTracker Health => _health;

    public IReadOnlyList<IDebridProvider> RegisteredProviders => _providers;

    /// <summary>
    /// Providers that are enabled and configured, ordered by admin priority (lower first), then id.
    /// Health is deliberately not applied here: callers decide whether to skip unhealthy providers
    /// and the admin view needs the full picture.
    /// </summary>
    public IReadOnlyList<IDebridProvider> GetActiveProviders()
    {
        var ranked = _providers
            .Select(p => (Provider: p, Settings: _settings.GetSettings(p.Id)))
            .Where(x => x.Settings.Configured && x.Provider.Enabled && x.Provider.Configured)
            .OrderBy(x => x.Settings.Priority)
            .ThenBy(x => x.Provider.Id, StringComparer.Ordinal)
            .ToList();
        LogProviderSet(ranked.Select(x => $"{x.Provider.Id}@{x.Settings.Priority}"));
        return ranked.Select(x => x.Provider).ToList();
    }

    private string? _lastProviderSet;

    /// <summary>Records the enabled provider set once per change so the log shows which providers were in play.</summary>
    private void LogProviderSet(IEnumerable<string> ranked)
    {
        var signature = string.Join(",", ranked);
        if (string.Equals(signature, Interlocked.Exchange(ref _lastProviderSet, signature), StringComparison.Ordinal))
            return;
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "debrid-providers",
            fields: new Dictionary<string, object?>
            {
                ["registered"] = string.Join(",", _providers.Select(p => p.Id)),
                ["active"] = signature.Length == 0 ? "none" : signature,
            });
    }

    public IDebridProvider? TryGetProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return null;
        return _providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the provider is registered, enabled, configured and not backing off.</summary>
    public bool IsUsable(string? providerId, out IDebridProvider? provider)
    {
        provider = TryGetProvider(providerId);
        if (provider is null)
            return false;
        var settings = _settings.GetSettings(provider.Id);
        return settings.Configured && provider.Enabled && provider.Configured && _health.IsAvailable(provider.Id);
    }

    public bool IsUsable(string? providerId) => IsUsable(providerId, out _);

    public IReadOnlyList<DebridProviderDescription> DescribeProviders()
    {
        var active = GetActiveProviders().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _providers
            .Select(p =>
            {
                var settings = _settings.GetSettings(p.Id);
                var health = _health.Get(p.Id);
                return new DebridProviderDescription(
                    p.Id,
                    p.Name,
                    settings.Enabled,
                    settings.Priority,
                    !string.IsNullOrWhiteSpace(settings.Credential),
                    DebridProviderCredentials.IsEnvironmentManaged(p.Id),
                    active.Contains(p.Id),
                    health.Health,
                    health.UntilUtc,
                    health.LastReason,
                    p.Capabilities);
            })
            .OrderBy(d => d.Priority)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Queries every usable provider for the given hashes with bounded per-provider concurrency,
    /// a per-provider timeout and a short cache of definitive answers. One provider failing,
    /// timing out or being rate limited never suppresses another provider's result.
    /// </summary>
    public async Task<DebridAvailabilityLookup> CheckAvailabilityAsync(
        IReadOnlyCollection<string> infoHashes,
        CancellationToken cancellationToken
    )
    {
        var hashes = infoHashes
            .Select(CardigannResultNormalizer.NormalizeInfoHash)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var providers = GetActiveProviders()
            .Where(p => p.Capabilities.HasFlag(DebridProviderCapabilities.CachedAvailability))
            .ToList();
        var perProvider = new List<DebridProviderLookupResult>();
        if (hashes.Count == 0 || providers.Count == 0)
            return new DebridAvailabilityLookup(perProvider, TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        var tasks = providers.Select(p => QueryProviderAsync(p, hashes, cancellationToken)).ToList();
        foreach (var task in tasks)
            perProvider.Add(await task.ConfigureAwait(false));
        stopwatch.Stop();

        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "debrid-availability",
            fields: new Dictionary<string, object?>
            {
                ["hashes"] = hashes.Count,
                ["durationMs"] = stopwatch.ElapsedMilliseconds,
                ["providers"] = string.Join(
                    ",",
                    perProvider.Select(r => $"{r.ProviderId}:{r.Cached}/{r.Queried}:{r.Outcome}{(r.Skipped is null ? "" : $"({r.Skipped})")}")),
            });

        return new DebridAvailabilityLookup(perProvider, stopwatch.Elapsed);
    }

    /// <summary>
    /// Chooses the transport for an already-ranked release: the highest-priority usable provider
    /// that reports the release cached becomes primary; the rest are retained as alternates.
    /// The release itself is never changed here.
    /// </summary>
    public DebridRouteSelection? SelectRoute(NativeReleaseCandidate candidate)
    {
        if (candidate.Availability is null || candidate.Availability.Count == 0)
            return null;
        var ordered = new List<(IDebridProvider Provider, DebridAvailability Availability)>();
        foreach (var provider in GetActiveProviders())
        {
            if (!_health.IsAvailable(provider.Id))
                continue;
            var availability = candidate.Availability.FirstOrDefault(a =>
                a.Cached
                && a.Status == DebridAvailabilityStatus.Cached
                && string.Equals(a.Provider, provider.Id, StringComparison.OrdinalIgnoreCase));
            if (availability is null)
                continue;
            ordered.Add((provider, availability));
        }

        if (ordered.Count == 0)
            return null;
        return new DebridRouteSelection(
            ordered[0].Provider,
            ordered[0].Availability,
            ordered.Skip(1).Select(x => x.Provider).ToList());
    }

    /// <summary>
    /// Resolves playback via the primary route and, if it fails or returns a different file,
    /// each alternate in order. A fallback stream is accepted only when its handle proves the
    /// same infohash, path and length as the pinned file (or the primary's answer).
    /// </summary>
    public async Task<DebridRoutedPlayback> ResolvePlaybackAsync(
        DebridPlaybackRequest request,
        CancellationToken cancellationToken
    )
    {
        var attempts = new List<DebridRouteAttempt>();
        DebridFileIdentity? expected = request.PinnedFile;
        foreach (var providerId in request.Routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUsable(providerId, out var provider) || provider is null)
            {
                attempts.Add(new DebridRouteAttempt(providerId, "skipped", provider is null ? "unregistered" : "unavailable"));
                continue;
            }

            DebridPlaybackResult result;
            try
            {
                result = await provider
                    .ResolvePlaybackAsync(request.Candidate, request.Query, expected, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _health.ReportFailure(provider.Id, "resolve_exception");
                attempts.Add(new DebridRouteAttempt(provider.Id, "failed", ex.GetType().Name));
                continue;
            }

            if (result.Stream is null)
            {
                var reason = result.Failure?.Reason ?? "failed";
                _health.ReportFailure(provider.Id, reason);
                attempts.Add(new DebridRouteAttempt(provider.Id, "failed", reason));
                continue;
            }

            var identity = result.Stream.CompletedFileHandle?.ContentIdentity;
            if (expected is not null && identity is not null && !expected.Matches(identity))
            {
                attempts.Add(new DebridRouteAttempt(provider.Id, "rejected", "different_file"));
                continue;
            }

            if (expected is not null && identity is null && attempts.Count > 0)
            {
                // A fallback route must prove it serves the same bytes; an unproven handle after
                // a failed primary is refused rather than risk splicing a different file.
                attempts.Add(new DebridRouteAttempt(provider.Id, "rejected", "unverifiable_identity"));
                continue;
            }

            _health.ReportSuccess(provider.Id);
            attempts.Add(new DebridRouteAttempt(provider.Id, "resolved", null));
            if (attempts.Count > 1)
            {
                NebulaBridgeFileLog.Write(
                    NebulaLogVerbosity.Information,
                    "debrid-failover",
                    fields: new Dictionary<string, object?>
                    {
                        ["candidate"] = request.Candidate.SourceId,
                        ["infoHash"] = request.Candidate.InfoHash,
                        ["chosen"] = provider.Id,
                        ["attempts"] = FormatAttempts(attempts),
                    });
            }

            return new DebridRoutedPlayback(result.Stream, provider, attempts);
        }

        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "debrid-failover",
            fields: new Dictionary<string, object?>
            {
                ["candidate"] = request.Candidate.SourceId,
                ["infoHash"] = request.Candidate.InfoHash,
                ["chosen"] = null,
                ["attempts"] = FormatAttempts(attempts),
            });
        return new DebridRoutedPlayback(null, null, attempts);
    }

    public static string FormatAttempts(IEnumerable<DebridRouteAttempt> attempts) =>
        string.Join(",", attempts.Select(a => $"{a.ProviderId}:{a.Outcome}{(a.Reason is null ? "" : $"({a.Reason})")}"));

    private async Task<DebridProviderLookupResult> QueryProviderAsync(
        IDebridProvider provider,
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken
    )
    {
        var now = _clock();
        var results = new Dictionary<string, DebridAvailability>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (var hash in hashes)
        {
            if (_availabilityCache.TryGetValue(CacheKey(provider.Id, hash), out var cached) && cached.ExpiresUtc > now)
                results[hash] = cached.Availability;
            else
                pending.Add(hash);
        }

        if (pending.Count == 0)
            return new DebridProviderLookupResult(provider.Id, results, "cached", null, hashes.Count, results.Values.Count(a => a.Cached));

        var health = _health.Get(provider.Id);
        if (health.Health != DebridProviderHealth.Healthy)
        {
            foreach (var hash in pending)
                results[hash] = Unknown(provider.Id, hash, health.Health);
            return new DebridProviderLookupResult(provider.Id, results, "skipped", health.Health.ToString(), hashes.Count, results.Values.Count(a => a.Cached));
        }

        var gate = _gates.GetOrAdd(provider.Id, _ => new SemaphoreSlim(DefaultPerProviderConcurrency, DefaultPerProviderConcurrency));
        var stopwatch = Stopwatch.StartNew();
        string outcome;
        string? skipped = null;
        NativeSourceFailure? failure = null;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_providerTimeout);
                var response = await provider.CheckCachedAsync(pending, timeout.Token).ConfigureAwait(false);
                DebridAvailabilityStatus? status = response.Failure is null ? null : StatusFor(response.Failure.Reason);
                foreach (var hash in pending)
                {
                    if (response.Availability.TryGetValue(hash, out var availability))
                    {
                        results[hash] = availability;
                        if (availability.Status is DebridAvailabilityStatus.Cached or DebridAvailabilityStatus.NotCached)
                            _availabilityCache[CacheKey(provider.Id, hash)] = new CachedAvailability(availability, now + _cacheTtl);
                    }
                    else
                    {
                        results[hash] = new DebridAvailability(provider.Id, false, [])
                        {
                            Status = status ?? DebridAvailabilityStatus.Unknown,
                        };
                    }
                }

                if (response.Failure is null)
                {
                    _health.ReportSuccess(provider.Id);
                    outcome = "ok";
                }
                else
                {
                    _health.ReportFailure(provider.Id, response.Failure);
                    outcome = "error";
                    skipped = response.Failure.Reason;
                    failure = response.Failure;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _health.ReportFailure(provider.Id, "timeout");
            foreach (var hash in pending)
                results[hash] = Unknown(provider.Id, hash, DebridProviderHealth.TemporarilyUnavailable);
            outcome = "timeout";
            failure = new NativeSourceFailure(provider.Id, "The debrid cache check timed out.", provider.Name, "cache", "timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _health.ReportFailure(provider.Id, "api_error");
            foreach (var hash in pending)
                results[hash] = new DebridAvailability(provider.Id, false, []) { Status = DebridAvailabilityStatus.ProviderError };
            outcome = "error";
            skipped = ex.GetType().Name;
            failure = new NativeSourceFailure(provider.Id, "The debrid cache check failed.", provider.Name, "cache", ex.GetType().Name.ToLowerInvariant());
        }

        stopwatch.Stop();
        TrimCache(now);
        return new DebridProviderLookupResult(provider.Id, results, outcome, skipped, hashes.Count, results.Values.Count(a => a.Cached), stopwatch.Elapsed, failure);
    }

    private static DebridAvailability Unknown(string providerId, string hash, DebridProviderHealth health) =>
        new(providerId, false, [])
        {
            Status = health == DebridProviderHealth.RateLimited
                ? DebridAvailabilityStatus.RateLimited
                : DebridAvailabilityStatus.Unknown,
        };

    private static DebridAvailabilityStatus StatusFor(string reason) => reason switch
    {
        "rate_limited" => DebridAvailabilityStatus.RateLimited,
        "timeout" => DebridAvailabilityStatus.Unknown,
        _ => DebridAvailabilityStatus.ProviderError,
    };

    private void TrimCache(DateTimeOffset now)
    {
        if (_availabilityCache.Count < 2048)
            return;
        foreach (var pair in _availabilityCache)
        {
            if (pair.Value.ExpiresUtc <= now)
                _availabilityCache.TryRemove(pair.Key, out _);
        }
    }

    private static string CacheKey(string providerId, string hash) => $"{providerId.ToLowerInvariant()}\n{hash}";

    private static IReadOnlyList<IDebridProvider> DedupeById(IEnumerable<IDebridProvider> providers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<IDebridProvider>();
        foreach (var provider in providers)
        {
            if (seen.Add(provider.Id))
                list.Add(provider);
        }

        return list;
    }

    private sealed record CachedAvailability(DebridAvailability Availability, DateTimeOffset ExpiresUtc);

    private sealed class RegistrationOrderSettings(IReadOnlyList<IDebridProvider> providers) : IDebridProviderSettingsProvider
    {
        public DebridProviderSettings GetSettings(string providerId)
        {
            for (var i = 0; i < providers.Count; i++)
            {
                if (string.Equals(providers[i].Id, providerId, StringComparison.OrdinalIgnoreCase))
                    return new DebridProviderSettings(providers[i].Id, providers[i].Enabled, i + 1, providers[i].Configured ? "registered" : string.Empty);
            }

            return new DebridProviderSettings(providerId, false, DebridProviderConfig.MaxPriority, string.Empty);
        }
    }
}

public sealed record DebridProviderDescription(
    string Id,
    string Name,
    bool Enabled,
    int Priority,
    bool HasCredential,
    bool CredentialFromEnvironment,
    bool Active,
    DebridProviderHealth Health,
    DateTimeOffset? HealthUntilUtc,
    string? LastFailureReason,
    DebridProviderCapabilities Capabilities
);

public sealed record DebridProviderLookupResult(
    string ProviderId,
    IReadOnlyDictionary<string, DebridAvailability> Availability,
    string Outcome,
    string? Skipped,
    int Queried,
    int Cached,
    TimeSpan Duration = default,
    NativeSourceFailure? Failure = null
);

public sealed record DebridAvailabilityLookup(
    IReadOnlyList<DebridProviderLookupResult> Providers,
    TimeSpan Duration
)
{
    /// <summary>All availability rows for one hash, one per provider that answered.</summary>
    public IReadOnlyList<DebridAvailability> For(string? infoHash)
    {
        var normalized = CardigannResultNormalizer.NormalizeInfoHash(infoHash);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];
        var rows = new List<DebridAvailability>();
        foreach (var provider in Providers)
        {
            if (provider.Availability.TryGetValue(normalized, out var availability))
                rows.Add(availability);
        }

        return rows;
    }
}

public sealed record DebridRouteSelection(
    IDebridProvider Primary,
    DebridAvailability Availability,
    IReadOnlyList<IDebridProvider> Alternates
);

public sealed record DebridRouteAttempt(string ProviderId, string Outcome, string? Reason);

public sealed record DebridRoutedPlayback(
    NativeResolvedStream? Stream,
    IDebridProvider? Provider,
    IReadOnlyList<DebridRouteAttempt> Attempts
);
