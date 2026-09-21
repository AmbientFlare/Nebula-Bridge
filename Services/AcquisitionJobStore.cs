using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Services;

public enum AcquisitionJobState
{
    Queued,
    Downloading,
    Completing,
    ReadyToImport,
    Importing,
    Imported,
    Completed,
    Failed,
    Cancelled,
}

public enum AcquisitionRetention
{
    Temporary,
    KeepItem,
    KeepSeries,
    Permanent,
}

/// <summary>Durable progress for the post-import virtual-to-local handoff.</summary>
public enum AcquisitionReconciliationState
{
    Pending,
    LocalItemMatched,
    StateTransferred,
    Reconciled,
    Failed,
}

public sealed record CachedByteRange(long Start, long End)
{
    public bool Contains(long start, long end) => Start <= start && End >= end;
}

/// <summary>Durable local-acquisition intent. No provider URL or credential is persisted.</summary>
public sealed record AcquisitionJob(
    Guid Id,
    Guid ItemId,
    string ProviderId,
    string SourceId,
    string FileName,
    AcquisitionJobState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    long BytesDownloaded = 0,
    long? ExpectedBytes = null,
    int Attempt = 0,
    string? Error = null,
    AcquisitionRetention Retention = AcquisitionRetention.Temporary,
    DateTimeOffset? LastAccessUtc = null,
    DateTimeOffset? ExpiresUtc = null,
    IReadOnlyList<CachedByteRange>? CachedRanges = null,
    string? SeriesId = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    bool Imported = false,
    DebridCompletedFileHandle? CompletedFileHandle = null,
    string? FinalPath = null,
    string? ImportTempPath = null,
    Guid? LocalItemId = null,
    string? CacheIdentity = null,
    string? ContentSha256 = null,
    AcquisitionReconciliationState ReconciliationState = AcquisitionReconciliationState.Pending,
    string? ReconciliationError = null,
    string? ProxyKey = null,
    IReadOnlyList<DebridCompletedFileHandle>? AlternateHandles = null
);

public static class AcquisitionJobIdentity
{
    /// <summary>
    /// Provider-neutral identity when the handle proves its content (infohash + path + length):
    /// the same bytes served by any provider map to one job. Handles without that proof fall
    /// back to the provider-scoped identity used by earlier releases.
    /// </summary>
    public static string Create(Guid itemId, DebridCompletedFileHandle handle)
    {
        // Keyed on the file name rather than the provider's path: TorBox and Real-Debrid report
        // different amounts of the torrent's directory tree for the same file, and the same bytes
        // must map to one job whichever provider first served them.
        if (handle.ContentIdentity is { } content)
            return Hash($"{itemId:N}\ncontent\n{content.InfoHash}\n{content.FileName}\n{content.Length}");
        return CreateProviderScoped(itemId, handle);
    }

    /// <summary>The first 6B identity, keyed on the provider's full path; kept so jobs stored under it are still found.</summary>
    internal static string? CreateFullPathScoped(Guid itemId, DebridCompletedFileHandle handle) =>
        handle.ContentIdentity is { } content
            ? Hash($"{itemId:N}\ncontent\n{content.InfoHash}\n{content.FilePath}\n{content.Length}")
            : null;

    /// <summary>The pre-6B identity: item + provider + provider item/file ids + length.</summary>
    public static string CreateProviderScoped(Guid itemId, DebridCompletedFileHandle handle) =>
        Hash($"{itemId:N}\n{handle.ProviderId.ToUpperInvariant()}\n{handle.RemoteItemId}\n{handle.FileId}\n{handle.ExpectedLength}");

    /// <summary>Every identity under which an existing job for this handle may be stored.</summary>
    public static IReadOnlyList<string> Candidates(Guid itemId, DebridCompletedFileHandle handle)
    {
        var neutral = Create(itemId, handle);
        var fullPath = CreateFullPathScoped(itemId, handle);
        var scoped = CreateProviderScoped(itemId, handle);
        return new[] { neutral, fullPath, scoped }.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Jobs written by the first 6B build are keyed on the provider's full path. Their handle still
    /// carries the content identity, so the current key is recomputed on load; the file itself is
    /// rewritten under the new key the next time the job is upserted.
    /// </summary>
    public static AcquisitionJob UpgradeStoredIdentity(AcquisitionJob job)
    {
        if (job.CompletedFileHandle is not { ContentIdentity: not null } handle)
            return job;
        var current = Create(job.ItemId, handle);
        return string.Equals(job.CacheIdentity, CreateFullPathScoped(job.ItemId, handle), StringComparison.Ordinal)
            && !string.Equals(job.CacheIdentity, current, StringComparison.Ordinal)
            ? job with { CacheIdentity = current }
            : job;
    }

    public static bool Matches(AcquisitionJob job, string cacheIdentity) =>
        string.Equals(job.CacheIdentity, cacheIdentity, StringComparison.Ordinal);

    public static bool MatchesAny(AcquisitionJob job, IReadOnlyList<string> cacheIdentities) =>
        job.CacheIdentity is not null && cacheIdentities.Contains(job.CacheIdentity, StringComparer.Ordinal);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static Guid ToJobId(string cacheIdentity) => new(Convert.FromHexString(cacheIdentity).AsSpan(0, 16));
}

/// <summary>
/// Stores Phase 7 job state below the Jellyfin data directory. Atomic replace means a restart
/// observes either the previous complete record or the next complete record, never partial JSON.
/// </summary>
public sealed class AcquisitionJobStore(IApplicationPaths appPaths)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string RootPath => Path.Combine(appPaths.DataPath, "nebulabridge", "acquisition");
    public string StagingPath => Path.Combine(RootPath, "staging");
    private string JobsPath => Path.Combine(RootPath, "jobs.json");

