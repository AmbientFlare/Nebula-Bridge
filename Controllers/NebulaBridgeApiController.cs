#pragma warning disable SA1611, SA1591, SA1615, CS0165

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using NebulaBridge.NativeSources;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using NebulaBridge.Services;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge")]
[Route("gelato")]
public sealed class NebulaBridgeApiController : ControllerBase
{
    private readonly ILogger<NebulaBridgeApiController> _log;
    private readonly NebulaBridgeManager _nebulabridgeManager;
    private readonly NativeStreamProxyRegistry _nativeStreamProxyRegistry;
    private readonly NativeStreamProxyHttpClient _nativeStreamProxyHttpClient;
    private readonly NebulaBridgeMetadataService _metadata;
    private readonly string _downloadPath;

    public NebulaBridgeApiController(
        ILogger<NebulaBridgeApiController> log,
        IApplicationPaths appPaths,
        NebulaBridgeManager nebulabridgeManager,
        NativeStreamProxyRegistry nativeStreamProxyRegistry,
        NativeStreamProxyHttpClient nativeStreamProxyHttpClient,
        NebulaBridgeMetadataService metadata
    )
    {
        _log = log;
        _nebulabridgeManager = nebulabridgeManager;
        _nativeStreamProxyRegistry = nativeStreamProxyRegistry;
        _nativeStreamProxyHttpClient = nativeStreamProxyHttpClient;
        _metadata = metadata;
        _downloadPath = Path.Combine(appPaths.CachePath, "nebulabridge-torrents");
        Directory.CreateDirectory(_downloadPath);
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

        var playback = await _nativeStreamProxyRegistry
            .ResolveTargetAsync(key, false, HttpContext.RequestAborted)
            .ConfigureAwait(false);
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
            var target = playback.Stream.Url;
            HttpResponseMessage? upstream = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var upstreamRequest = CreateNativeUpstreamRequest(target);
                upstream = await _nativeStreamProxyHttpClient
                    .Client.SendAsync(
                        upstreamRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        HttpContext.RequestAborted
                    )
                    .ConfigureAwait(false);
                if (
                    attempt == 0
                    && upstream.StatusCode
                        is HttpStatusCode.Unauthorized
                            or HttpStatusCode.Forbidden
                            or HttpStatusCode.NotFound
                )
                {
                    upstream.Dispose();
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

                break;
            }

            using (upstream)
            {
                if (upstream is null)
                {
                    return StatusCode(StatusCodes.Status502BadGateway);
                }

                if (
                    upstream.StatusCode
                    is not (
                        HttpStatusCode.OK
                        or HttpStatusCode.PartialContent
                        or HttpStatusCode.RequestedRangeNotSatisfiable
                    )
                )
                {
                    _log.LogWarning(
                        "Native stream proxy upstream returned HTTP {StatusCode}",
                        (int)upstream.StatusCode
                    );
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
                    await upstream.Content
                        .CopyToAsync(Response.Body, HttpContext.RequestAborted)
                        .ConfigureAwait(false);
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
    }

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

    [HttpGet("stream")]
    public async Task<IActionResult> TorrentStream(
        [FromQuery] string ih,
        [FromQuery] int? idx,
        [FromQuery] string? filename,
        [FromQuery] string? trackers
    )
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (
            remoteIp == null
            || !(
                IPAddress.IsLoopback(remoteIp)
                || remoteIp.Equals(HttpContext.Connection.LocalIpAddress)
            )
        )
            return Forbid();

        if (string.IsNullOrWhiteSpace(ih))
            return BadRequest("Missing ?ih=<infohash or magnet>");

        var ct = HttpContext.RequestAborted;

        var plugin = NebulaBridgePlugin.Instance!;
        var settings = new EngineSettingsBuilder
        {
            MaximumConnections = 40,
            MaximumDownloadRate = plugin.Configuration.P2PDLSpeed,
            MaximumUploadRate = plugin.Configuration.P2PULSpeed,
        }.ToSettings();

        var engine = new ClientEngine(settings);

        var infoHashes =
            TryParseInfoHashes(ih)
            ?? throw new ArgumentException("Invalid infohash or magnet.", nameof(ih));
        var announce = ParseTrackers(trackers) ?? DefaultTrackers();
        var magnet = new MagnetLink(infoHashes, name: null, announceUrls: announce);

        var manager = await engine.AddStreamingAsync(magnet, _downloadPath);
        await manager.StartAsync();

        if (!manager.HasMetadata)
        {
            while (!manager.HasMetadata && !ct.IsCancellationRequested)
                await Task.Delay(100, ct);

            if (!manager.HasMetadata)
                return StatusCode(503, "Metadata not yet available.");
        }

        var selected =
            idx is { } i and >= 0 && i < manager.Files.Count
                ? manager.Files[i]
                : (
                    !string.IsNullOrWhiteSpace(filename)
                        ? manager.Files.FirstOrDefault(x =>
                            x.Path.EndsWith(filename, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(
                                Path.GetFileName(x.Path),
                                filename,
                                StringComparison.OrdinalIgnoreCase
                            )
                        ) ?? PickHeuristic(manager)
                        : PickHeuristic(manager)
                );

        var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = new Timer(
            _ =>
            {
                _log.LogDebug(
                    "file: {File}, progress: {Progress:0.00}%, dl: {DL}/s, ul: {UL}/s, peers: {Peers}, seeds: {Seeds}, leechers: {Leechs}, bytes: {Bytes}",
                    selected.Path,
                    manager.Progress,
                    manager.Monitor.DownloadRate,
                    manager.Monitor.UploadRate,
                    manager.Peers.Available,
                    manager.Peers.Seeds,
                    manager.Peers.Leechs,
                    manager.Monitor.DataBytesReceived
                );
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10)
        );

        _log.LogInformation($"starting torrent stream for {selected.Path}");
        var streamProvider = manager.StreamProvider
            ?? throw new InvalidOperationException("Torrent stream provider is unavailable.");
        var stream = await streamProvider.CreateStreamAsync(selected, ct);

        // Register cleanup for both normal completion and cancellation
        ct.Register(() =>
        {
            _log.LogInformation("Client disconnected. Cleaning up resources...");
            try
            {
                timerCts.Cancel();
            }
            catch
            {
                // ignored
            }

            try
            {
                timer.Dispose();
            }
            catch
            {
                // ignored
            }

            try
            {
                manager.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // ignored
            }

            try
            {
                engine.Dispose();
            }
            catch
            {
                // ignored
            }
        });

        Response.Headers.AcceptRanges = "bytes";
        return File(stream, GuessContentType(selected.Path), enableRangeProcessing: true);
    }

    private static ITorrentManagerFile PickHeuristic(TorrentManager manager)
    {
        return manager.Files.OrderByDescending(LikelyVideo).ThenByDescending(f => f.Length).First();

        static bool LikelyVideo(ITorrentManagerFile f)
        {
            var name = Path.GetFileName(f.Path);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (name.Contains("sample", StringComparison.OrdinalIgnoreCase))
                return false;
            if (
                ext
                is ".srt"
                    or ".ass"
                    or ".ssa"
                    or ".sub"
                    or ".idx"
                    or ".nfo"
                    or ".txt"
                    or ".jpg"
                    or ".jpeg"
                    or ".png"
                    or ".gif"
            )
                return false;
            return ext
                is ".mkv"
                    or ".mp4"
                    or ".m4v"
                    or ".avi"
                    or ".mov"
                    or ".wmv"
                    or ".ts"
                    or ".m2ts";
        }
    }

    private static InfoHashes? TryParseInfoHashes(string s)
    {
        s = s.Trim();

        if (Regex.IsMatch(s, "^[A-Fa-f0-9]{40}$"))
            return InfoHashes.FromInfoHash(InfoHash.FromHex(s));

        if (Regex.IsMatch(s, "^[A-Z2-7=]+$", RegexOptions.IgnoreCase))
            return InfoHashes.FromInfoHash(InfoHash.FromBase32(s));

        if (Regex.IsMatch(s, "^[A-Fa-f0-9]{64}$"))
            return InfoHashes.FromInfoHash(InfoHash.FromHex(s));

        if (MagnetLink.TryParse(s, out var m))
            return m.InfoHashes;

        return null;
    }

    private static string[]? ParseTrackers(string? trackers) =>
        string.IsNullOrWhiteSpace(trackers)
            ? null
            : Uri.UnescapeDataString(trackers)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] DefaultTrackers() =>
        [
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://explodie.org:6969/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
        ];

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".ts" or ".m2ts" => "video/mp2t",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };
    }
}
