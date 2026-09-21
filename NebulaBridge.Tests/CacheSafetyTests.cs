using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using NebulaBridge.Controllers;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class CacheSafetyTests
{
    [Fact]
    public void ValidRangeRequiresExact206Contract()
    {
        var range = new ContentRangeHeaderValue(10, 19, 100);
        Assert.NotNull(CacheHttpResponseValidator.Validate(HttpStatusCode.PartialContent, range, 10,
            "video/x-matroska", 10, 10, 100));
        Assert.Null(CacheHttpResponseValidator.Validate(HttpStatusCode.PartialContent,
            new ContentRangeHeaderValue(11, 20, 100), 10, "video/x-matroska", 10, 10, 100));
        Assert.Null(CacheHttpResponseValidator.Validate(HttpStatusCode.PartialContent, range, 9,
            "video/x-matroska", 10, 10, 100));
        Assert.Null(CacheHttpResponseValidator.Validate(HttpStatusCode.PartialContent,
            new ContentRangeHeaderValue(10, 19), 10, "video/x-matroska", 10, 10, 100));
        Assert.Null(CacheHttpResponseValidator.Validate(HttpStatusCode.PartialContent,
            new ContentRangeHeaderValue(95, 104, 105), 10, "video/x-matroska", 95, 10, 100));
    }

    [Fact]
    public void ExactChunkedPartialResponseWithoutContentLengthIsCacheable()
    {
        var plan = CacheHttpResponseValidator.Validate(
            HttpStatusCode.PartialContent,
            new ContentRangeHeaderValue(0, 1023, 4096),
            null,
            null,
            0,
            1024,
            4096);

        Assert.Equal(new CacheWritePlan(0, 1024, 4096), plan);
        Assert.Equal(
            new CacheWritePlan(0, 1024, 4096),
            CacheHttpResponseValidator.Validate(
                HttpStatusCode.PartialContent,
                new ContentRangeHeaderValue(0, 1023, 4096),
                1024,
                null,
                0,
                1024,
                null));
    }

    [Fact]
    public void InvalidRangeIsRejectedOnlyWhenBytesWouldEnterAcquisitionCache()
    {
        Assert.True(NebulaBridgeApiController.ShouldRejectInvalidRange(
            true,
            HttpStatusCode.PartialContent,
            true,
            null));
        Assert.False(NebulaBridgeApiController.ShouldRejectInvalidRange(
            false,
            HttpStatusCode.PartialContent,
            true,
            null));
    }

    [Fact]
    public void HeadProbeDoesNotCreatePlaybackAcquisition()
    {
        Assert.False(NebulaBridgeApiController.ShouldBeginPlayback(HttpMethods.Head, "bytes=0-99"));
        Assert.False(NebulaBridgeApiController.ShouldBeginPlayback(HttpMethods.Get, null));
        Assert.True(NebulaBridgeApiController.ShouldBeginPlayback(HttpMethods.Get, "bytes=0-99"));
        Assert.False(NebulaBridgeApiController.ShouldBeginPlayback(HttpMethods.Get, "bytes=invalid"));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadGateway)]
    public void RangeErrorsAndIgnoredRangesNeverEnterCache(HttpStatusCode status)
    {
        Assert.Null(CacheHttpResponseValidator.Validate(status, null, 20, "text/html", 10, 10, 100));
    }

    [Fact]
    public async Task TeeUsesBoundedChunksAndContinuesWhenOptionalCacheFails()
    {
        var bytes = new byte[BoundedMediaTee.BufferBytes * 3 + 7];
        Random.Shared.NextBytes(bytes);
        await using var source = new MemoryStream(bytes);
        await using var destination = new MemoryStream();
        var largest = 0;
        var attempts = 0;
        await BoundedMediaTee.CopyAsync(source, destination, 0, (_, chunk, _) =>
        {
            largest = Math.Max(largest, chunk.Length);
            attempts++;
            throw new IOException("cache unavailable");
        }, default);
        Assert.Equal(bytes, destination.ToArray());
        Assert.InRange(largest, 1, BoundedMediaTee.BufferBytes);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void FreeSpaceFloorReservesConfiguredCapacity()
    {
        Assert.True(CacheStorageSafety.HasCapacity(4096, 1024, 0));
        Assert.False(CacheStorageSafety.HasCapacity(2048, 1024, 1));
    }

    [Fact]
    public async Task ActivePlaybackPreventsPurgeTransitionUntilReleased()
    {
        var tracker = new AcquisitionActivityTracker();
        var id = Guid.NewGuid();
        var playback = await tracker.AcquireUseAsync(id, default);
        var transitionTask = tracker.AcquireExclusiveAsync(id, default);
        Assert.False(transitionTask.IsCompleted);
        await playback.DisposeAsync();
        await using var transition = await transitionTask;
        Assert.False(tracker.IsActive(id));
    }

    [Fact]
    public async Task ActiveCacheWriterPreventsImportTransitionUntilReleased()
    {
        var tracker = new AcquisitionActivityTracker();
        var id = Guid.NewGuid();
        var writer = await tracker.AcquireUseAsync(id, default);
        var importTask = tracker.AcquireExclusiveAsync(id, default);
        Assert.False(importTask.IsCompleted);
        await writer.DisposeAsync();
        await using var import = await importTask;
        Assert.False(tracker.IsActive(id));
    }

    [Fact]
    public async Task RetentionAndPurgeTransitionsAreSerialized()
    {
        var tracker = new AcquisitionActivityTracker();
        var id = Guid.NewGuid();
        var retention = await tracker.AcquireExclusiveAsync(id, default);
        var purgeTask = tracker.AcquireExclusiveAsync(id, default);
        Assert.False(purgeTask.IsCompleted);
        await retention.DisposeAsync();
        await using var purge = await purgeTask;
    }

    [Fact]
    public void RetainedAndImportLifecycleJobsCannotBePurged()
    {
        var job = new AcquisitionJob(Guid.NewGuid(), Guid.NewGuid(), "torbox", "source", "a.mkv",
            AcquisitionJobState.Queued, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.True(AcquisitionCoordinator.CanPurge(job));
        Assert.False(AcquisitionCoordinator.CanPurge(job with { Retention = AcquisitionRetention.KeepItem }));
        Assert.False(AcquisitionCoordinator.CanPurge(job with { State = AcquisitionJobState.ReadyToImport }));
        Assert.False(AcquisitionCoordinator.CanPurge(job with { State = AcquisitionJobState.Imported }));
    }
}