    public async Task<IReadOnlyList<AcquisitionJob>> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(JobsPath)) return [];
            await using var stream = File.OpenRead(JobsPath);
            var jobs = await JsonSerializer.DeserializeAsync<List<AcquisitionJob>>(stream, JsonOptions, ct).ConfigureAwait(false) ?? [];
            return jobs.Select(AcquisitionJobIdentity.UpgradeStoredIdentity).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertAsync(AcquisitionJob job, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(StagingPath);
            List<AcquisitionJob> jobs = [];
            if (File.Exists(JobsPath))
            {
                await using var stream = File.OpenRead(JobsPath);
                jobs = await JsonSerializer.DeserializeAsync<List<AcquisitionJob>>(stream, JsonOptions, ct).ConfigureAwait(false) ?? [];
            }
            var index = jobs.FindIndex(candidate => candidate.Id == job.Id);
            if (index >= 0) jobs[index] = job; else jobs.Add(job);
            var temporary = JobsPath + ".tmp." + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(jobs, JsonOptions), ct).ConfigureAwait(false);
            File.Move(temporary, JobsPath, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    public async Task<AcquisitionJob?> MutateAsync(
        Guid id,
        Func<AcquisitionJob?, AcquisitionJob?> mutation,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = await ReadJobsUnsafeAsync(ct).ConfigureAwait(false);
            var index = jobs.FindIndex(candidate => candidate.Id == id);
            var updated = mutation(index >= 0 ? jobs[index] : null);
            if (updated is null)
            {
                if (index < 0) return null;
                jobs.RemoveAt(index);
            }
            else if (index >= 0) jobs[index] = updated;
            else jobs.Add(updated);
            await WriteJobsUnsafeAsync(jobs, ct).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public Task<AcquisitionJob> GetOrCreateAsync(
        string cacheIdentity,
        Func<AcquisitionJob> create,
        Func<AcquisitionJob, AcquisitionJob> update,
        CancellationToken ct) => GetOrCreateAsync([cacheIdentity], create, update, ct);

    /// <summary>Matches the first job stored under any of the identities (neutral first, legacy after).</summary>
    public async Task<AcquisitionJob> GetOrCreateAsync(
        IReadOnlyList<string> cacheIdentities,
        Func<AcquisitionJob> create,
        Func<AcquisitionJob, AcquisitionJob> update,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = await ReadJobsUnsafeAsync(ct).ConfigureAwait(false);
            var index = -1;
            foreach (var cacheIdentity in cacheIdentities)
            {
                index = jobs.FindIndex(candidate => AcquisitionJobIdentity.Matches(candidate, cacheIdentity));
                if (index >= 0) break;
            }

            var job = index >= 0 ? update(jobs[index]) : create();
            if (index >= 0) jobs[index] = job; else jobs.Add(job);
            await WriteJobsUnsafeAsync(jobs, ct).ConfigureAwait(false);
            return job;
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = await ReadJobsUnsafeAsync(ct).ConfigureAwait(false);
            jobs.RemoveAll(job => job.Id == id);
            await WriteJobsUnsafeAsync(jobs, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<AcquisitionJob>> ReadJobsUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(JobsPath)) return [];
        await using var stream = File.OpenRead(JobsPath);
        return await JsonSerializer.DeserializeAsync<List<AcquisitionJob>>(stream, JsonOptions, ct).ConfigureAwait(false) ?? [];
    }

    private async Task WriteJobsUnsafeAsync(List<AcquisitionJob> jobs, CancellationToken ct)
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(StagingPath);
        var temporary = JobsPath + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(jobs, JsonOptions), ct).ConfigureAwait(false);
        File.Move(temporary, JobsPath, overwrite: true);
    }
}
