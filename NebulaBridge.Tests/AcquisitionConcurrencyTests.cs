using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using NebulaBridge.NativeSources;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class AcquisitionConcurrencyTests
{
    [Fact]
    public void KeepSeriesIdentitySurvivesDuplicateJellyfinSeriesRows()
    {
        var first = new Series { Id = Guid.NewGuid() };
        var duplicate = new Series { Id = Guid.NewGuid() };
        first.SetProviderId("Stremio", "tt2861424");
        duplicate.SetProviderId("Stremio", "tt2861424");

        Assert.Equal(
            NebulaBridgeManager.GetAcquisitionSeriesIdentity(
                new Episode { SeriesId = first.Id },
                first),
            NebulaBridgeManager.GetAcquisitionSeriesIdentity(
                new Episode { SeriesId = duplicate.Id },
                duplicate));
    }

    [Theory]
    [MemberData(nameof(RetryableCompletionFailures))]
    public void CompletionRetriesTransientProviderAndRangeFailures(Exception exception)
    {
        Assert.True(AcquisitionCoordinator.IsRetryableCompletionException(exception));
        Assert.Equal(5, AcquisitionCoordinator.CompletionRetryAttempts);
    }

    public static TheoryData<Exception> RetryableCompletionFailures => new()
    {
        new HttpRequestException(),
        new IOException(),
        new InvalidOperationException(),
        new System.Text.Json.JsonException(),
        new TaskCanceledException(),
    };

    [Fact]
    public void RetriedCompletionClearsThePreviousTransientError()
    {
        var failed = new AcquisitionJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "torbox",
            "source",
            "episode.mkv",
            AcquisitionJobState.Failed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Error: nameof(IOException),
            Retention: AcquisitionRetention.KeepSeries);

        var completing = AcquisitionCoordinator.MarkCompleting(failed);

        Assert.Equal(AcquisitionJobState.Completing, completing.State);
        Assert.Null(completing.Error);
    }

    [Fact]
    public void LaterPlaybackCompletesAnInheritedKeepSeriesJobOnlyAfterItIsEncountered()
    {
        var encountered = new AcquisitionJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "torbox",
            "source",
            "episode.mkv",
            AcquisitionJobState.Queued,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Retention: AcquisitionRetention.KeepSeries);

        Assert.True(AcquisitionCoordinator.ShouldCompleteAfterPlayback(encountered));
        Assert.False(AcquisitionCoordinator.ShouldCompleteAfterPlayback(
            encountered with { Retention = AcquisitionRetention.Temporary }));
        Assert.False(AcquisitionCoordinator.ShouldCompleteAfterPlayback(
            encountered with { State = AcquisitionJobState.Imported, Imported = true }));
    }

    [Fact]
    public void AcquisitionUsesSelectedProviderFileNameInsteadOfReleaseTitle()
    {
        var handle = new DebridCompletedFileHandle(
            "torbox",
            "remote",
            "file",
            "folder/preview.mp4",
            100);

        Assert.Equal("preview.mp4", AcquisitionCoordinator.GetSelectedFileName(handle));
    }

    [Fact]
    public void ExactIdentitySeparatesReleasesAndFilesFromSameIndexer()
    {
        var item = Guid.NewGuid();
        var first = AcquisitionJobIdentity.Create(item, new("torbox", "torrent-1", "file-1", "a.mkv", 100));
        var otherRelease = AcquisitionJobIdentity.Create(item, new("torbox", "torrent-2", "file-1", "a.mkv", 100));
        var otherFile = AcquisitionJobIdentity.Create(item, new("torbox", "torrent-1", "file-2", "a.mkv", 100));
        var otherLength = AcquisitionJobIdentity.Create(item, new("torbox", "torrent-1", "file-1", "a.mkv", 101));
        Assert.Equal(4, new[] { first, otherRelease, otherFile, otherLength }.Distinct().Count());
    }

    [Fact]
    public void KeepSeriesSchedulesSelectedAndEncounteredTemporaryJobsOnly()
    {
        var series = Guid.NewGuid().ToString();
        var selected = new AcquisitionJob(Guid.NewGuid(), Guid.NewGuid(), "torbox", "selected", "one.mkv",
            AcquisitionJobState.Queued, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Retention: AcquisitionRetention.Temporary, SeriesId: series);
        var related = selected with { Id = Guid.NewGuid(), SourceId = "related" };
        var unrelatedSeries = selected with { Id = Guid.NewGuid(), SeriesId = Guid.NewGuid().ToString() };
        var alreadyRetained = selected with { Id = Guid.NewGuid(), Retention = AcquisitionRetention.KeepItem };

        var ids = AcquisitionCoordinator.GetCompletionIds(selected.Id, AcquisitionRetention.KeepSeries, selected,
            [selected, related, unrelatedSeries, alreadyRetained]);

        Assert.Equal([selected.Id, related.Id], ids);
    }

    [Fact]
    public async Task ConcurrentRangeWritesMergeAgainstCurrentState()
    {
        await using var fixture = new StoreFixture();
        var job = fixture.Job();
        await fixture.Store.UpsertAsync(job, default);
        var cache = new ProgressiveRangeCache();
        await Task.WhenAll(
            cache.StoreAsync(job, 0, new byte[10], fixture.Store, default),
            cache.StoreAsync(job, 20, new byte[10], fixture.Store, default));
        var saved = Assert.Single(await fixture.Store.ReadAsync(default));
        Assert.Equal([new CachedByteRange(0, 9), new CachedByteRange(20, 29)], saved.CachedRanges);
    }

    [Fact]
    public async Task ConcurrentMutationsPreserveRangeAndRetention()
    {
        await using var fixture = new StoreFixture();
        var job = fixture.Job();
        await fixture.Store.UpsertAsync(job, default);
        await Task.WhenAll(
            fixture.Store.MutateAsync(job.Id, current => current! with { CachedRanges = [new(0, 9)] }, default),
            fixture.Store.MutateAsync(job.Id, current => current! with { Retention = AcquisitionRetention.KeepItem }, default));
        var saved = Assert.Single(await fixture.Store.ReadAsync(default));
        Assert.Equal(AcquisitionRetention.KeepItem, saved.Retention);
        Assert.Equal([new CachedByteRange(0, 9)], saved.CachedRanges);
    }

    [Fact]
    public async Task RemovingOneJobCannotEraseConcurrentUnrelatedUpdate()
    {
        await using var fixture = new StoreFixture();
        var first = fixture.Job();
        var second = fixture.Job();
        await fixture.Store.UpsertAsync(first, default);
        await fixture.Store.UpsertAsync(second, default);
        await Task.WhenAll(
            fixture.Store.RemoveAsync(first.Id, default),
            fixture.Store.MutateAsync(second.Id, current => current! with { Retention = AcquisitionRetention.KeepItem }, default));
        var saved = Assert.Single(await fixture.Store.ReadAsync(default));
        Assert.Equal(second.Id, saved.Id);
        Assert.Equal(AcquisitionRetention.KeepItem, saved.Retention);
    }

    [Fact]
    public async Task SimultaneousExactIdentityCreationProducesOneJob()
    {
        await using var fixture = new StoreFixture();
        var handle = new DebridCompletedFileHandle("torbox", "1", "2", "a.mkv", 100);
        var identity = AcquisitionJobIdentity.Create(Guid.NewGuid(), handle);
        AcquisitionJob Create() => fixture.Job() with { CacheIdentity = identity, CompletedFileHandle = handle };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fixture.Store.GetOrCreateAsync(identity, Create, current => current, default)));
        Assert.Single(results.Select(result => result.Id).Distinct());
        Assert.Single(await fixture.Store.ReadAsync(default));
    }

    [Fact]
    public async Task ImportedExactIdentityIsReusedInsteadOfCreatingDuplicateJobId()
    {
        await using var fixture = new StoreFixture();
        var itemId = Guid.NewGuid();
        var handle = new DebridCompletedFileHandle("torbox", "1", "2", "a.mkv", 100);
        var identity = AcquisitionJobIdentity.Create(itemId, handle);
        var imported = fixture.Job() with
        {
            Id = AcquisitionJobIdentity.ToJobId(identity),
            ItemId = itemId,
            CacheIdentity = identity,
            CompletedFileHandle = handle,
            State = AcquisitionJobState.Imported,
            Imported = true,
        };
        await fixture.Store.UpsertAsync(imported, default);

        var result = await fixture.Store.GetOrCreateAsync(identity,
            () => throw new InvalidOperationException("Exact imported identity must be reused."),
            current => current,
            default);

        Assert.Equal(imported.Id, result.Id);
        Assert.Single(await fixture.Store.ReadAsync(default));
    }

    [Fact]
    public void OnlyRetainedUnfinishedJobsCanBeCancelledAndCancelDropsTheKeepIntent()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), Guid.NewGuid(), "torbox", "source", "episode.mkv",
            AcquisitionJobState.Completing, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Retention: AcquisitionRetention.KeepItem, Error: nameof(IOException),
            CachedRanges: [new CachedByteRange(0, 4_194_303)]);

        Assert.True(AcquisitionCoordinator.CanCancel(job));
        Assert.True(AcquisitionCoordinator.CanCancel(job with { State = AcquisitionJobState.Failed }));
        Assert.True(AcquisitionCoordinator.CanCancel(job with { State = AcquisitionJobState.Queued }));
        Assert.False(AcquisitionCoordinator.CanCancel(job with { Retention = AcquisitionRetention.Temporary }));
        Assert.False(AcquisitionCoordinator.CanCancel(job with { State = AcquisitionJobState.ReadyToImport }));
        Assert.False(AcquisitionCoordinator.CanCancel(job with { State = AcquisitionJobState.Importing }));
        Assert.False(AcquisitionCoordinator.CanCancel(job with { State = AcquisitionJobState.Imported, Imported = true }));

        var cancelled = AcquisitionCoordinator.MarkCancelled(job);

        Assert.Equal(AcquisitionJobState.Cancelled, cancelled.State);
        Assert.Equal(AcquisitionRetention.Temporary, cancelled.Retention);
        Assert.NotNull(cancelled.ExpiresUtc);
        Assert.Null(cancelled.Error);
        Assert.Equal(job.CachedRanges, cancelled.CachedRanges);
        Assert.True(AcquisitionCoordinator.CanPurge(cancelled));
        Assert.False(AcquisitionCoordinator.ShouldCompleteAfterPlayback(cancelled));
        Assert.Equal(AcquisitionRecoveryAction.None, AcquisitionImportRecoveryService.GetRecoveryAction(cancelled));
    }

    [Fact]
    public async Task ACompletionIsOwnedOncePerJobAndCancelStopsItWithTheBytesKept()
    {
        await using var fixture = new StoreFixture();
        var provider = new HeldCompletionProvider();
        var coordinator = fixture.Coordinator(provider);
        var job = fixture.RetainedJob(provider.Id, provider.Handle);
        await fixture.Store.UpsertAsync(job, default);

        var first = coordinator.StartCompletion(job.Id);
        var second = coordinator.StartCompletion(job.Id);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(first, second);
        Assert.Equal(1, provider.Resolves);
        Assert.Equal([job.Id], coordinator.RunningCompletions);
        Assert.Equal(AcquisitionJobState.Completing, (await fixture.Store.ReadAsync(default)).Single().State);

        Assert.True(await coordinator.CancelAsync(job.Id, default));

        var saved = (await fixture.Store.ReadAsync(default)).Single();
        Assert.Equal(AcquisitionJobState.Cancelled, saved.State);
        Assert.Equal(AcquisitionRetention.Temporary, saved.Retention);
        Assert.Equal(job.CachedRanges, saved.CachedRanges);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.Empty(coordinator.RunningCompletions);
        Assert.False(await coordinator.CancelAsync(job.Id, default));
    }

    [Fact]
    public async Task ShutdownStopsRunningCompletionsButLeavesThemForRecovery()
    {
        await using var fixture = new StoreFixture();
        var provider = new HeldCompletionProvider();
        var coordinator = fixture.Coordinator(provider);
        var job = fixture.RetainedJob(provider.Id, provider.Handle);
        await fixture.Store.UpsertAsync(job, default);

        var run = coordinator.StartCompletion(job.Id);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.ShutdownAsync(default);

        Assert.True(run.IsCompletedSuccessfully);
        Assert.Empty(coordinator.RunningCompletions);
        var saved = (await fixture.Store.ReadAsync(default)).Single();
        Assert.Equal(AcquisitionJobState.Completing, saved.State);
        Assert.Equal(AcquisitionRetention.KeepItem, saved.Retention);
        Assert.Equal(AcquisitionRecoveryAction.Complete, AcquisitionImportRecoveryService.GetRecoveryAction(saved));
        Assert.True(coordinator.StartCompletion(job.Id).IsCompleted);
        Assert.Equal(1, provider.Resolves);
    }

    [Fact]
    public async Task CancellingAJobNobodyIsCompletingStillDropsTheKeepIntent()
    {
        await using var fixture = new StoreFixture();
        var provider = new HeldCompletionProvider();
        var coordinator = fixture.Coordinator(provider);
        var job = fixture.RetainedJob(provider.Id, provider.Handle) with { State = AcquisitionJobState.Failed, Error = nameof(IOException) };
        await fixture.Store.UpsertAsync(job, default);

        Assert.True(await coordinator.CancelAsync(job.Id, default));

        var saved = (await fixture.Store.ReadAsync(default)).Single();
        Assert.Equal(AcquisitionJobState.Cancelled, saved.State);
        Assert.Null(saved.Error);
        Assert.Equal(0, provider.Resolves);
    }

    /// <summary>A completed-file provider whose source resolution blocks until cancelled.</summary>
    private sealed class HeldCompletionProvider : IDebridProvider
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "held";
        public string Name => "Held";
        public bool Enabled => true;
        public bool Configured => true;
        public DebridProviderCapabilities Capabilities => DebridProviderCapabilities.CompletedFileSource;
        public Task Started => _started.Task;
        public int Resolves { get; private set; }
        public DebridCompletedFileHandle Handle { get; } = new("held", "1", "1", "Movie.mkv", 8_388_608);

        public Task<DebridCacheCheckResult> CheckCachedAsync(IReadOnlyCollection<string> infoHashes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DebridPlaybackResult> ResolvePlaybackAsync(NativeReleaseCandidate candidate, NativeMediaQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<DebridCompletedFileSource?> ResolveCompletedFileSourceAsync(DebridCompletedFileHandle handle, CancellationToken cancellationToken)
        {
            Resolves++;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "nebulabridge-tests", Guid.NewGuid().ToString("N"));
        public StoreFixture() => Store = new(new TestPaths(_root));
        public AcquisitionJobStore Store { get; }
        public AcquisitionJob Job() => new(Guid.NewGuid(), Guid.NewGuid(), "torbox", "indexer", "a.mkv",
            AcquisitionJobState.Queued, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        public AcquisitionJob RetainedJob(string providerId, DebridCompletedFileHandle handle) => Job() with
        {
            ProviderId = providerId,
            State = AcquisitionJobState.Queued,
            Retention = AcquisitionRetention.KeepItem,
            ExpectedBytes = handle.ExpectedLength,
            CompletedFileHandle = handle,
            CachedRanges = [new CachedByteRange(0, 4_194_303)],
        };

        public AcquisitionCoordinator Coordinator(IDebridProvider provider) => new(
            Store,
            new ProgressiveRangeCache(),
            [provider],
            null!,
            null!,
            new AcquisitionActivityTracker(),
            null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AcquisitionCoordinator>.Instance)
        {
            EnabledOverride = true,
        };

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;
        public string WebPath => root;
        public string ProgramSystemPath => root;
        public string DataPath => root;
        public string ImageCachePath => root;
        public string PluginsPath => root;
        public string PluginConfigurationsPath => root;
        public string LogDirectoryPath => root;
        public string ConfigurationDirectoryPath => root;
        public string SystemConfigurationFilePath => Path.Combine(root, "system.xml");
        public string CachePath => root;
        public string TempDirectory => root;
        public string VirtualDataPath => root;
        public string TrickplayPath => root;
        public string BackupPath => root;
        public void MakeSanityCheckOrThrow() => Directory.CreateDirectory(root);
        public void CreateAndCheckMarker(string path, string markerName, bool recursive) => Directory.CreateDirectory(path);
    }
}
