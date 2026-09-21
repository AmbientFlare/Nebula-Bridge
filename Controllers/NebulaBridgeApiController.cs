#pragma warning disable SA1611, SA1591, SA1615, CS0165

using System.ComponentModel.DataAnnotations;
using System.Net;
using NebulaBridge.NativeSources;
using NebulaBridge.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NebulaBridge.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge")]
public sealed class NebulaBridgeApiController : ControllerBase
{
    private readonly ILogger<NebulaBridgeApiController> _log;
    private readonly NebulaBridgeManager _nebulabridgeManager;
    private readonly NativeStreamProxyRegistry _nativeStreamProxyRegistry;
    private readonly NativeStreamProxyHttpClient _nativeStreamProxyHttpClient;
    private readonly NebulaBridgeMetadataService _metadata;
    private readonly HierarchyHydrationService _hierarchy;
    private readonly AcquisitionCoordinator _acquisitions;
    private readonly ProgressiveRangeCache _rangeCache;
    private readonly AcquisitionJobStore _jobStore;
    private readonly CacheStorageSafety _storageSafety;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public NebulaBridgeApiController(
        ILogger<NebulaBridgeApiController> log,
        NebulaBridgeManager nebulabridgeManager,
        NativeStreamProxyRegistry nativeStreamProxyRegistry,
        NativeStreamProxyHttpClient nativeStreamProxyHttpClient,
        NebulaBridgeMetadataService metadata,
        HierarchyHydrationService hierarchy,
        AcquisitionCoordinator acquisitions,
        ProgressiveRangeCache rangeCache,
        AcquisitionJobStore jobStore,
        CacheStorageSafety storageSafety,
        ILibraryManager libraryManager,
        IUserManager userManager
    )
    {
        _log = log;
        _nebulabridgeManager = nebulabridgeManager;
        _nativeStreamProxyRegistry = nativeStreamProxyRegistry;
        _nativeStreamProxyHttpClient = nativeStreamProxyHttpClient;
        _metadata = metadata;
        _hierarchy = hierarchy;
        _acquisitions = acquisitions;
        _rangeCache = rangeCache;
        _jobStore = jobStore;
        _storageSafety = storageSafety;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    [AcceptVerbs("GET", "HEAD")]
    [Route("native-stream/{key}")]
    public async Task<IActionResult> NativeStream([FromRoute, Required] string key)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (
            remoteIp is null
            || !(
                IPAddress.IsLoopback(remoteIp)
                || remoteIp.Equals(HttpContext.Connection.LocalIpAddress)
            )
        )
        {
            return Forbid();
        }

        await _acquisitions.RestoreReplaySourceAsync(
            _nativeStreamProxyRegistry,
            key,
            HttpContext.RequestAborted).ConfigureAwait(false);
        await using var sourceUse = await _acquisitions.BeginSourceUseAsync(
            _nativeStreamProxyRegistry, key, HttpContext.RequestAborted).ConfigureAwait(false);
        if (HttpMethods.IsHead(Request.Method))
            _nativeStreamProxyRegistry.MarkProbe(key);
        var playback = await _nativeStreamProxyRegistry
            .ResolveTargetAsync(key, false, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        var isUnrangedPlayback = HttpMethods.IsGet(Request.Method)
            && string.IsNullOrWhiteSpace(Request.Headers.Range)
            && _nativeStreamProxyRegistry.ShouldBeginUnrangedPlayback(key);
        var shouldBeginPlayback = ShouldBeginPlayback(Request.Method, Request.Headers.Range)
            || isUnrangedPlayback;
        await using var playbackSession = shouldBeginPlayback
            ? await _acquisitions.BeginPlaybackAsync(_nativeStreamProxyRegistry, key, HttpContext.RequestAborted).ConfigureAwait(false)
            : null;
        var job = playbackSession?.Job;
        if (playback.Stream is null)
        {
            _log.LogWarning(
                "Native stream selection failed at {Stage} ({Reason})",
                playback.Failure?.Stage ?? "selection",
                playback.Failure?.Reason ?? "failed"
            );
            return playback.Failure?.Reason == "not_found"
                ? NotFound()
                : StatusCode(StatusCodes.Status502BadGateway);
        }

        try
        {
            if (job is not null && TryGetSingleRange(Request.Headers.Range, out var cachedStart, out var cachedLength))
            {
                var cached = await _rangeCache.TryReadAsync(job, cachedStart, cachedLength, _jobStore, HttpContext.RequestAborted).ConfigureAwait(false);
                if (cached is not null)
                {
                    NebulaBridgeFileLog.Write(
                        NebulaLogVerbosity.Debug,
                        "cache-hit",
                        job.Id,
                        new Dictionary<string, object?>
                        {
                            ["start"] = cachedStart,
                            ["length"] = cachedLength,
                        });
                    Response.StatusCode = StatusCodes.Status206PartialContent;
                    Response.ContentLength = cached.Length;
                    Response.Headers.AcceptRanges = "bytes";
                    if (job.ExpectedBytes is { } total)
                        Response.Headers.ContentRange = $"bytes {cachedStart}-{cachedStart + cached.Length - 1}/{total}";
                    await Response.Body.WriteAsync(cached, HttpContext.RequestAborted).ConfigureAwait(false);
                    return new EmptyResult();
                }
            }
            var target = playback.Stream.Url;
            if (job is not null)
                NebulaBridgeFileLog.Write(
                    NebulaLogVerbosity.Debug,
                    "cache-miss",
                    job.Id,
                    new Dictionary<string, object?>
                    {
                        ["range"] = Request.Headers.Range.ToString(),
                        ["cachedBytes"] = AcquisitionCoordinator.GetCachedBytes(job),
                    });
            var openTimeout = StreamOpenTimeout;
            var refreshedLink = false;
            var failedOver = false;
            HttpResponseMessage upstream;
            while (true)
            {
                using var upstreamRequest = CreateNativeUpstreamRequest(target);
                var open = await NativeStreamOpen
                    .SendAsync(_nativeStreamProxyHttpClient.Client, upstreamRequest, openTimeout, HttpContext.RequestAborted)
                    .ConfigureAwait(false);
                if (open.Response is { } answered && !refreshedLink && NativeStreamOpen.IsStaleLink(answered.StatusCode))
                {
                    refreshedLink = true;
                    answered.Dispose();
                    var refreshed = await _nativeStreamProxyRegistry
                        .ResolveTargetAsync(key, true, HttpContext.RequestAborted)
                        .ConfigureAwait(false);
                    if (refreshed.Stream is null)
                    {
                        return StatusCode(StatusCodes.Status502BadGateway);
                    }

                    target = refreshed.Stream.Url;
                    continue;
                }

                var failure = open.Classify();
                if (failure is null && open.Response is { } servable)
                {
                    upstream = servable;
                    break;
                }

                failure ??= NativeStreamOpen.Faulted;
                var status = open.Response is null ? 0 : (int)open.Response.StatusCode;
                open.Response?.Dispose();
                NebulaBridgeFileLog.Write(
                    NebulaLogVerbosity.Information,
                    "stream-open-failed",
                    job?.Id,
                    new Dictionary<string, object?>
                    {
                        ["reason"] = failure,
                        ["statusCode"] = status == 0 ? null : status,
                        ["elapsedMs"] = (long)open.Elapsed.TotalMilliseconds,
                        ["timeoutMs"] = (long)openTimeout.TotalMilliseconds,
                        ["method"] = Request.Method,
                        ["range"] = Request.Headers.Range.ToString(),
                        ["failedOver"] = !failedOver,
                    });
                if (!failedOver)
                {
                    failedOver = true;
                    var alternate = await _nativeStreamProxyRegistry
                        .FailOverAsync(key, failure, HttpContext.RequestAborted)
                        .ConfigureAwait(false);
                    if (alternate.Stream is not null)
                    {
                        target = alternate.Stream.Url;
                        continue;
                    }
                }

                _log.LogWarning(
                    "Native stream proxy upstream failed ({Reason}, HTTP {StatusCode}, {ElapsedMs} ms)",
                    failure,
                    status,
                    (long)open.Elapsed.TotalMilliseconds
                );
                return StatusCode(
                    failure == NativeStreamOpen.Stalled
                        ? StatusCodes.Status504GatewayTimeout
                        : StatusCodes.Status502BadGateway);
            }

            using (upstream)
            {
                long requestedStart = 0;
                long requestedLength = 0;
                var hasRequestedRange = !HttpMethods.IsHead(Request.Method)
                    && TryGetSingleRange(Request.Headers.Range, out requestedStart, out requestedLength);
                var cachePlan = job is null || HttpMethods.IsHead(Request.Method) ? null : CacheHttpResponseValidator.Validate(
                    upstream.StatusCode,
                    upstream.Content.Headers.ContentRange,
                    upstream.Content.Headers.ContentLength,
                    upstream.Content.Headers.ContentType?.MediaType,
                    hasRequestedRange ? requestedStart : null,
                    hasRequestedRange ? requestedLength : null,
                    job.ExpectedBytes);
                if (ShouldRejectInvalidRange(job is not null, upstream.StatusCode, hasRequestedRange, cachePlan))
                {
                    _log.LogWarning(
                        "Native stream proxy rejected an invalid upstream range response "
                            + "({StatusCode}, {ContentType}, range={ContentRange}, length={ContentLength}, "
                            + "requested={RequestedStart}+{RequestedLength}, expected={ExpectedLength})",
                        (int)upstream.StatusCode,
                        upstream.Content.Headers.ContentType?.MediaType ?? "unknown",
                        upstream.Content.Headers.ContentRange?.ToString() ?? "missing",
                        upstream.Content.Headers.ContentLength,
                        requestedStart,
                        requestedLength,
                        job?.ExpectedBytes);
                    return StatusCode(StatusCodes.Status502BadGateway);
                }

                Response.StatusCode = (int)upstream.StatusCode;
                Response.ContentLength = upstream.Content.Headers.ContentLength;
                Response.ContentType =
                    upstream.Content.Headers.ContentType?.ToString()
                    ?? "application/octet-stream";
                if (upstream.Headers.AcceptRanges.Count > 0)
                {
                    Response.Headers.AcceptRanges = string.Join(",", upstream.Headers.AcceptRanges);
                }
                if (upstream.Content.Headers.ContentRange is not null)
                {
                    Response.Headers.ContentRange = upstream.Content.Headers.ContentRange.ToString();
                }

                if (!HttpMethods.IsHead(Request.Method))
                {
                    await using var body = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                    var offset = cachePlan?.Start ?? 0;
                    Func<long, ReadOnlyMemory<byte>, CancellationToken, Task>? cacheWrite =
                        cachePlan is not null && job is not null
                            ? async (chunkOffset, bytes, token) =>
                            {
                                if (chunkOffset < cachePlan.Start
                                    || checked(chunkOffset + bytes.Length) > cachePlan.Start + cachePlan.Length)
                                    throw new IOException("Upstream body exceeded its validated range.");
                                if (!_storageSafety.CanWrite(bytes.Length))
                                    throw new IOException("Playback cache free-space floor reached.");
                                await _rangeCache.StoreAsync(job, chunkOffset, bytes.ToArray(), _jobStore, token).ConfigureAwait(false);
                            }
                    : null;
                    await BoundedMediaTee.CopyAsync(body, Response.Body, offset, cacheWrite, HttpContext.RequestAborted,
                        ex => _log.LogWarning("Playback cache disabled for this request ({FailureType})", ex.GetType().Name)).ConfigureAwait(false);
                }

                return new EmptyResult();
            }
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Do not attach the exception because HttpClient exceptions can contain the signed
            // provider URL. The exception type is sufficient for operational diagnostics.
            _log.LogWarning(
                "Native stream proxy request failed ({FailureType})",
                ex.GetType().Name
            );
            return StatusCode(StatusCodes.Status502BadGateway);
        }
        finally
        {
            if (job is not null && isUnrangedPlayback)
            {
                NebulaBridgeFileLog.Write(
                    NebulaLogVerbosity.Information,
                    "playback-stream-ended",
                    job.Id,
                    new Dictionary<string, object?>
                    {
                        ["clientDisconnected"] = HttpContext.RequestAborted.IsCancellationRequested,
                    });
            }
        }
    }

    private static TimeSpan StreamOpenTimeout =>
        TimeSpan.FromSeconds(NebulaBridgePlugin.Instance?.Configuration.StreamOpenTimeoutSeconds ?? 20);

    private static bool TryGetSingleRange(string? value, out long start, out long length)
    {
        start = length = 0;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = value[6..].Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && long.TryParse(parts[0], out start) && long.TryParse(parts[1], out var end) && end >= start && (length = end - start + 1) > 0;
    }

    internal static bool ShouldRejectInvalidRange(
        bool hasAcquisitionJob,
        HttpStatusCode status,
        bool hasRequestedRange,
        CacheWritePlan? cachePlan) =>
        hasAcquisitionJob
        && status == HttpStatusCode.PartialContent
        && hasRequestedRange
        && cachePlan is null;

    internal static bool ShouldBeginPlayback(string method, string? range) =>
        !HttpMethods.IsHead(method) && TryGetSingleRange(range, out _, out _);

    private HttpRequestMessage CreateNativeUpstreamRequest(Uri target)
    {
        var upstreamRequest = new HttpRequestMessage(
            HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get,
            target
        );
        if (Request.Headers.TryGetValue("Range", out var range))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("Range", range.ToString());
        }
        if (Request.Headers.TryGetValue("If-Range", out var ifRange))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("If-Range", ifRange.ToString());
        }

