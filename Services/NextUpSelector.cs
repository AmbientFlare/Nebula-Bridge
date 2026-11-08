namespace NebulaBridge.Services;

/// <summary>One completed play of an episode, with when it was last watched if known.</summary>
public readonly record struct EpisodeWatch(int Season, int Episode, DateTimeOffset? WatchedAt);

/// <summary>
/// Frontier-based next-up selection. The frontier is the most recently watched episode by
/// timestamp, so a one-off rewatch of an old episode does not drag it backward and seasons
/// watched elsewhere without a recorded play do not pull next-up back to their first gap. Next
/// up is the first aired, unwatched episode after the frontier in season/episode order; a show
/// with nothing watched starts at its first aired episode; a show with nothing unwatched after
/// the frontier has no next-up.
/// </summary>
public static class NextUpSelector
{
    public static StremioMeta? Select(
        IEnumerable<StremioMeta> episodes,
        IReadOnlyCollection<EpisodeWatch> watched,
        DateTime utcNow
    ) =>
        Select(
            episodes,
            item => item.Season,
            item => item.Episode ?? item.Number,
            item => item.GetPremiereDate(),
            watched,
            utcNow);

    /// <summary>
    /// The same selection over any episode shape. An episode with no air date is treated as
    /// unaired when <paramref name="requireAirDate"/> is set (metadata from a provider, where a
    /// missing date means not yet scheduled) and as aired otherwise (library items, where the
    /// stub simply may not carry one).
    /// </summary>
    public static T? Select<T>(
        IEnumerable<T> episodes,
        Func<T, int?> season,
        Func<T, int?> episode,
        Func<T, DateTime?> aired,
        IReadOnlyCollection<EpisodeWatch> watched,
        DateTime utcNow,
        bool requireAirDate = true
    )
        where T : class
    {
        var ordered = Aired(episodes, season, episode, aired, utcNow, requireAirDate);
        if (ordered.Count == 0)
            return null;

        var watchedKeys = watched.Select(Key).ToHashSet();
        var frontier = Frontier(watched);

        foreach (var (key, item) in ordered)
        {
            if (frontier is { } f && Compare(key, f) <= 0)
                continue;
            if (!watchedKeys.Contains(key))
                return item;
        }

        return null;
    }

    /// <summary>
    /// The <paramref name="count"/> aired episodes that follow <paramref name="current"/> in
    /// season/episode order, for warming discovery ahead of a viewer.
    /// </summary>
    public static IReadOnlyList<T> Following<T>(
        IEnumerable<T> episodes,
        T current,
        int count,
        Func<T, int?> season,
        Func<T, int?> episode,
        Func<T, DateTime?> aired,
        DateTime utcNow,
        bool requireAirDate = false
    )
        where T : class
    {
        if (count <= 0 || season(current) is not { } s || episode(current) is not { } e)
            return [];

        var position = (s, e);
        return Aired(episodes, season, episode, aired, utcNow, requireAirDate)
            .Where(entry => Compare(entry.Key, position) > 0)
            .Take(count)
            .Select(entry => entry.Item)
            .ToList();
    }

    private static List<((int Season, int Episode) Key, T Item)> Aired<T>(
        IEnumerable<T> episodes,
        Func<T, int?> season,
        Func<T, int?> episode,
        Func<T, DateTime?> aired,
        DateTime utcNow,
        bool requireAirDate
    ) =>
        episodes
            .Select(item => (Season: season(item), Episode: episode(item), Item: item))
            .Where(entry => entry.Season is > 0 && entry.Episode is > 0)
            .Where(entry => aired(entry.Item) is { } airDate ? airDate <= utcNow : !requireAirDate)
            .Select(entry => ((entry.Season!.Value, entry.Episode!.Value), entry.Item))
            .OrderBy(entry => entry.Item1.Item1)
            .ThenBy(entry => entry.Item1.Item2)
            .ToList();

    /// <summary>
    /// The most recently watched episode. Plays without a timestamp rank below any with one;
    /// among those, the highest episode number stands in for recency.
    /// </summary>
    internal static (int Season, int Episode)? Frontier(IReadOnlyCollection<EpisodeWatch> watched)
    {
        if (watched.Count == 0)
            return null;

        var top = watched
            .OrderByDescending(item => item.WatchedAt.HasValue)
            .ThenByDescending(item => item.WatchedAt)
            .ThenByDescending(item => item.Season)
            .ThenByDescending(item => item.Episode)
            .First();
        return (top.Season, top.Episode);
    }

    private static (int Season, int Episode) Key(EpisodeWatch watch) => (watch.Season, watch.Episode);

    private static int Compare((int Season, int Episode) a, (int Season, int Episode) b)
    {
        var bySeason = a.Season.CompareTo(b.Season);
        return bySeason != 0 ? bySeason : a.Episode.CompareTo(b.Episode);
    }
}
