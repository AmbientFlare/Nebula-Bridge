using System.Collections.Concurrent;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.NativeSources;

/// <summary>
/// In-memory provider health with bounded backoff. Failures never disable a provider
/// permanently: every state expires and the next request re-probes the provider.
/// </summary>
public sealed class DebridProviderHealthTracker
{
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AuthFailureBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PremiumBackoff = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, HealthRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public DebridProviderHealthTracker()
        : this(() => DateTimeOffset.UtcNow)
    { }

    public DebridProviderHealthTracker(Func<DateTimeOffset> clock)
    {
        _clock = clock;
    }

    public DebridProviderHealthSnapshot Get(string providerId)
    {
        var now = _clock();
        if (!_records.TryGetValue(providerId, out var record) || record.UntilUtc <= now)
            return new DebridProviderHealthSnapshot(providerId, DebridProviderHealth.Healthy, null, record?.LastReason, record?.LastFailureUtc);
        return new DebridProviderHealthSnapshot(providerId, record.Health, record.UntilUtc, record.LastReason, record.LastFailureUtc);
    }

    /// <summary>True when the provider should be tried now. Expired backoff windows re-open automatically.</summary>
    public bool IsAvailable(string providerId) => Get(providerId).Health == DebridProviderHealth.Healthy;

    public void ReportSuccess(string providerId)
    {
        if (_records.TryRemove(providerId, out _))
            Log(providerId, DebridProviderHealth.Healthy, null, "recovered");
    }

    /// <summary>Records a failure and returns the health state it mapped to.</summary>
    public DebridProviderHealth ReportFailure(string providerId, NativeSourceFailure failure) =>
        ReportFailure(providerId, failure.Reason);

    public DebridProviderHealth ReportFailure(string providerId, string reason)
    {
        var health = Classify(reason);
        if (health == DebridProviderHealth.Healthy)
            return health;
        var now = _clock();
        var record = _records.AddOrUpdate(
            providerId,
            _ => new HealthRecord(health, now + BackoffFor(health, 0), 1, reason, now),
            (_, existing) =>
            {
                var consecutive = existing.UntilUtc > now ? existing.Consecutive + 1 : 1;
                return new HealthRecord(health, now + BackoffFor(health, consecutive - 1), consecutive, reason, now);
            });
        Log(providerId, health, record.UntilUtc, reason);
        return health;
    }

    /// <summary>
    /// Maps normalized failure reasons to health states. Configuration problems are not health, and
    /// neither is anything that only says this one torrent cannot be served (not cached, no usable
    /// file, links that do not describe the chosen file, an archive bundle, a blocked file): the provider answered
    /// correctly and the next release may well play.
    /// </summary>
    public static DebridProviderHealth Classify(string? reason) => reason switch
    {
        "rate_limited" => DebridProviderHealth.RateLimited,
        "authentication_rejected" => DebridProviderHealth.AuthFailed,
        "premium_required" or "account_locked" => DebridProviderHealth.PremiumUnavailable,
        null or "" or "not_configured" or "not_cached" or "invalid_hash" or "not_found"
            or "no_playable_file" or "wrong_title" or "wrong_episode"
            or "links_mismatch" or "length_mismatch" or "archive_link" or "blocked_content" => DebridProviderHealth.Healthy,
        _ => DebridProviderHealth.TemporarilyUnavailable,
    };

    internal static TimeSpan BackoffFor(DebridProviderHealth health, int previousFailures) => health switch
    {
        DebridProviderHealth.RateLimited => Min(Scale(RateLimitBackoff, previousFailures), TimeSpan.FromMinutes(5)),
        DebridProviderHealth.AuthFailed => AuthFailureBackoff,
        DebridProviderHealth.PremiumUnavailable => PremiumBackoff,
        _ => Min(Scale(MinimumBackoff, previousFailures), MaximumBackoff),
    };

    private static TimeSpan Scale(TimeSpan value, int exponent) =>
        TimeSpan.FromTicks(value.Ticks << Math.Clamp(exponent, 0, 6));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static void Log(string providerId, DebridProviderHealth health, DateTimeOffset? until, string? reason) =>
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "debrid-health",
            fields: new Dictionary<string, object?>
            {
                ["provider"] = providerId,
                ["health"] = health.ToString(),
                ["reason"] = reason,
                ["untilUtc"] = until?.ToString("O"),
            });

    private sealed record HealthRecord(
        DebridProviderHealth Health,
        DateTimeOffset UntilUtc,
        int Consecutive,
        string? LastReason,
        DateTimeOffset LastFailureUtc);
}

public sealed record DebridProviderHealthSnapshot(
    string ProviderId,
    DebridProviderHealth Health,
    DateTimeOffset? UntilUtc,
    string? LastReason,
    DateTimeOffset? LastFailureUtc);
