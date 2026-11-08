using MediaBrowser.Common.Configuration;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class NebulaBridgeFileLogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nebulabridge-log-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DedicatedLogRedactsSecretsAndNeverSerializesProviderUrlsVerbatim()
    {
        var line = NebulaBridgeFileLog.FormatEntry(
            DateTimeOffset.Parse("2026-09-10T12:00:00-07:00"),
            NebulaLogVerbosity.Debug,
            "provider-source",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new Dictionary<string, object?>
            {
                ["detail"] = "Bearer token-value https://provider.invalid/file?api_key=secret&x=1",
            });

        Assert.Contains("[REDACTED]", line, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", line, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=secret", line, StringComparison.Ordinal);
        Assert.DoesNotContain("provider.invalid", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", line, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(NebulaLogVerbosity.Information, NebulaLogVerbosity.Information, true)]
    [InlineData(NebulaLogVerbosity.Debug, NebulaLogVerbosity.Information, false)]
    [InlineData(NebulaLogVerbosity.Debug, NebulaLogVerbosity.Debug, true)]
    [InlineData(NebulaLogVerbosity.Trace, NebulaLogVerbosity.Debug, false)]
    public void VerbosityIsBounded(
        NebulaLogVerbosity level,
        NebulaLogVerbosity configured,
        bool expected) =>
        Assert.Equal(expected, NebulaBridgeFileLog.ShouldWrite(level, configured));

    [Fact]
    public void CleanupDeletesOnlyExpiredNebulaDailyLogs()
    {
        Directory.CreateDirectory(_root);
        var expired = Path.Combine(_root, "nebula-bridge_20260801.log");
        var current = Path.Combine(_root, "nebula-bridge_20260910.log");
        var unrelated = Path.Combine(_root, "log_20260910.log");
        File.WriteAllText(expired, "old");
        File.WriteAllText(current, "current");
        File.WriteAllText(unrelated, "jellyfin");
        File.SetLastWriteTimeUtc(expired, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(current, new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc));
        var log = new NebulaBridgeFileLog(new TestPaths(_root));

        log.DeleteExpiredLogs(DateTimeOffset.Parse("2026-09-10T12:00:00Z"));

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(unrelated));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
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
