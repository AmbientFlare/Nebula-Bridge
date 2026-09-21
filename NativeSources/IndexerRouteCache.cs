using System.Collections.Concurrent;

namespace NebulaBridge.NativeSources;

/// <summary>How an indexer host should be fetched.</summary>
internal enum IndexerRoute
{
    /// <summary>Ordinary HTTP. Cheap; roughly a second.</summary>
    Direct,

    /// <summary>Through FlareSolverr. Costs a flat ~11s and one of the solver's four slots.</summary>
    FlareSolverr,
}

/// <summary>
/// Remembers, per host, whether an ordinary HTTP fetch worked or hit a Cloudflare challenge.
///
/// The Cardigann <c>info_flaresolverr</c> flag is a static authoring hint and is wrong in both
/// directions: measured against the shipped definition set, six of thirteen flagged indexers
/// never present a challenge, while an unflagged site can turn Cloudflare on at any time. So the
/// flag is not consulted at all — the first request to a host discovers the truth and the answer
/// is cached briefly, which lets a site that changes its posture be followed automatically.
/// </summary>
internal sealed class IndexerRouteCache
{
    /// <summary>
    /// Short enough that a site toggling Cloudflare is picked up within the hour, long enough
    /// that a genuinely protected host costs one wasted direct attempt rather than one per search.
    /// </summary>
    private static readonly TimeSpan RouteTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> _routes = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly TimeProvider _time;

    internal IndexerRouteCache(TimeProvider? timeProvider = null) =>
        _time = timeProvider ?? TimeProvider.System;

    private readonly record struct Entry(IndexerRoute Route, DateTimeOffset ExpiresAt);

    /// <summary>
    /// The remembered route for <paramref name="host"/>, or null when nothing is known and the
    /// caller should try direct to find out.
    /// </summary>
    internal IndexerRoute? GetRoute(string host)
    {
        if (!_routes.TryGetValue(host, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= _time.GetUtcNow())
        {
            // Re-probe rather than trusting a stale decision.
            _routes.TryRemove(host, out _);
            return null;
        }

        return entry.Route;
    }

    /// <summary>Record that a host answered an ordinary request.</summary>
    internal void RecordDirect(string host) => Record(host, IndexerRoute.Direct);

    /// <summary>Record that a host served a Cloudflare challenge.</summary>
    internal void RecordChallenge(string host) => Record(host, IndexerRoute.FlareSolverr);

    private void Record(string host, IndexerRoute route) =>
        _routes[host] = new Entry(route, _time.GetUtcNow().Add(RouteTtl));
}
