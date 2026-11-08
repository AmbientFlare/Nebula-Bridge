using NebulaBridge.Services;
using MediaBrowser.Common.Configuration;

namespace NebulaBridge.Tests;

public sealed class ProgressiveRangeCacheTests
{
    [Fact]
    public void NormalizeSortsAndMergesOverlappingAndAdjacentRanges()
    {
        var normalized = ProgressiveRangeCache.Normalize(
            [
                new CachedByteRange(20, 29),
                new CachedByteRange(0, 9),
                new CachedByteRange(8, 19),
                new CachedByteRange(30, 39),
            ]
        );

        Assert.Equal(
            [new CachedByteRange(0, 39)],
            normalized
        );
    }

    [Fact]
    public void NormalizePreservesGapsBetweenRanges()
    {
        var normalized = ProgressiveRangeCache.Normalize(
            [
                new CachedByteRange(100, 109),
                new CachedByteRange(0, 9),
                new CachedByteRange(20, 29),
            ]
        );

        Assert.Equal(
            [
                new CachedByteRange(0, 9),
                new CachedByteRange(20, 29),
                new CachedByteRange(100, 109),
            ],
            normalized
        );
    }

    [Fact]
    public void NormalizeCollapsesDuplicateRanges()
    {
        var range = new CachedByteRange(42, 51);

        var normalized = ProgressiveRangeCache.Normalize([range, range]);

        Assert.Equal([range], normalized);
    }

    [Fact]
    public async Task PartialTemporaryCacheReopensExistingBytesWithoutFetchingAgain()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nebulabridge-replay-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AcquisitionJobStore(new TestPaths(root));
            var cache = new ProgressiveRangeCache();
            var job = new AcquisitionJob(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "provider",
                "source",
                "episode.mkv",
                AcquisitionJobState.Queued,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ExpectedBytes: 4096);
            await store.UpsertAsync(job, default);
            var bytes = Enumerable.Range(0, 1024).Select(index => (byte)(index % 251)).ToArray();
            await cache.StoreAsync(job, 0, bytes, store, default);

            var reloaded = Assert.Single(await store.ReadAsync(default));
            var replayed = await cache.TryReadAsync(reloaded, 0, bytes.Length, store, default);

            Assert.Equal(bytes, replayed);
            Assert.Equal([new CachedByteRange(0, 1023)], reloaded.CachedRanges);
            Assert.Equal(AcquisitionRetention.Temporary, reloaded.Retention);
            Assert.False(reloaded.Imported);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
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
        public void CreateAndCheckMarker(string path, string markerName, bool recursive) =>
            Directory.CreateDirectory(path);
    }
}