        return upstreamRequest;
    }

    [HttpGet("meta/{stremioMetaType}/{Id}")]
    [Authorize]
    public async Task<ActionResult<StremioMeta>> NebulaBridgeMeta(
        [FromRoute, Required] StremioMediaType stremioMetaType,
        [FromRoute, Required] string id
    )
    {
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(Guid.Empty);
        var meta = await _metadata
            .GetMetaAsync(cfg, id, stremioMetaType, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        if (meta is null)
        {
            return NotFound();
        }
        return meta;
    }

    [HttpGet("capabilities")]
    [Authorize]
    public ActionResult<NebulaBridgeCapabilities> GetCapabilities()
    {
        var available = HttpContext.TryGetAuthenticatedUserId(out var userId)
            && NebulaBridgePlugin.Instance!.Configuration.UserConfigs
                .FirstOrDefault(config => config.UserId == userId)
                ?.NoNebulaBridge != true;
        return Ok(
            new NebulaBridgeCapabilities(
                1,
                new NebulaBridgeFeatures(
                    HierarchyPrefetch: true,
                    SeriesHydration: true,
                    SeasonHydration: true,
                    PlaybackPrefetch: false,
                    LocalAcquisition: _acquisitions.Enabled,
                    RetentionStatus: true
                ),
                SupportedVersions: [1],
                Availability: new NebulaBridgeAvailability(available, available)
            )
        );
    }

    [HttpGet("acquisition-status/{itemId:guid}")]
    [Authorize]
    public async Task<ActionResult<AcquisitionRetentionStatus>> GetAcquisitionStatus(
        [FromRoute] Guid itemId)
    {
        if (!HttpContext.TryGetAuthenticatedUserId(out var userId)
            || _userManager.GetUserById(userId) is not { } user
            || _libraryManager.GetItemById<BaseItem>(itemId, user) is not { } item)
            return NotFound();

        var jobs = await _acquisitions.ListAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var job = jobs.Where(candidate => candidate.ItemId == itemId)
            .OrderByDescending(candidate => candidate.LastAccessUtc ?? candidate.UpdatedUtc)
            .FirstOrDefault();
        var seriesId = job?.SeriesId;
        if (string.IsNullOrWhiteSpace(seriesId) && item is Episode episode)
            seriesId = NebulaBridgeManager.GetAcquisitionSeriesIdentity(
                episode,
                _libraryManager.GetItemById(episode.SeriesId) as Series);
        var seriesKeepPolicy = !string.IsNullOrWhiteSpace(seriesId)
            && jobs.Any(candidate => candidate.SeriesId == seriesId
                && candidate.Retention == AcquisitionRetention.KeepSeries);
        var enabled = _acquisitions.Enabled
            && NebulaBridgePlugin.Instance!.Configuration.UserConfigs
                .FirstOrDefault(config => config.UserId == userId)?.NoNebulaBridge != true;
        var temporary = job?.Retention == AcquisitionRetention.Temporary && !job.Imported;
        return Ok(new AcquisitionRetentionStatus(
            enabled,
            temporary,
            job?.Id,
            job?.State.ToString(),
            job?.Retention.ToString(),
            job is null ? 0 : AcquisitionCoordinator.GetCachedBytes(job),
            job?.ExpectedBytes,
            job?.ExpiresUtc,
            job?.Imported == true,
            enabled && temporary,
            enabled && temporary && item is Episode && !seriesKeepPolicy,
            seriesKeepPolicy));
    }

    [HttpPost("hydrate/series/{itemId:guid}")]
    [Authorize]
    public async Task<ActionResult<HierarchyHydrationResponse>> HydrateSeries(
        [FromRoute] Guid itemId
    )
    {
        if (!HttpContext.TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        _log.LogDebug("Hierarchy prefetch requested for Series {ItemId}", itemId);
        var result = await _hierarchy
            .HydrateSeriesAsync(itemId, userId, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(HierarchyHydrationResponse.From(result));
    }

    [HttpPost("hydrate/season/{itemId:guid}")]
    [Authorize]
    public async Task<ActionResult<HierarchyHydrationResponse>> HydrateSeason(
        [FromRoute] Guid itemId
    )
    {
        if (!HttpContext.TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        _log.LogDebug("Hierarchy prefetch requested for Season {ItemId}", itemId);
        var result = await _hierarchy
            .HydrateSeasonAsync(itemId, userId, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(HierarchyHydrationResponse.From(result));
    }

    // [HttpGet("catalogs")]
    // Moved to CatalogController

    [HttpGet("subtitles/{itemId:guid}")]
    public ActionResult<IEnumerable<StremioSubtitle>> GetSubtitles(
        [FromRoute, Required] Guid itemId
    )
    {
        var subs = _nebulabridgeManager.GetStremioSubtitlesCache(itemId);
        return Ok(subs ?? new List<StremioSubtitle>());
    }


}
