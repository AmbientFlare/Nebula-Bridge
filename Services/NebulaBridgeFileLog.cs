using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using NebulaBridge.Config;

namespace NebulaBridge.Services;

/// <summary>Plugin-owned, daily operational telemetry kept out of Jellyfin's main log.</summary>
public sealed partial class NebulaBridgeFileLog(IApplicationPaths paths) : BackgroundService
{
    private const int RetentionDays = 14;
    private readonly Channel<string> _entries = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public static NebulaBridgeFileLog? Instance { get; private set; }

    public string CurrentPath => Path.Combine(
        paths.LogDirectoryPath,
        $"nebula-bridge_{DateTimeOffset.Now:yyyyMMdd}.log");

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Instance = this;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _entries.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    public static void Write(
        NebulaLogVerbosity level,
        string eventName,
        Guid? correlationId = null,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        var instance = Instance;
        var configuration = NebulaBridgePlugin.Instance?.Configuration;
        if (instance is null
            || configuration?.EnableDedicatedLog != true
            || !ShouldWrite(level, configuration.DedicatedLogVerbosity))
            return;

        instance._entries.Writer.TryWrite(FormatEntry(
            DateTimeOffset.Now,
            level,
            eventName,
            correlationId,
            fields));
    }

    internal static bool ShouldWrite(NebulaLogVerbosity level, NebulaLogVerbosity configured) =>
        level <= configured;

    internal static string FormatEntry(
        DateTimeOffset timestamp,
        NebulaLogVerbosity level,
        string eventName,
        Guid? correlationId,
        IReadOnlyDictionary<string, object?>? fields)
    {
        var safeFields = fields?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is string value ? (object?)Redact(value) : pair.Value,
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            timestamp,
            level = level.ToString(),
            eventName = Redact(eventName),
            correlationId = correlationId?.ToString("N"),
            fields = safeFields,
        });
    }

    internal static string Redact(string value)
    {
        var redacted = BearerPattern().Replace(value, "$1[REDACTED]");
        redacted = SensitiveHeaderPattern().Replace(redacted, "$1[REDACTED]");
        redacted = SecretQueryPattern().Replace(redacted, "$1[REDACTED]");
        redacted = SecretAssignmentPattern().Replace(redacted, "$1[REDACTED]");
        return UrlPattern().Replace(redacted, "[URL REDACTED]");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(paths.LogDirectoryPath);
        DeleteExpiredLogs(DateTimeOffset.Now);
        try
        {
            await foreach (var entry in _entries.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await File.AppendAllTextAsync(CurrentPath, entry + Environment.NewLine, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        { }
    }

    internal void DeleteExpiredLogs(DateTimeOffset now)
    {
        if (!Directory.Exists(paths.LogDirectoryPath))
            return;
        foreach (var file in Directory.EnumerateFiles(paths.LogDirectoryPath, "nebula-bridge_????????.log"))
        {
            if (File.GetLastWriteTimeUtc(file) < now.UtcDateTime.AddDays(-RetentionDays))
                File.Delete(file);
        }
    }

    [GeneratedRegex("(?i)(bearer\\s+)[A-Za-z0-9._~+/-]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)([?&](?:api[_-]?key|token|access[_-]?token|auth|authorization|password|secret)=)[^&\\s]+")]
    private static partial Regex SecretQueryPattern();

    [GeneratedRegex("(?i)((?:authorization|cookie|set-cookie)\\s*[:=]\\s*)[^\\r\\n]+")]
    private static partial Regex SensitiveHeaderPattern();

    [GeneratedRegex("(?i)(\\b(?:api[_-]?key|token|access[_-]?token|auth|password|secret)\\s*[:=]\\s*)[^&\\s,;]+")]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex("(?i)https?://[^\\s\\\"']+")]
    private static partial Regex UrlPattern();
}
