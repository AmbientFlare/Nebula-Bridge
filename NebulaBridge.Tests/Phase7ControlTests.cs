using System.Reflection;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using NebulaBridge.Config;
using NebulaBridge.Controllers;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class Phase7ControlTests
{
    [Fact]
    public void LocalAcquisitionDefaultsOffWithSafeStorageAndLoggingDefaults()
    {
        var configuration = new PluginConfiguration();

        Assert.False(configuration.EnableLocalAcquisition);
        Assert.Equal(string.Empty, configuration.MovieImportPath);
        Assert.Equal(string.Empty, configuration.SeriesImportPath);
        Assert.Equal(3, configuration.PlaybackCacheRetentionDays);
        Assert.Equal(2048, configuration.PlaybackCacheMinimumFreeSpaceMb);
        Assert.True(configuration.EnableDedicatedLog);
        Assert.Equal(NebulaLogVerbosity.Information, configuration.DedicatedLogVerbosity);
        Assert.False(AcquisitionCoordinator.IsLocalAcquisitionEnabled(configuration));
        Assert.False(AcquisitionCoordinator.IsLocalAcquisitionEnabled(null));
    }

    [Fact]
    public void PermanentImportRootsAreSeparateFromNebulaVirtualLibraryPaths()
    {
        var configuration = new PluginConfiguration
        {
            MoviePath = "/virtual/movies",
            SeriesPath = "/virtual/series",
            MovieImportPath = "/media/movies",
            SeriesImportPath = "/media/tv",
        };

        Assert.Equal(
            "/media/movies",
            AcquisitionImportService.GetImportRoot(
                new MediaBrowser.Controller.Entities.Movies.Movie(),
                configuration));
        Assert.Equal(
            "/media/tv",
            AcquisitionImportService.GetImportRoot(
                new MediaBrowser.Controller.Entities.TV.Episode(),
                configuration));
    }

    [Theory]
    [InlineData("/data/nebulabridge/libraries/catalog", true)]
    [InlineData("/config/virtual/movies", true)]
    [InlineData("/config/virtual/series", true)]
    [InlineData("/config/local/movies", false)]
    [InlineData("/config/local/tv", false)]
    public void UserAccessClassifiesManagedAndLegacyVirtualLibrariesOnly(
        string location,
        bool expected)
    {
        var virtualPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/config/virtual/movies",
            "/config/virtual/series",
        };

        Assert.Equal(
            expected,
            BridgeLibraryService.IsManagedLocation(
                location,
                "/data/nebulabridge/libraries/",
                virtualPaths));
    }

    [Fact]
    public void AdminReviewActionsRemainElevationProtected()
    {
        var authorization = typeof(AcquisitionCacheController)
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(Policies.RequiresElevation, authorization?.Policy);
        Assert.NotNull(typeof(AcquisitionCacheController).GetMethod(nameof(AcquisitionCacheController.List)));
        Assert.NotNull(typeof(AcquisitionCacheController).GetMethod(nameof(AcquisitionCacheController.ListKept)));
        Assert.NotNull(typeof(AcquisitionCacheController).GetMethod(nameof(AcquisitionCacheController.Retain)));
        Assert.NotNull(typeof(AcquisitionCacheController).GetMethod(nameof(AcquisitionCacheController.Retry)));
        Assert.NotNull(typeof(AcquisitionCacheController).GetMethod(nameof(AcquisitionCacheController.Purge)));
    }

    [Fact]
    public void ClientRetentionStatusRequiresAuthenticationAndIsProviderNeutral()
    {
        var method = typeof(NebulaBridgeApiController)
            .GetMethod(nameof(NebulaBridgeApiController.GetAcquisitionStatus));
        Assert.NotNull(method?.GetCustomAttribute<AuthorizeAttribute>());

        var json = System.Text.Json.JsonSerializer.Serialize(new AcquisitionRetentionStatus(
            true,
            true,
            Guid.NewGuid(),
            "Queued",
            "Temporary",
            1024,
            4096,
            DateTimeOffset.UtcNow,
            false,
            true,
            true,
            false));
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"canKeepSeries\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewProjectionExposesProgressAndOnlyValidActions()
    {
        var job = new AcquisitionJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "provider",
            "source",
            "Show S01E01.mkv",
            AcquisitionJobState.Queued,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ExpectedBytes: 100,
            CachedRanges: [new(0, 24), new(50, 74)],
            SeriesId: "stremio:series",
            CompletedFileHandle: new("provider", "remote", "file", "Show S01E01.mkv", 100));

        var review = AcquisitionJobReview.From(job);

        Assert.Equal("Episode", review.MediaType);
        Assert.Equal(50, review.CachedBytes);
        Assert.Equal(50, review.Percent);
        Assert.True(review.CanKeepItem);
        Assert.True(review.CanKeepSeries);
        Assert.True(review.CanPurge);
        Assert.DoesNotContain("remote", review.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ImportedJobsAreExcludedFromActiveCacheReview()
    {
        var imported = new AcquisitionJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "provider",
            "source",
            "movie.mkv",
            AcquisitionJobState.Imported,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Retention: AcquisitionRetention.KeepItem,
            Imported: true);

        Assert.False(AcquisitionCacheController.IsActiveReviewJob(imported));
        Assert.False(AcquisitionJobReview.From(imported).CanPurge);
    }

    [Fact]
    public void KeptSeriesPolicyProjectsImportedAndPendingCounts()
    {
        var policy = new AcquisitionSeriesPolicyReview(
            "stremio:series",
            "Example Show",
            ImportedEpisodes: 3,
            PendingEpisodes: 1,
            DateTimeOffset.UtcNow);

        Assert.Equal("Example Show", policy.Title);
        Assert.Equal(3, policy.ImportedEpisodes);
        Assert.Equal(1, policy.PendingEpisodes);
    }

    [Fact]
    public void KeptEpisodeRowsIncludeSeriesAndEpisodeIdentity()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            SeriesName = "Example Show",
            ParentIndexNumber = 2,
            IndexNumber = 5,
            Name = "A Good Episode",
        };

        Assert.Equal(
            "Example Show — S02E05 A Good Episode",
            AcquisitionCacheController.FormatEpisodeTitle(episode));
    }

    [Fact]
    public void StoppingTemporaryPlaybackDoesNotPromoteOrScheduleCompletion()
    {
        var job = new AcquisitionJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "provider",
            "source",
            "episode.mkv",
            AcquisitionJobState.Queued,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Retention: AcquisitionRetention.Temporary,
            CachedRanges: [new(0, 1023)]);

        Assert.False(AcquisitionCoordinator.ShouldCompleteAfterPlayback(job));
        Assert.Equal(AcquisitionRetention.Temporary, job.Retention);
        Assert.False(job.Imported);
        Assert.Equal(AcquisitionReconciliationState.Pending, job.ReconciliationState);
    }
}
