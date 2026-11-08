using NebulaBridge.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace NebulaBridge.Tests;

public sealed class AcquisitionImportTests
{
    [Fact]
    public void EpisodeDestinationCarriesTrustedSeriesIdentityIntoJellyfinScan()
    {
        var series = new Series { Name = "Example" };
        series.SetProviderId(MetadataProvider.Tvdb, "12345");
        var episode = new Episode
        {
            Name = "Pilot",
            SeriesName = "Example",
            ParentIndexNumber = 1,
            IndexNumber = 2,
        };

        Assert.Equal(
            Path.Combine(
                "Example [tvdbid-12345]",
                "Season 01",
                "Example S01E02 - Pilot.mkv"),
            AcquisitionImportService.BuildDestination(episode, "provider-file.mkv", series));
    }

    [Fact]
    public void IncompleteRangesCannotImport()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), Guid.NewGuid(), "test", "source", "movie.mkv",
            AcquisitionJobState.ReadyToImport, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ExpectedBytes: 100, CachedRanges: [new CachedByteRange(0, 49)]);
        Assert.False(AcquisitionImportService.IsComplete(job));
    }

    [Fact]
    public void FullRangeCanImport()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), Guid.NewGuid(), "test", "source", "movie.mkv",
            AcquisitionJobState.ReadyToImport, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ExpectedBytes: 100, CachedRanges: [new CachedByteRange(0, 99)]);
        Assert.True(AcquisitionImportService.IsComplete(job));
    }

    [Theory]
    [InlineData("../Bad:Movie/ ", "_Bad_Movie_")]
    [InlineData("Normal Name", "Normal Name")]
    public void LogicalFileNamesAreSanitized(string input, string expected)
    {
        Assert.Equal(expected, AcquisitionImportService.SafeName(input));
    }

    [Fact]
    public void TemporaryPathIsOwnedByJob()
    {
        var final = Path.Combine(Path.GetTempPath(), "movie.mkv");
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.Equal(final + ".11111111111111111111111111111111.nebulabridge-importing",
            AcquisitionImportService.JobOwnedTemporaryPath(final, id));
        Assert.NotEqual(
            AcquisitionImportService.JobOwnedTemporaryPath(final, id),
            AcquisitionImportService.JobOwnedTemporaryPath(final, Guid.NewGuid()));
    }

    [Fact]
    public async Task ContentHashDistinguishesSameLengthDestinationConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "nebulabridge-hash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var staged = Path.Combine(root, "staged");
            var existing = Path.Combine(root, "existing");
            await File.WriteAllBytesAsync(staged, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(existing, [4, 3, 2, 1]);
            Assert.NotEqual(
                await AcquisitionImportService.ComputeSha256Async(staged, default),
                await AcquisitionImportService.ComputeSha256Async(existing, default));
            var stagedHash = await AcquisitionImportService.ComputeSha256Async(staged, default);
            Assert.True(await AcquisitionImportService.IsOwnedArtifactAsync(staged, 4, stagedHash, default));
            Assert.False(await AcquisitionImportService.IsOwnedArtifactAsync(existing, 4, stagedHash, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentImportsForSameDestinationAreSerialized()
    {
        var destination = Path.Combine(Path.GetTempPath(), "nebulabridge-tests", Guid.NewGuid().ToString("N"), "movie.mkv");
        var first = await AcquisitionImportService.AcquireDestinationAsync(destination, default);
        var secondTask = AcquisitionImportService.AcquireDestinationAsync(destination, default);
        Assert.False(secondTask.IsCompleted);
        await first.DisposeAsync();
        await using var second = await secondTask;
    }

    [Theory]
    [InlineData(AcquisitionJobState.ReadyToImport, AcquisitionRetention.KeepItem, 2)]
    [InlineData(AcquisitionJobState.Importing, AcquisitionRetention.KeepItem, 2)]
    [InlineData(AcquisitionJobState.Queued, AcquisitionRetention.KeepItem, 1)]
    [InlineData(AcquisitionJobState.Completing, AcquisitionRetention.KeepSeries, 1)]
    [InlineData(AcquisitionJobState.Failed, AcquisitionRetention.KeepItem, 1)]
    [InlineData(AcquisitionJobState.Queued, AcquisitionRetention.Temporary, 0)]
    [InlineData(AcquisitionJobState.Imported, AcquisitionRetention.Permanent, 0)]
    public void RestartRecoveryRoutesDurableStates(
        AcquisitionJobState state,
        AcquisitionRetention retention,
        int expected)
    {
        var job = new AcquisitionJob(Guid.NewGuid(), Guid.NewGuid(), "torbox", "source", "a.mkv",
            state, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Retention: retention);
        Assert.Equal(expected, (int)AcquisitionImportRecoveryService.GetRecoveryAction(job));
    }
}
