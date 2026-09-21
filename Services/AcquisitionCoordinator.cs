using NebulaBridge.NativeSources;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;

namespace NebulaBridge.Services;

/// <summary>Creates/reuses server-wide playback cache records without storing remote URLs.</summary>
public sealed class AcquisitionCoordinator(
    AcquisitionJobStore store,
    ProgressiveRangeCache cache,
    DebridProviderOrchestrator debrid,
    NativeStreamProxyHttpClient http,
    AcquisitionImportService importer,
    AcquisitionActivityTracker activity,
    CacheStorageSafety storageSafety,
    ILogger<AcquisitionCoordinator> logger
)
{
    /// <summary>Test/legacy shape: providers participate in registration order. Kept internal so DI never sees two same-arity constructors.</summary>
    internal AcquisitionCoordinator(
        AcquisitionJobStore store,
        ProgressiveRangeCache cache,
        IEnumerable<IDebridProvider> providers,
        NativeStreamProxyHttpClient http,
        AcquisitionImportService importer,
        AcquisitionActivityTracker activity,
        CacheStorageSafety storageSafety,
        ILogger<AcquisitionCoordinator> logger)
        : this(store, cache, DebridProviderOrchestrator.FromProviders(providers), http, importer, activity, storageSafety, logger)
    { }

    private const int CompletionChunkBytes = 4 * 1024 * 1024;
    internal const int CompletionRetryAttempts = 5;
    private readonly SemaphoreSlim _completionSlots = new(2, 2);
    private readonly ConcurrentDictionary<Guid, CompletionRun> _completions = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Job ids whose completion this coordinator is currently running.</summary>
    internal IReadOnlyCollection<Guid> RunningCompletions => _completions.Keys.ToArray();

    private ILogger Logger => logger;

    /// <summary>
    /// Starts completing a retained job in the background under this coordinator's ownership:
    /// one run per job, cancellable through <see cref="CancelAsync"/>, and stopped together at
    /// shutdown so no download outlives the host. The returned task is the run itself.
    /// </summary>
    public Task StartCompletion(Guid id)
    {
        if (_shutdown.IsCancellationRequested)
            return Task.CompletedTask;
        var run = _completions.GetOrAdd(id, key => new CompletionRun(key, this));
        return run.Task;
    }

    /// <summary>
    /// Stops completing a retained job and drops the keep intent. The bytes fetched so far stay
    /// on disk as ordinary temporary cache, so a later Keep resumes from them, and the entry ages
    /// out with the rest of the temporary cache if nobody does.
    /// </summary>
    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        if (!Enabled)
            return false;
        var job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == id);
        if (job is null || !CanCancel(job))
            return false;
        if (_completions.TryGetValue(id, out var run))
        {
            run.Cancel();
            await run.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        // Re-checked inside the mutation: the run may have finished and moved the job on.
        var cancelled = await store.MutateAsync(id, current => current is null || !CanCancel(current) ? current : MarkCancelled(current), ct).ConfigureAwait(false);
        if (cancelled?.State != AcquisitionJobState.Cancelled)
            return false;
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "completion-cancelled",
            cancelled.Id,
            new Dictionary<string, object?>
            {
                ["cachedBytes"] = GetCachedBytes(cancelled),
                ["expectedBytes"] = cancelled.ExpectedBytes,
                ["wasRunning"] = run is not null,
            });
        return true;
    }

    /// <summary>
    /// Host shutdown: stop every running completion and wait for it to let go of the store.
    /// Jobs are left in <c>Completing</c> so startup recovery resumes them from the kept bytes.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        _shutdown.Cancel();
        var running = _completions.Values.Select(run => run.Task).ToArray();
        if (running.Length == 0)
            return;
        try
        {
            await Task.WhenAll(running).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("{Count} acquisition completion(s) were still stopping when the host gave up waiting", running.Length);
        }
    }

    internal static bool CanCancel(AcquisitionJob job) =>
        !job.Imported
        && job.Retention != AcquisitionRetention.Temporary
        && job.State is AcquisitionJobState.Queued or AcquisitionJobState.Downloading
            or AcquisitionJobState.Completing or AcquisitionJobState.Failed;

    internal static AcquisitionJob MarkCancelled(AcquisitionJob job)
    {
        var now = DateTimeOffset.UtcNow;
        return job with
        {
            State = AcquisitionJobState.Cancelled,
            Retention = AcquisitionRetention.Temporary,
            ExpiresUtc = now.AddDays(NebulaBridgePlugin.Instance?.Configuration.PlaybackCacheRetentionDays ?? 3),
            Error = null,
            UpdatedUtc = now,
        };
    }

    /// <summary>One owned background completion: its own token, linked to shutdown, removed from the registry when it ends.</summary>
    private sealed class CompletionRun
    {
        private readonly CancellationTokenSource _cts;

        public CompletionRun(Guid id, AcquisitionCoordinator owner)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(owner._shutdown.Token);
            Task = RunAsync(id, owner);
        }

        public Task Task { get; }

        public void Cancel() => _cts.Cancel();

        private async Task RunAsync(Guid id, AcquisitionCoordinator owner)
        {
            // Let the registry entry exist before the run can observe its own completion.
            await Task.Yield();
            try
            {
                await owner.CompleteRetainedAsync(id, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // A user cancel is recorded by CancelAsync; a shutdown leaves the job for recovery.
            }
            catch (Exception ex)
            {
                owner.Logger.LogWarning(ex, "Retained acquisition completion crashed for {JobId}", id);
            }
            finally
            {
                owner._completions.TryRemove(id, out _);
                _cts.Dispose();
            }
        }
    }

    public bool Enabled => EnabledOverride ?? IsLocalAcquisitionEnabled(NebulaBridgePlugin.Instance?.Configuration);

    /// <summary>Test seam: stands in for the plugin configuration, which tests cannot construct.</summary>
    internal bool? EnabledOverride { get; set; }

    internal static bool IsLocalAcquisitionEnabled(PluginConfiguration? configuration) =>
        configuration?.EnableLocalAcquisition == true;

    public Task<IAsyncDisposable?> BeginSourceUseAsync(
        NativeStreamProxyRegistry registry,
        string proxyKey,
        CancellationToken ct)
    {
        if (!registry.TryGetAcquisitionIdentity(proxyKey, out var identity) || identity.ItemId is not { } itemId)
            return Task.FromResult<IAsyncDisposable?>(null);
        return AcquireAsync(itemId, ct);

        async Task<IAsyncDisposable?> AcquireAsync(Guid id, CancellationToken token) =>
            await activity.AcquireUseAsync(id, token).ConfigureAwait(false);
    }

    public async Task<AcquisitionPlaybackSession?> BeginPlaybackAsync(
        NativeStreamProxyRegistry registry, string proxyKey, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return null;
        if (!registry.TryGetAcquisitionIdentity(proxyKey, out var identity) || identity.ItemId is not { } itemId
            || identity.CompletedFileHandle is not { } handle)
            return null;
        // Neutral identity first (same bytes via any provider), then the provider-scoped identity
        // older jobs were stored under, so an existing TorBox job is reused rather than duplicated.
        var identities = AcquisitionJobIdentity.Candidates(itemId, handle);
        var cacheIdentity = identities[0];
        var existingId = (await store.ReadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => AcquisitionJobIdentity.MatchesAny(candidate, identities))?.Id;
        var jobId = existingId ?? AcquisitionJobIdentity.ToJobId(cacheIdentity);
        var itemUse = await activity.AcquireUseAsync(itemId, cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? jobUse = null;
        try
        {
            if (jobId != itemId)
                jobUse = await activity.AcquireUseAsync(jobId, cancellationToken).ConfigureAwait(false);
            var jobs = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var expiry = now.AddDays(NebulaBridgePlugin.Instance?.Configuration.PlaybackCacheRetentionDays ?? 3);
            var inherited = !string.IsNullOrWhiteSpace(identity.SeriesId) && jobs.Any(candidate => candidate.SeriesId == identity.SeriesId && candidate.Retention == AcquisitionRetention.KeepSeries);
            var selectedFileName = GetSelectedFileName(handle);
            var job = await store.GetOrCreateAsync(identities,
                () => new AcquisitionJob(jobId, itemId, identity.ProviderId, identity.SourceId,
                    selectedFileName, AcquisitionJobState.Queued, now, now,
                    LastAccessUtc: now, ExpiresUtc: inherited ? null : expiry, Retention: inherited ? AcquisitionRetention.KeepSeries : AcquisitionRetention.Temporary, SeriesId: identity.SeriesId, ExpectedBytes: handle.ExpectedLength, CompletedFileHandle: handle, CacheIdentity: cacheIdentity, ProxyKey: proxyKey),
                existing => AttachHandle(existing, handle, cacheIdentity) with
                {
                    LastAccessUtc = now,
                    ExpiresUtc = existing.Retention == AcquisitionRetention.Temporary ? expiry : null,
                    ProxyKey = proxyKey,
                    UpdatedUtc = now,
                }, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(job.ProxyKey))
            {
                job = await store.MutateAsync(job.Id, current => current is null ? null : current with
                {
                    ProxyKey = proxyKey,
                    UpdatedUtc = now,
                }, cancellationToken).ConfigureAwait(false) ?? job;
            }
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "acquisition-job",
                job.Id,
                new Dictionary<string, object?>
                {
                    ["action"] = job.CreatedUtc == now ? "created" : "reused",
                    ["itemId"] = itemId.ToString("N"),
                    ["state"] = job.State.ToString(),
                    ["retention"] = job.Retention.ToString(),
                    ["cachedBytes"] = GetCachedBytes(job),
                    ["expectedBytes"] = job.ExpectedBytes,
                });
            if (job.Imported)
            {
                if (jobUse is not null) await jobUse.DisposeAsync().ConfigureAwait(false);
                await itemUse.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            if (ShouldCompleteAfterPlayback(job))
                _ = StartCompletion(job.Id); // owned via _completions; observed by CancelAsync/ShutdownAsync
            return new AcquisitionPlaybackSession(job, itemUse, jobUse);
        }
        catch
        {
            if (jobUse is not null) await jobUse.DisposeAsync().ConfigureAwait(false);
            await itemUse.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<IReadOnlyList<AcquisitionJob>> ListAsync(CancellationToken ct) => store.ReadAsync(ct);

    public async Task<bool> RestoreReplaySourceAsync(
        NativeStreamProxyRegistry registry,
        string proxyKey,
        CancellationToken ct)
    {
        if (registry.TryGetAcquisitionIdentity(proxyKey, out _))
            return true;
        var now = DateTimeOffset.UtcNow;
        var job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate =>
            string.Equals(candidate.ProxyKey, proxyKey, StringComparison.OrdinalIgnoreCase)
            && IsReplayable(candidate, now));
        if (job?.CompletedFileHandle is not { } handle)
            return false;
        var restored = registry.RestoreCompletedFile(
            proxyKey,
            handle,
            job.ItemId,
            job.SeriesId,
            job.AlternateHandles);
        if (restored)
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "provider-source-restored",
                job.Id,
                new Dictionary<string, object?>
                {
                    ["itemId"] = job.ItemId.ToString("N"),
                    ["provider"] = handle.ProviderId,
                    ["cachedBytes"] = GetCachedBytes(job),
                });
        return restored;
    }

    public async Task<int> RestoreReplaySourcesAsync(
        NativeStreamProxyRegistry registry,
        Guid itemId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = await store.ReadAsync(ct).ConfigureAwait(false);
        var restored = 0;
        foreach (var job in jobs.Where(candidate => candidate.ItemId == itemId
            && IsReplayable(candidate, now)
            && !string.IsNullOrWhiteSpace(candidate.ProxyKey)
            && candidate.CompletedFileHandle is not null))
        {
            // Hosted run: a job owned by a since-disabled provider was restored, discovery was
            // skipped on its account, and the only source 404'd. Restore it regardless (the
            // provider may come back) but only a servable source may replace discovery.
            if (registry.RestoreCompletedFile(
                job.ProxyKey!,
                job.CompletedFileHandle!,
                job.ItemId,
                job.SeriesId,
                job.AlternateHandles)
                && registry.CanServeCompletedFile(job.CompletedFileHandle!, job.AlternateHandles))
                restored++;
        }
        return restored;
    }

    public async Task<bool> RetainAsync(Guid id, AcquisitionRetention retention, CancellationToken ct)
    {
        if (!Enabled || retention == AcquisitionRetention.Temporary)
            return false;
        await using var transition = await activity.AcquireExclusiveAsync(id, ct).ConfigureAwait(false);
        var job = await store.MutateAsync(id, current => current is null ? null : current with
        { Retention = retention, ExpiresUtc = null, UpdatedUtc = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        if (job is null) return false;
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "retention-changed",
            job.Id,
            new Dictionary<string, object?>
            {
                ["retention"] = retention.ToString(),
                ["seriesId"] = job.SeriesId,
            });
        var completionIds = new List<Guid> { id };
        if (retention == AcquisitionRetention.KeepSeries && !string.IsNullOrWhiteSpace(job.SeriesId))
        {
            var allJobs = await store.ReadAsync(ct).ConfigureAwait(false);
            completionIds = [.. GetCompletionIds(id, retention, job, allJobs)];
            foreach (var related in allJobs.Where(candidate => candidate.SeriesId == job.SeriesId && candidate.Retention == AcquisitionRetention.Temporary))
            {
                await using var relatedTransition = await activity.AcquireExclusiveAsync(related.Id, ct).ConfigureAwait(false);
                await store.MutateAsync(related.Id, current => current is null ? null : current with
                { Retention = retention, ExpiresUtc = null, UpdatedUtc = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
            }
        }
        foreach (var completionId in completionIds.Distinct())
            _ = StartCompletion(completionId);
        return true;
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken ct)
    {
        if (!Enabled)
            return false;
        var job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate =>
            candidate.Id == id
            && candidate.Retention != AcquisitionRetention.Temporary
            && candidate.State == AcquisitionJobState.Failed);
        if (job is null)
            return false;
        _ = StartCompletion(id);
        return true;
    }

    internal static IReadOnlyList<Guid> GetCompletionIds(
        Guid selectedId,
        AcquisitionRetention retention,
        AcquisitionJob selected,
        IEnumerable<AcquisitionJob> jobs)
    {
        if (retention != AcquisitionRetention.KeepSeries || string.IsNullOrWhiteSpace(selected.SeriesId))
            return [selectedId];

        return [.. new[] { selectedId }.Concat(jobs
            .Where(job => job.SeriesId == selected.SeriesId && job.Retention == AcquisitionRetention.Temporary)
            .Select(job => job.Id)
            )
            .Distinct()];
    }

    public async Task CompleteRetainedAsync(Guid id, CancellationToken ct)
    {
        if (!Enabled)
            return;
        await _completionSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var readyToImport = false;
            await using (await activity.AcquireUseAsync(id, ct).ConfigureAwait(false))
            {
                var job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == id);
                if (job is null || job.Retention == AcquisitionRetention.Temporary
                    || job.State is AcquisitionJobState.Importing or AcquisitionJobState.Imported
                    || job.ExpectedBytes is not > 0 || job.CompletedFileHandle is not { } handle) return;
                var routes = GetCompletionRoutes(job);
                if (routes.Count == 0)
                {
                    NebulaBridgeFileLog.Write(
                        NebulaLogVerbosity.Information,
                        "completion-skipped",
                        job.Id,
                        new Dictionary<string, object?>
                        {
                            ["reason"] = "no_usable_provider",
                            ["owner"] = handle.ProviderId,
                            ["alternates"] = string.Join(",", (job.AlternateHandles ?? []).Select(alternate => alternate.ProviderId)),
                        });
                    return;
                }
                job = await store.MutateAsync(id, current => current is null ? null : MarkCompleting(current), ct).ConfigureAwait(false) ?? job;
                NebulaBridgeFileLog.Write(
                    NebulaLogVerbosity.Information,
                    "completion-start",
                    job.Id,
                    new Dictionary<string, object?>
                    {
                        ["cachedBytes"] = GetCachedBytes(job),
                        ["expectedBytes"] = job.ExpectedBytes,
                    });
                try
                {
                    DebridCompletedFileSource? source = null;
                    var routeIndex = 0;
                    for (long start = 0; start < job.ExpectedBytes; start += CompletionChunkBytes)
                    {
                        var length = Math.Min(CompletionChunkBytes, job.ExpectedBytes.Value - start);
                        if ((job.CachedRanges ?? []).Any(range => range.Contains(start, start + length - 1))) continue;
                        (source, routeIndex) = await FetchChunkWithRetryAsync(routes, routeIndex, source, job, start, length, ct).ConfigureAwait(false);
                        job = (await store.ReadAsync(ct).ConfigureAwait(false)).First(candidate => candidate.Id == id);
                        NebulaBridgeFileLog.Write(
                            NebulaLogVerbosity.Debug,
                            "completion-progress",
                            job.Id,
                            new Dictionary<string, object?>
                            {
                                ["cachedBytes"] = GetCachedBytes(job),
                                ["expectedBytes"] = job.ExpectedBytes,
                            });
                    }
                    await store.MutateAsync(id, current => current is null ? null : current with
                    { State = AcquisitionJobState.ReadyToImport, UpdatedUtc = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
                    readyToImport = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Cancelled by the operator or by shutdown: the state is decided by the
                    // caller (Cancelled, or left Completing for recovery), never Failed.
                    throw;
                }
                catch (Exception ex) when (IsRetryableCompletionException(ex))
                {
                    logger.LogWarning(
                        "Retained acquisition completion failed for {JobId} ({FailureType})",
                        id,
                        ex.GetType().Name);
                    await store.MutateAsync(id, current => current is null ? null : current with
                    { State = AcquisitionJobState.Failed, Error = ex.GetType().Name, UpdatedUtc = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
                }
            }
            if (readyToImport) await importer.ImportAsync(id, ct).ConfigureAwait(false);
        }
        finally { _completionSlots.Release(); }
    }

    private async Task<(DebridCompletedFileSource Source, int RouteIndex)> FetchChunkWithRetryAsync(
        IReadOnlyList<(IDebridProvider Provider, DebridCompletedFileHandle Handle)> routes,
        int routeIndex,
        DebridCompletedFileSource? source,
        AcquisitionJob job,
        long start,
        long length,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (provider, handle) = routes[routeIndex % routes.Count];
            try
            {
                if (source is null)
                {
                    source = await provider.ResolveCompletedFileSourceAsync(handle, ct).ConfigureAwait(false);
                    if (source is not null && job.ExpectedBytes is not null && source.ExpectedLength is not null && source.ExpectedLength != job.ExpectedBytes)
                        throw new InvalidOperationException("Provider completed-file length does not match the job.");
                }

                if (source is null || !source.SupportsRanges) throw new InvalidOperationException("Provider cannot supply a ranged completed-file source.");
                if (!storageSafety.CanWrite(length)) throw new IOException("Playback cache free-space floor reached.");
                await cache.ReadOrFetchAsync(job, start, length, async (offset, count, token) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + count - 1);
                    using var response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new HttpRequestException("Completed-file source expired.");
                    var plan = CacheHttpResponseValidator.Validate(response.StatusCode, response.Content.Headers.ContentRange,
                        response.Content.Headers.ContentLength, response.Content.Headers.ContentType?.MediaType,
                        offset, count, job.ExpectedBytes);
                    if (plan is null) throw new IOException("Provider returned an invalid byte-range response.");
                    return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                }, store, ct).ConfigureAwait(false);
                return (source, routeIndex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsRetryableCompletionException(ex))
            {
                if (attempt >= CompletionRetryAttempts) throw;
                // A completed-file URL is deliberately ephemeral. Re-resolve only after a
                // failed transfer instead of calling the provider once per four-megabyte chunk;
                // the latter can exhaust provider request-link rate limits during one import.
                // Every route serves the same bytes (same infohash/path/length), so rotating to
                // an alternate provider on retry never mixes content within a job.
                source = null;
                if (routes.Count > 1)
                {
                    var next = (routeIndex + 1) % routes.Count;
                    NebulaBridgeFileLog.Write(
                        NebulaLogVerbosity.Information,
                        "debrid-failover",
                        job.Id,
                        new Dictionary<string, object?>
                        {
                            ["stage"] = "completion",
                            ["from"] = provider.Id,
                            ["to"] = routes[next].Provider.Id,
                            ["failureType"] = ex.GetType().Name,
                        });
                    routeIndex = next;
                }
                NebulaBridgeFileLog.Write(
                    NebulaLogVerbosity.Debug,
                    "completion-retry",
                    job.Id,
                    new Dictionary<string, object?>
                    {
                        ["attempt"] = attempt,
                        ["failureType"] = ex.GetType().Name,
                        ["start"] = start,
                        ["length"] = length,
                    });
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), ct).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsRetryableCompletionException(Exception exception) =>
        exception is HttpRequestException or IOException or InvalidOperationException or JsonException
            or TaskCanceledException;

    internal static AcquisitionJob MarkCompleting(AcquisitionJob job) => job with
    {
        State = AcquisitionJobState.Completing,
        Error = null,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    internal static bool ShouldCompleteAfterPlayback(AcquisitionJob job) =>
        job.Retention != AcquisitionRetention.Temporary && !job.Imported;

    public async Task<bool> PurgeAsync(Guid id, CancellationToken ct)
    {
        await using var transition = await activity.AcquireExclusiveAsync(id, ct).ConfigureAwait(false);
        var job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(job => job.Id == id);
        if (job is null || !CanPurge(job)) return false;
        var path = Path.Combine(store.StagingPath, id.ToString("N"));
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        await store.RemoveAsync(id, ct).ConfigureAwait(false);
        return true;
    }

    internal static bool CanPurge(AcquisitionJob job) =>
        job.Retention == AcquisitionRetention.Temporary
        && job.State is AcquisitionJobState.Queued or AcquisitionJobState.Failed
            or AcquisitionJobState.Cancelled or AcquisitionJobState.Completed;

    internal static bool IsReplayable(AcquisitionJob job, DateTimeOffset now) =>
        !job.Imported
        && job.State is not (AcquisitionJobState.Importing or AcquisitionJobState.Imported)
        && (job.Retention != AcquisitionRetention.Temporary || job.ExpiresUtc is null || job.ExpiresUtc > now);

    internal static long GetCachedBytes(AcquisitionJob job) =>
        (job.CachedRanges ?? []).Sum(range => checked(range.End - range.Start + 1));

    /// <summary>
    /// Records a provider's handle for an existing job without changing the job's identity or
    /// owner. The identity is upgraded to the neutral form when the new handle proves content; a
    /// handle from another provider is kept as an alternate route for completion and replay.
    /// </summary>
    internal static AcquisitionJob AttachHandle(AcquisitionJob job, DebridCompletedFileHandle handle, string cacheIdentity)
    {
        var owner = job.CompletedFileHandle;
        if (owner is null)
            return job with { CompletedFileHandle = handle, CacheIdentity = cacheIdentity };
        if (owner.ExpectedLength is not null && handle.ExpectedLength is not null && owner.ExpectedLength != handle.ExpectedLength)
            return job;
        var upgraded = handle.ContentIdentity is not null && job.CacheIdentity != cacheIdentity ? cacheIdentity : job.CacheIdentity;
        if (string.Equals(owner.ProviderId, handle.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            // Same provider: prefer the richer handle (older jobs stored no infohash/path).
            var refreshed = owner.ContentIdentity is null && handle.ContentIdentity is not null ? handle : owner;
            return job with { CompletedFileHandle = refreshed, CacheIdentity = upgraded };
        }

        var alternates = (job.AlternateHandles ?? [])
            .Where(existing => !string.Equals(existing.ProviderId, handle.ProviderId, StringComparison.OrdinalIgnoreCase))
            .Append(handle)
            .ToList();
        return job with { AlternateHandles = alternates, CacheIdentity = upgraded };
    }

    /// <summary>
    /// Ordered (provider, handle) routes able to reopen the job's bytes right now: the owner
    /// first, then alternates whose provider is usable and whose length matches.
    /// </summary>
    internal IReadOnlyList<(IDebridProvider Provider, DebridCompletedFileHandle Handle)> GetCompletionRoutes(AcquisitionJob job)
    {
        var routes = new List<(IDebridProvider, DebridCompletedFileHandle)>();
        if (job.CompletedFileHandle is not { } owner)
            return routes;
        foreach (var handle in new[] { owner }.Concat(job.AlternateHandles ?? []))
        {
            if (!debrid.IsUsable(handle.ProviderId, out var provider) || provider is null
                || !provider.Capabilities.HasFlag(DebridProviderCapabilities.CompletedFileSource))
                continue;
            if (!ReferenceEquals(handle, owner) && owner.ExpectedLength is not null && handle.ExpectedLength != owner.ExpectedLength)
                continue;
            routes.Add((provider, handle));
        }

        return routes;
    }

    internal static string GetSelectedFileName(DebridCompletedFileHandle handle)
    {
        var fileName = Path.GetFileName(handle.FileName);
        return string.IsNullOrWhiteSpace(fileName) ? "media" : fileName;
    }
}

public sealed class AcquisitionPlaybackSession(
    AcquisitionJob job,
    IAsyncDisposable itemLease,
    IAsyncDisposable? jobLease) : IAsyncDisposable
{
    public AcquisitionJob Job { get; } = job;
    public async ValueTask DisposeAsync()
    {
        if (jobLease is not null) await jobLease.DisposeAsync().ConfigureAwait(false);
        await itemLease.DisposeAsync().ConfigureAwait(false);
    }
}
