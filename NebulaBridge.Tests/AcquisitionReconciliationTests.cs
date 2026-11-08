using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class AcquisitionReconciliationTests
{
    [Fact]
    public void MovieMatchingRequiresTrustedProviderIdentityRatherThanFilename()
    {
        var virtualMovie = new Movie { Name = "Same Name", Path = "/virtual/same-name.mkv" };
        var localMovie = new Movie { Name = "Same Name", Path = "/local/same-name.mkv" };
        Assert.False(AcquisitionReconciliationService.MatchesMovie(virtualMovie, localMovie));
        virtualMovie.SetProviderId(MetadataProvider.Tmdb, "123");
        localMovie.SetProviderId(MetadataProvider.Tmdb, "123");
        Assert.True(AcquisitionReconciliationService.MatchesMovie(virtualMovie, localMovie));
    }

    [Fact]
    public void EpisodeMatchingRequiresSeriesIdentityAndNumbering()
    {
        var sourceSeries = new Series { Name = "Series" };
        sourceSeries.SetProviderId(MetadataProvider.Tvdb, "12");
        var localSeries = new Series { Name = "Series" };
        localSeries.SetProviderId(MetadataProvider.Tvdb, "12");
        var source = new Episode { ParentIndexNumber = 1, IndexNumber = 2 };
        var local = new Episode { ParentIndexNumber = 1, IndexNumber = 2 };
        Assert.True(AcquisitionReconciliationService.MatchesEpisode(source, sourceSeries, local, localSeries));
        local.IndexNumber = 3;
        Assert.False(AcquisitionReconciliationService.MatchesEpisode(source, sourceSeries, local, localSeries));
    }

    [Fact]
    public void NewlyScannedEpisodeWithoutSeriesIdentityWaitsInsteadOfThrowing()
    {
        Assert.False(AcquisitionReconciliationService.HasUsableSeriesIdentity(new Episode()));
        Assert.True(AcquisitionReconciliationService.HasUsableSeriesIdentity(
            new Episode { SeriesId = Guid.NewGuid() }));
    }

    [Fact]
    public void ReconciledLocalEpisodeSuppressesOnlyItsOwnVirtualRow()
    {
        var itemId = Guid.NewGuid();
        var reconciled = new AcquisitionJob(
            Guid.NewGuid(),
            itemId,
            "torbox",
            "source",
            "episode.mkv",
            AcquisitionJobState.Imported,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Imported: true,
            FinalPath: "/media/episode.mkv",
            LocalItemId: Guid.NewGuid(),
            ReconciliationState: AcquisitionReconciliationState.Reconciled);

        Assert.True(NebulaBridgeManager.ShouldSuppressVirtualEpisode(itemId, [reconciled]));
        Assert.False(NebulaBridgeManager.ShouldSuppressVirtualEpisode(Guid.NewGuid(), [reconciled]));
        Assert.False(NebulaBridgeManager.ShouldSuppressVirtualEpisode(
            itemId,
            [reconciled with { ReconciliationState = AcquisitionReconciliationState.Pending }]));
    }

    [Fact]
    public void ContradictoryMovieIdentityIsRejected()
    {
        var source = new Movie();
        source.SetProviderId(MetadataProvider.Tmdb, "1");
        source.SetProviderId(MetadataProvider.Imdb, "tt1");
        var local = new Movie();
        local.SetProviderId(MetadataProvider.Tmdb, "1");
        local.SetProviderId(MetadataProvider.Imdb, "tt2");
        Assert.False(AcquisitionReconciliationService.MatchesMovie(source, local));
    }

    [Fact]
    public void ContradictorySeriesIdentityIsRejected()
    {
        var sourceSeries = new Series();
        sourceSeries.SetProviderId(MetadataProvider.Tvdb, "1");
        sourceSeries.SetProviderId(MetadataProvider.Tmdb, "2");
        var localSeries = new Series();
        localSeries.SetProviderId(MetadataProvider.Tvdb, "1");
        localSeries.SetProviderId(MetadataProvider.Tmdb, "3");
        var source = new Episode { ParentIndexNumber = 1, IndexNumber = 1 };
        var local = new Episode { ParentIndexNumber = 1, IndexNumber = 1 };
        Assert.False(AcquisitionReconciliationService.MatchesEpisode(source, sourceSeries, local, localSeries));
    }

    [Fact]
    public void ReconciliationProofIsBoundToFinalPath()
    {
        var final = Path.Combine(Path.GetTempPath(), "imported", "movie.mkv");
        Assert.True(AcquisitionReconciliationService.MatchesFinalPath(new Movie { Path = final }, final));
        Assert.False(AcquisitionReconciliationService.MatchesFinalPath(
            new Movie { Path = Path.Combine(Path.GetTempPath(), "other", "movie.mkv") }, final));
    }

    [Fact]
    public void MergeTransfersWatchedResumeAndFavoriteWithoutRegression()
    {
        var local = new UserItemData
        {
            Key = "local",
            PlaybackPositionTicks = TimeSpan.FromMinutes(10).Ticks,
            IsFavorite = false,
            Played = false,
            PlayCount = 1,
        };
        var remote = new UserItemData
        {
            Key = "remote",
            PlaybackPositionTicks = TimeSpan.FromMinutes(21).Ticks,
            IsFavorite = true,
            Played = true,
            PlayCount = 2,
        };

        Assert.True(AcquisitionReconciliationService.Merge(local, remote));
        Assert.True(local.Played);
        Assert.True(local.IsFavorite);
        Assert.Equal(2, local.PlayCount);
        // A watched local item deliberately has no actionable resume state.
        Assert.Equal(TimeSpan.FromMinutes(10).Ticks, local.PlaybackPositionTicks);
    }

    [Fact]
    public void MergeKeepsStrongerExistingLocalStateForEachUser()
    {
        var local = new UserItemData
        {
            Key = "local",
            PlaybackPositionTicks = TimeSpan.FromMinutes(30).Ticks,
            IsFavorite = true,
            Played = false,
            PlayCount = 5,
        };
        var remote = new UserItemData
        {
            Key = "remote",
            PlaybackPositionTicks = TimeSpan.FromMinutes(21).Ticks,
            IsFavorite = false,
            Played = false,
            PlayCount = 1,
        };

        Assert.False(AcquisitionReconciliationService.Merge(local, remote));
        Assert.Equal(TimeSpan.FromMinutes(30).Ticks, local.PlaybackPositionTicks);
        Assert.True(local.IsFavorite);
        Assert.Equal(5, local.PlayCount);
    }

    [Fact]
    public void EmptyDefaultUserDataDoesNotCreateStateForAnUninvolvedUser()
    {
        Assert.False(AcquisitionReconciliationService.HasPersistedState(new UserItemData { Key = string.Empty }));
        Assert.True(AcquisitionReconciliationService.HasPersistedState(new UserItemData { Key = "user-item", IsFavorite = true }));
    }
}
