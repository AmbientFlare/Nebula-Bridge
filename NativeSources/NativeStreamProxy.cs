using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NebulaBridge.NativeSources;

/// <summary>
/// Keeps provider download links inside the Jellyfin process. MediaSource paths contain only a
/// loopback URL with an opaque stable key, so signed provider URLs and query tokens never appear
/// in Jellyfin client responses or ffmpeg command logs.
/// </summary>
public sealed class NativeStreamProxyRegistry
{
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, ProxyEntry> _entries = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly DebridProviderOrchestrator _debrid;

    public NativeStreamProxyRegistry()
        : this(Array.Empty<IDebridProvider>())
    { }

    /// <summary>Test/legacy shape: providers participate in registration order. Kept internal so DI never sees two same-arity constructors.</summary>
    internal NativeStreamProxyRegistry(IEnumerable<IDebridProvider> providers)
        : this(DebridProviderOrchestrator.FromProviders(providers))
    { }

    public NativeStreamProxyRegistry(DebridProviderOrchestrator debrid)
    {
        _debrid = debrid;
    }

    public static bool IsProxyUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !uri.IsLoopback
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        const string prefix = "/nebulabridge/native-stream/";
        var key = uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath[prefix.Length..]
            : string.Empty;
        return key.Length == 32 && key.All(Uri.IsHexDigit);
    }

    public bool IsRegisteredProxyUri(string? value)
    {
        if (!IsProxyUri(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        var key = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
        return _entries.TryGetValue(key, out var entry) && entry.Touch(DateTimeOffset.UtcNow);
    }

    public Uri Register(NativeResolvedStream stream, int jellyfinHttpPort)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PruneExpired(DateTimeOffset.UtcNow);
        var identity = $"{stream.SourceId}\n{stream.Name}\n{stream.Filename}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var key = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        _entries[key] = new ProxyEntry(stream.Url, null, DateTimeOffset.UtcNow);
        return new Uri(
            $"http://127.0.0.1:{jellyfinHttpPort}/nebulabridge/native-stream/{key}",
            UriKind.Absolute
        );
    }

    public Uri Register(NativePreparedStream stream, int jellyfinHttpPort, Guid? itemId = null, string? seriesId = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PruneExpired(DateTimeOffset.UtcNow);
        if (stream.DirectUrl is null && stream.DebridRequest is null)
        {
            throw new ArgumentException("A prepared stream requires a direct URL or debrid request.", nameof(stream));
        }

        // Transport is deliberately absent from the identity: the same release/file keeps one
        // proxy key no matter which provider ends up serving it, so provider priority changes
        // and failovers never create duplicate Jellyfin media sources.
        var requestIdentity = stream.DebridRequest is null
            ? stream.DirectUrl?.AbsoluteUri
            : $"{stream.DebridRequest.Candidate.InfoHash}\n"
                + $"{stream.DebridRequest.Query.Season}\n{stream.DebridRequest.Query.Episode}";
        // The same provider bytes can appear as more than one Jellyfin logical item (for example,
        // a raw discovery row and its later metadata-backed movie). Keep their proxy/acquisition
        // attribution distinct even when the release/file identity is otherwise identical.
        var identity = $"{itemId:N}\n{stream.SourceId}\n{stream.Name}\n{stream.Filename}\n{requestIdentity}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var key = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        _entries.AddOrUpdate(
            key,
            _ => new ProxyEntry(stream.DirectUrl, stream.DebridRequest, DateTimeOffset.UtcNow, itemId, seriesId),
            (_, existing) => existing.Update(stream.DirectUrl, stream.DebridRequest, DateTimeOffset.UtcNow, itemId, seriesId)
        );
        return new Uri(
            $"http://127.0.0.1:{jellyfinHttpPort}/nebulabridge/native-stream/{key}",
            UriKind.Absolute
        );
    }

    public bool RestoreCompletedFile(
        string key,
        DebridCompletedFileHandle handle,
        Guid itemId,
        string? seriesId,
        IReadOnlyList<DebridCompletedFileHandle>? alternates = null)
    {
        // Any registered handle qualifies, even one whose provider is currently disabled: the
        // entry may still be served through an alternate handle, and the disabled provider
        // may be re-enabled later. Usability is decided at resolve time.
        var handles = new[] { handle }.Concat(alternates ?? []).ToList();
        if (key.Length != 32 || !key.All(Uri.IsHexDigit)
            || !handles.Any(h => _debrid.TryGetProvider(h.ProviderId) is not null))
            return false;
        _entries.AddOrUpdate(
            key,
            _ => new ProxyEntry(null, null, DateTimeOffset.UtcNow, itemId, seriesId, handle, alternates),
            (_, existing) => existing.UpdateCompletedFile(
                DateTimeOffset.UtcNow,
                itemId,
                seriesId,
                handle,
                alternates));
        return true;
    }

    /// <summary>
    /// Whether a completed file could be served right now: some handle belongs to a provider that
    /// is enabled, configured and healthy. A restored entry that fails this is kept for later but
    /// must not stand in for discovery, or the item is unplayable until the job expires.
    /// </summary>
    public bool CanServeCompletedFile(
        DebridCompletedFileHandle handle,
        IReadOnlyList<DebridCompletedFileHandle>? alternates = null) =>
        new[] { handle }.Concat(alternates ?? []).Any(h => _debrid.IsUsable(h.ProviderId));

    public bool TryGetAcquisitionIdentity(string key, out NativeProxyAcquisitionIdentity identity)
    {
        identity = default!;
        if (!_entries.TryGetValue(key, out var entry) || !entry.Touch(DateTimeOffset.UtcNow)) return false;
        identity = new(
            entry.ItemId,
            entry.Request?.Provider ?? entry.CompletedFileHandle?.ProviderId ?? "direct",
            entry.Request?.Candidate.SourceId ?? "durable",
            entry.Filename,
            entry.SeriesId,
            entry.CompletedFileHandle ?? entry.Resolved?.CompletedFileHandle);
        return entry.ItemId is not null;
    }

    public bool TryGetTarget(string key, out Uri? target)
    {
        target = null;
        return key.Length == 32
            && key.All(Uri.IsHexDigit)
            && _entries.TryGetValue(key, out var entry)
            && entry.Touch(DateTimeOffset.UtcNow)
            && (target = entry.DirectTarget) is not null;
    }

    public void MarkProbe(string key)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.Touch(DateTimeOffset.UtcNow))
            entry.Probed = true;
    }

    public bool ShouldBeginUnrangedPlayback(string key)
    {
        if (!_entries.TryGetValue(key, out var entry) || !entry.Touch(DateTimeOffset.UtcNow))
            return false;
        if (!entry.Probed)
        {
            entry.Probed = true;
            return false;
        }
        return true;
    }

    public Task<DebridPlaybackResult> ResolveTargetAsync(
        string key,
        bool forceRefresh,
        CancellationToken cancellationToken
    ) => ResolveTargetAsync(key, forceRefresh, null, cancellationToken);

    /// <summary>
    /// The provider currently serving the key's resolved URL stalled or errored at the HTTP
    /// layer. Reports it unhealthy (so every route skips it for the backoff window), drops the
    /// cached URL and re-resolves through the remaining routes. A direct target has no
    /// alternate and simply fails.
    /// </summary>
    public async Task<DebridPlaybackResult> FailOverAsync(
        string key,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!TryGetEntry(key, out var entry)
            || entry.DirectTarget is not null
            || entry.ResolvedProviderId is not { } failed)
        {
            return new DebridPlaybackResult(
                null,
                new NativeSourceFailure("proxy", "The stream has no alternate route.", Stage: "upstream", Reason: reason));
        }

        _debrid.Health.ReportFailure(failed, reason);
        var result = await ResolveTargetAsync(key, true, failed, cancellationToken).ConfigureAwait(false);
        Services.NebulaBridgeFileLog.Write(
            Config.NebulaLogVerbosity.Information,
            "stream-failover",
            entry.ItemId,
            new Dictionary<string, object?>
            {
                ["failed"] = failed,
                ["reason"] = reason,
                ["chosen"] = result.Stream is null ? null : entry.ResolvedProviderId,
                ["fileName"] = entry.Filename,
            });
        return result;
    }

    private bool TryGetEntry(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProxyEntry? entry)
    {
        entry = null;
        return key.Length == 32
            && key.All(Uri.IsHexDigit)
            && _entries.TryGetValue(key, out entry)
            && entry.Touch(DateTimeOffset.UtcNow);
    }

    private async Task<DebridPlaybackResult> ResolveTargetAsync(
        string key,
        bool forceRefresh,
        string? excludeProviderId,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetEntry(key, out var entry))
        {
            return new DebridPlaybackResult(
                null,
                new NativeSourceFailure("proxy", "The stream selection was not found.", Stage: "selection", Reason: "not_found")
            );
        }

        if (entry.DirectTarget is not null)
        {
            return new DebridPlaybackResult(
                new NativeResolvedStream("direct", "Direct source", entry.DirectTarget)
            );
        }

        if (entry.Request is null && entry.CompletedFileHandle is null)
        {
            return new DebridPlaybackResult(
                null,
                new NativeSourceFailure("proxy", "The debrid provider is unavailable.", Stage: "provider", Reason: "not_found")
            );
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (
                !forceRefresh
                && entry.Resolved is not null
                && entry.ResolvedUntilUtc > DateTimeOffset.UtcNow
            )
            {
                return new DebridPlaybackResult(entry.Resolved);
            }

            DebridPlaybackResult result;
            string? resolvedBy;
            if (entry.Request is not null)
            {
                var request = entry.Request;
                // Once any provider has resolved the file, later refreshes and failovers are
                // pinned to that exact infohash/path/length even if the pipeline had no pin.
                var pinned = request.PinnedFile ?? entry.Resolved?.CompletedFileHandle?.ContentIdentity;
                if (pinned is not null && request.PinnedFile is null)
                    entry.Request = request = request with { PinnedFile = pinned };
                var routed = await RoutePlaybackAsync(request, excludeProviderId, cancellationToken).ConfigureAwait(false);
                resolvedBy = routed.Provider?.Id;
                result = routed.Stream is not null
                    ? new DebridPlaybackResult(routed.Stream)
                    : new DebridPlaybackResult(
                        null,
                        new NativeSourceFailure(
                            "proxy",
                            "No enabled debrid provider could serve the selected file.",
                            Stage: "provider",
                            Reason: routed.Attempts.Count == 0 ? "not_found" : "failed"));
            }
            else
            {
                result = await ResolveCompletedFileAsync(entry, excludeProviderId, cancellationToken).ConfigureAwait(false);
                resolvedBy = result.Stream?.SourceId;
            }

            if (result.Stream is not null)
            {
                entry.Resolved = result.Stream;
                entry.ResolvedProviderId = resolvedBy;
                // Provider URLs are temporary; this stays in memory and is refreshed before expiry.
                entry.ResolvedUntilUtc = DateTimeOffset.UtcNow.AddHours(2);
            }
            else if (excludeProviderId is not null)
            {
                // The excluded provider's URL is what failed; do not hand it out again.
                entry.Resolved = null;
                entry.ResolvedProviderId = null;
                entry.ResolvedUntilUtc = default;
            }

            return result;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Routes through the orchestrator, leaving out the provider whose URL just failed.</summary>
    private Task<DebridRoutedPlayback> RoutePlaybackAsync(
        DebridPlaybackRequest request,
        string? excludeProviderId,
        CancellationToken cancellationToken)
    {
        if (excludeProviderId is null)
            return _debrid.ResolvePlaybackAsync(request, cancellationToken);
        var routes = request.Routes
            .Where(r => !string.Equals(r, excludeProviderId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (routes.Count == 0)
            return Task.FromResult(new DebridRoutedPlayback(null, null, []));
        return _debrid.ResolvePlaybackAsync(
            request with { Provider = routes[0], Alternates = routes.Skip(1).ToList() },
            cancellationToken);
    }

    /// <summary>
    /// Reopens a retained completed file via its owning handle first, then any alternate handle
    /// whose provider is usable and whose expected length matches. Handles are provider-specific
    /// identities of the same bytes; a length mismatch disqualifies the alternate outright.
    /// </summary>
    private async Task<DebridPlaybackResult> ResolveCompletedFileAsync(
        ProxyEntry entry,
        string? excludeProviderId,
        CancellationToken cancellationToken)
    {
        var owner = entry.CompletedFileHandle!;
        var attempts = new List<DebridRouteAttempt>();
        foreach (var handle in new[] { owner }.Concat(entry.AlternateHandles ?? []))
        {
            if (string.Equals(handle.ProviderId, excludeProviderId, StringComparison.OrdinalIgnoreCase))
            {
                attempts.Add(new DebridRouteAttempt(handle.ProviderId, "skipped", "upstream_failed"));
                continue;
            }

            if (!_debrid.IsUsable(handle.ProviderId, out var provider) || provider is null)
            {
                attempts.Add(new DebridRouteAttempt(handle.ProviderId, "skipped", provider is null ? "unregistered" : "unavailable"));
                continue;
            }

            if (!provider.Capabilities.HasFlag(DebridProviderCapabilities.CompletedFileSource))
            {
                attempts.Add(new DebridRouteAttempt(handle.ProviderId, "skipped", "no_completed_file_source"));
                continue;
            }

            if (!ReferenceEquals(handle, owner)
                && owner.ExpectedLength is not null
                && handle.ExpectedLength != owner.ExpectedLength)
            {
                attempts.Add(new DebridRouteAttempt(handle.ProviderId, "rejected", "length_mismatch"));
                continue;
            }

            DebridCompletedFileSource? source;
            try
            {
                source = await provider.ResolveCompletedFileSourceAsync(handle, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _debrid.Health.ReportFailure(provider.Id, "resolve_exception");
                attempts.Add(new DebridRouteAttempt(handle.ProviderId, "failed", ex.GetType().Name));
                continue;
            }

            if (source is null)
            {
                attempts.Add(new DebridRouteAttempt(handle.ProviderId, "failed", "not_found"));
                continue;
            }

            if (owner.ExpectedLength is not null && source.ExpectedLength is not null && source.ExpectedLength != owner.ExpectedLength)
            {
                attempts.Add(new DebridRouteAttempt(handle.ProviderId, "rejected", "length_mismatch"));
                continue;
            }

            attempts.Add(new DebridRouteAttempt(handle.ProviderId, "resolved", null));
            if (attempts.Count > 1)
            {
                Services.NebulaBridgeFileLog.Write(
                    Config.NebulaLogVerbosity.Information,
                    "debrid-failover",
                    fields: new Dictionary<string, object?>
                    {
                        ["stage"] = "completed_file",
                        ["infoHash"] = owner.InfoHash,
                        ["chosen"] = provider.Id,
                        ["attempts"] = DebridProviderOrchestrator.FormatAttempts(attempts),
                    });
            }

            return new DebridPlaybackResult(
                new NativeResolvedStream(
                    provider.Id,
                    handle.FileName,
                    source.Url,
                    source.ExpectedLength,
                    handle.FileName,
                    handle));
        }

        return new DebridPlaybackResult(
            null,
            new NativeSourceFailure(
                owner.ProviderId,
                "The completed file could not be reopened.",
                Stage: "completed_file",
                Reason: attempts.All(a => a.Outcome == "skipped") ? "not_found" : "failed"));
    }

    internal int PruneExpired(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastAccessUtc + EntryLifetime >= now)
            {
                continue;
            }

            if (((ICollection<KeyValuePair<string, ProxyEntry>>)_entries).Remove(new(key, entry)))
            {
                removed++;
            }
        }

        return removed;
    }

    private sealed class ProxyEntry(
        Uri? directTarget,
        DebridPlaybackRequest? request,
        DateTimeOffset now,
        Guid? itemId = null,
        string? seriesId = null,
        DebridCompletedFileHandle? completedFileHandle = null,
        IReadOnlyList<DebridCompletedFileHandle>? alternateHandles = null)
    {
        public Uri? DirectTarget { get; private set; } = directTarget;

        public DebridPlaybackRequest? Request { get; set; } = request;

        public IReadOnlyList<DebridCompletedFileHandle>? AlternateHandles { get; private set; } = alternateHandles;

        public NativeResolvedStream? Resolved { get; set; }

        /// <summary>Debrid provider that produced <see cref="Resolved"/>; null for direct targets.</summary>
        public string? ResolvedProviderId { get; set; }

        public DateTimeOffset ResolvedUntilUtc { get; set; }

        public DateTimeOffset LastAccessUtc { get; private set; } = now;

        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Probed { get; set; }
        public Guid? ItemId { get; private set; } = itemId;
        public string? SeriesId { get; private set; } = seriesId;
        public DebridCompletedFileHandle? CompletedFileHandle { get; private set; } = completedFileHandle;
        public string? Filename => Request?.Candidate.Title ?? CompletedFileHandle?.FileName;

        public bool Touch(DateTimeOffset now)
        {
            if (LastAccessUtc + EntryLifetime < now)
            {
                return false;
            }

            LastAccessUtc = now;
            return true;
        }

        public ProxyEntry Update(Uri? target, DebridPlaybackRequest? newRequest, DateTimeOffset now, Guid? itemId = null, string? seriesId = null)
        {
            DirectTarget = target;
            Request = newRequest;
            CompletedFileHandle = null;
            AlternateHandles = null;
            if (target is not null)
            {
                Resolved = null;
                ResolvedProviderId = null;
                ResolvedUntilUtc = default;
            }

            LastAccessUtc = now;
            ItemId ??= itemId;
            SeriesId ??= seriesId;

            return this;
        }

        public ProxyEntry UpdateCompletedFile(
            DateTimeOffset now,
            Guid itemId,
            string? seriesId,
            DebridCompletedFileHandle handle,
            IReadOnlyList<DebridCompletedFileHandle>? alternates)
        {
            DirectTarget = null;
            Request = null;
            Resolved = null;
            ResolvedProviderId = null;
            ResolvedUntilUtc = default;
            LastAccessUtc = now;
            ItemId = itemId;
            SeriesId = seriesId;
            CompletedFileHandle = handle;
            AlternateHandles = alternates;
            return this;
        }
    }
}

public sealed record NativeProxyAcquisitionIdentity(Guid? ItemId, string ProviderId, string SourceId, string? FileName, string? SeriesId, DebridCompletedFileHandle? CompletedFileHandle);

/// <summary>A no-logging, no-redirect HTTP client used only by the loopback media proxy.</summary>
public sealed class NativeStreamProxyHttpClient : IDisposable
{
    public NativeStreamProxyHttpClient()
    {
        Client = new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
            },
            disposeHandler: true
        )
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}
