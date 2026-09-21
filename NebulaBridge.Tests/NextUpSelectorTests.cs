using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class NextUpSelectorTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static StremioMeta Episode(int season, int episode, int? airedDaysAgo = 30) =>
        new()
        {
            Id = $"s{season}e{episode}",
            Season = season,
            Episode = episode,
            Name = $"S{season}E{episode}",
            Released = airedDaysAgo is { } days ? Now.AddDays(-days) : null,
        };

    private static List<StremioMeta> Show(int seasons, int perSeason) =>
        Enumerable.Range(1, seasons)
            .SelectMany(season => Enumerable.Range(1, perSeason).Select(episode => Episode(season, episode)))
            .ToList();

    [Fact]
    public void FrontierIsTheMostRecentlyWatchedEpisodeNotTheHighest()
    {
        // Seasons 4-6 were watched elsewhere and never recorded; the viewer is midway through
        // season 7. A rewatch of S02E03 last week must not drag next-up back to season 2.
        var watched = new List<EpisodeWatch>();
        for (var e = 1; e <= 10; e++)
        {
            watched.Add(new EpisodeWatch(1, e, T0.AddDays(-400 + e)));
            watched.Add(new EpisodeWatch(2, e, T0.AddDays(-300 + e)));
            watched.Add(new EpisodeWatch(3, e, T0.AddDays(-200 + e)));
        }

        for (var e = 1; e <= 4; e++)
            watched.Add(new EpisodeWatch(7, e, T0.AddDays(e)));
        watched.Add(new EpisodeWatch(2, 3, T0.AddDays(2)));

        var next = NextUpSelector.Select(Show(8, 10), watched, Now);

        Assert.NotNull(next);
        Assert.Equal((7, 5), (next.Season, next.Episode));
    }

    [Fact]
    public void NextUpSkipsWatchedEpisodesAfterTheFrontierAndUnairedOnes()
    {
        var episodes = Show(1, 4);
        episodes.Add(Episode(1, 5, airedDaysAgo: -3));
        episodes.Add(Episode(1, 6, airedDaysAgo: null));
        var watched = new List<EpisodeWatch>
        {
            new(1, 1, T0),
            new(1, 2, T0.AddDays(1)),
            new(1, 3, T0.AddDays(-5)),
        };

        var next = NextUpSelector.Select(episodes, watched, Now);

        Assert.NotNull(next);
        Assert.Equal((1, 4), (next.Season, next.Episode));
    }

    [Fact]
    public void CaughtUpShowHasNoNextUpEvenWithOldGaps()
    {
        var episodes = Show(2, 3);
        var watched = new List<EpisodeWatch>
        {
            new(1, 1, T0),
            // S01E02 was never recorded.
            new(1, 3, T0.AddDays(1)),
            new(2, 1, T0.AddDays(2)),
            new(2, 2, T0.AddDays(3)),
            new(2, 3, T0.AddDays(4)),
        };

        Assert.Null(NextUpSelector.Select(episodes, watched, Now));
    }

    [Fact]
    public void NothingWatchedStartsAtTheFirstAiredEpisode()
    {
        var episodes = Show(2, 3);
        episodes.Insert(0, Episode(0, 1));

        var next = NextUpSelector.Select(episodes, [], Now);

        Assert.NotNull(next);
        Assert.Equal((1, 1), (next.Season, next.Episode));
    }

    [Fact]
    public void WithoutTimestampsTheHighestWatchedEpisodeIsTheFrontier()
    {
        var watched = new List<EpisodeWatch> { new(1, 1, null), new(3, 2, null), new(2, 5, null) };

        var next = NextUpSelector.Select(Show(4, 6), watched, Now);

        Assert.NotNull(next);
        Assert.Equal((3, 3), (next.Season, next.Episode));
    }

    [Fact]
    public void TimestampedPlaysOutrankUntimestampedOnes()
    {
        var watched = new List<EpisodeWatch> { new(4, 6, null), new(2, 1, T0) };

        var next = NextUpSelector.Select(Show(4, 6), watched, Now);

        Assert.NotNull(next);
        Assert.Equal((2, 2), (next.Season, next.Episode));
    }

    private sealed record Stub(int? Season, int? Episode, DateTime? Aired);

    private static List<Stub> StubShow(int seasons, int perSeason, DateTime? aired = null) =>
        Enumerable.Range(1, seasons)
            .SelectMany(season => Enumerable.Range(1, perSeason).Select(episode => new Stub(season, episode, aired)))
            .ToList();

    [Fact]
    public void LibraryStubsWithoutAirDatesCountAsAiredWhenAskedTo()
    {
        var show = StubShow(1, 3);
        var watched = new[] { new EpisodeWatch(1, 1, T0) };

        Assert.Null(NextUpSelector.Select(show, s => s.Season, s => s.Episode, s => s.Aired, watched, Now));
        var next = NextUpSelector.Select(
            show, s => s.Season, s => s.Episode, s => s.Aired, watched, Now, requireAirDate: false);
        Assert.Equal(new Stub(1, 2, null), next);
    }

    [Fact]
    public void FollowingReturnsTheNextEpisodesInOrderAcrossSeasons()
    {
        var show = StubShow(2, 3);
        var current = show.Single(s => s.Season == 1 && s.Episode == 3);

        var following = NextUpSelector.Following(
            show, current, 2, s => s.Season, s => s.Episode, s => s.Aired, Now);

        Assert.Equal([new Stub(2, 1, null), new Stub(2, 2, null)], following);
    }

    [Fact]
    public void FollowingStopsAtUnairedEpisodesAndTheEndOfTheShow()
    {
        var show = StubShow(1, 3, aired: Now.AddDays(-30));
        show.Add(new Stub(1, 4, Now.AddDays(7)));
        var current = show.Single(s => s.Episode == 2);

        var following = NextUpSelector.Following(
            show, current, 3, s => s.Season, s => s.Episode, s => s.Aired, Now);

        Assert.Equal([show.Single(s => s.Episode == 3)], following);
        Assert.Empty(NextUpSelector.Following(
            show, new Stub(null, 1, null), 3, s => s.Season, s => s.Episode, s => s.Aired, Now));
    }
}
