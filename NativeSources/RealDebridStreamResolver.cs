using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.NativeSources;

/// <summary>
/// Dedicated Real-Debrid client without the default logging handlers. Unrestricted download
/// links are personal, signed URLs, so keeping every response out of application logs is part
/// of the credential boundary.
/// </summary>
public sealed class RealDebridHttpClient : IDisposable
{
    public RealDebridHttpClient()
        : this(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            }
        )
    { }

    public RealDebridHttpClient(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("https://api.real-debrid.com/rest/1.0/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Real-Debrid implementation of the provider-neutral debrid contract.
///
/// Real-Debrid no longer exposes a global instant-availability endpoint (the documented
/// <c>/torrents/instantAvailability</c> route answers with error 37, "disabled endpoint"), so
/// "cached" here means "already downloaded into this account". Availability is therefore
/// account-scoped and read-only: this provider never adds magnets, selects files or deletes
/// torrents, and it does not start an uncached acquisition because a user pressed Play.
/// </summary>
public sealed partial class RealDebridStreamResolver(
    RealDebridHttpClient http,
    IDebridProviderSettingsProvider settingsProvider,
    INetworkTargetValidator targetValidator,
    ILogger<RealDebridStreamResolver> logger
) : IDebridProvider
{
    public const string ProviderId = "realdebrid";
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int TorrentPageSize = 1000;
    private const int MaximumTorrentPages = 10;
    /// <summary>How many hoster links are unrestricted while looking for the chosen file when Real-Debrid generated fewer links than selected files.</summary>
    private const int MaximumLinkProbes = 40;
    private static readonly TimeSpan TorrentListCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TorrentInfoCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly SemaphoreSlim _listGate = new(1, 1);
    private readonly Dictionary<string, (RealDebridTorrentInfo Info, DateTimeOffset ExpiresUtc)> _infoCache = new(StringComparer.Ordinal);
    private (IReadOnlyList<RealDebridTorrentSummary> Torrents, DateTimeOffset ExpiresUtc)? _listCache;

    public string Id => ProviderId;

    public string Name => "Real-Debrid";

    public bool Enabled => Settings.Enabled;

    public bool Configured => Settings.Configured;

    private DebridProviderSettings Settings => settingsProvider.GetSettings(Id);

    /// <summary>
    /// No MagnetSubmission: adding a magnet to Real-Debrid starts a download when the content is
    /// not cached, which the cached-only playback rule forbids without an explicit product decision.
    /// </summary>
    public DebridProviderCapabilities Capabilities =>
        DebridProviderCapabilities.CachedAvailability
        | DebridProviderCapabilities.AccountScopedAvailability
        | DebridProviderCapabilities.FileSelection
        | DebridProviderCapabilities.DirectStreamUrl
        | DebridProviderCapabilities.CompletedFileSource;

    public async Task<DebridCacheCheckResult> CheckCachedAsync(
        IReadOnlyCollection<string> infoHashes,
        CancellationToken cancellationToken
    )
    {
        var hashes = infoHashes
            .Select(CardigannResultNormalizer.NormalizeInfoHash)
            .Where(hash => hash is not null && InfoHashPattern().IsMatch(hash))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hashes.Length == 0)
            return new DebridCacheCheckResult(new Dictionary<string, DebridAvailability>(StringComparer.OrdinalIgnoreCase));

        var settings = Settings;
        if (!settings.Configured)
            return FailureResult("configuration", "not_configured", "Real-Debrid is not configured.");

        try
        {
            var torrents = await GetDownloadedTorrentsAsync(settings.Credential, cancellationToken).ConfigureAwait(false);
            var byHash = torrents
                .Where(torrent => !string.IsNullOrWhiteSpace(torrent.Hash))
                .GroupBy(torrent => torrent.Hash!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var availability = new Dictionary<string, DebridAvailability>(StringComparer.OrdinalIgnoreCase);
            foreach (var hash in hashes)
            {
                if (!byHash.TryGetValue(hash, out var torrent))
                {
                    availability[hash] = new DebridAvailability(Id, false, []);
                    continue;
                }

                var info = await GetTorrentInfoAsync(torrent.Id, settings.Credential, cancellationToken).ConfigureAwait(false);
                var files = info is null ? [] : SelectedFiles(info).Select(ToDebridFile).ToList();
                availability[hash] = new DebridAvailability(Id, files.Count > 0, files) { RemoteItemId = torrent.Id };
            }

            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "debrid-cache-check",
                fields: new Dictionary<string, object?>
                {
                    ["provider"] = Id,
                    ["hashCount"] = hashes.Length,
                    ["cachedCount"] = availability.Values.Count(value => value.Cached),
                    ["accountTorrents"] = torrents.Count,
                });
            return new DebridCacheCheckResult(availability);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            LogFailure("cache check", ex);
            return FailureResult("cache", FailureReason(ex), "Real-Debrid cache check failed.");
        }
    }

    public Task<DebridPlaybackResult> ResolvePlaybackAsync(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    ) => ResolvePlaybackAsync(candidate, query, null, cancellationToken);

    public async Task<DebridPlaybackResult> ResolvePlaybackAsync(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query,
        DebridFileIdentity? pinnedFile,
        CancellationToken cancellationToken
    )
    {
        var settings = Settings;
        var hash = CardigannResultNormalizer.NormalizeInfoHash(candidate.InfoHash);
        if (!settings.Configured)
            return PlaybackFailure("configuration", "not_configured", "Real-Debrid is not configured.");
        if (hash is null || !InfoHashPattern().IsMatch(hash))
            return PlaybackFailure("selection", "invalid_hash", "The torrent has no valid info hash.");

        var known = candidate.Availability?.FirstOrDefault(value =>
            string.Equals(value.Provider, Id, StringComparison.OrdinalIgnoreCase) && value.Cached);
        try
        {
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "release-selected",
                fields: new Dictionary<string, object?>
                {
                    ["provider"] = Id,
                    ["source"] = candidate.SourceId,
                    ["size"] = candidate.SizeBytes,
                });

            // Look the torrent up in the account again rather than trusting a stale search result;
            // this is read-only and never adds anything to the account.
            var torrentId = known?.RemoteItemId;
            if (torrentId is null)
            {
                var torrents = await GetDownloadedTorrentsAsync(settings.Credential, cancellationToken).ConfigureAwait(false);
                torrentId = torrents.FirstOrDefault(torrent => string.Equals(torrent.Hash, hash, StringComparison.OrdinalIgnoreCase))?.Id;
            }

            if (torrentId is null)
                return PlaybackFailure("selection", "not_cached", "The selected torrent is not in the Real-Debrid account.");

            var info = await GetTorrentInfoAsync(torrentId, settings.Credential, cancellationToken).ConfigureAwait(false);
            if (info is null || !IsDownloaded(info.Status))
                return PlaybackFailure("selection", "not_cached", "The Real-Debrid torrent is not fully downloaded.");

            var selected = SelectedFiles(info);
            var files = selected.Select(ToDebridFile).ToList();
            var selection = pinnedFile is null
                ? DebridMediaFileSelector.SelectWithDiagnostics(files, query)
                : DebridMediaFileSelector.SelectPinned(files, hash, pinnedFile);
            if (selection.File is null)
            {
                logger.LogWarning(
                    "Rejected Real-Debrid candidate {CandidateTitle} from {SourceId}: {Reason} — {Message}",
                    candidate.Title,
                    candidate.SourceId,
                    selection.Reason,
                    selection.Message);
                return PlaybackFailure("files", selection.Reason, selection.Message);
            }

            var resolved = await UnrestrictFileAsync(info, selected, selection.File, settings.Credential, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null)
                return new DebridPlaybackResult(null, resolved.Failure);

            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "provider-source-resolved",
                fields: new Dictionary<string, object?>
                {
                    ["provider"] = Id,
                    ["source"] = candidate.SourceId,
                    ["fileSize"] = resolved.Source!.ExpectedLength,
                });
            var handle = new DebridCompletedFileHandle(
                Id,
                info.Id,
                selection.File.Id.ToString(),
                Path.GetFileName(selection.File.Name),
                resolved.Source.ExpectedLength,
                hash,
                selection.File.Name);
            return new DebridPlaybackResult(
                new NativeResolvedStream(
                    $"{Id}:{candidate.SourceId}",
                    candidate.Title,
                    resolved.Source.Url,
                    resolved.Source.ExpectedLength,
                    Path.GetFileName(selection.File.Name),
                    handle));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            LogFailure("playback resolution", ex);
            return PlaybackFailure("provider", FailureReason(ex), "Real-Debrid playback resolution failed.");
        }
    }

    public async Task<DebridCompletedFileSource?> ResolveCompletedFileSourceAsync(
        DebridCompletedFileHandle handle,
        CancellationToken cancellationToken
    )
    {
        if (!string.Equals(handle.ProviderId, Id, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(handle.RemoteItemId)
            || !long.TryParse(handle.FileId, out var fileId)) return null;
        var settings = Settings;
        if (!settings.Configured) return null;
        var info = await GetTorrentInfoAsync(handle.RemoteItemId, settings.Credential, cancellationToken).ConfigureAwait(false);
        if (info is null || !IsDownloaded(info.Status)) return null;
        var selected = SelectedFiles(info);
        var file = selected.FirstOrDefault(candidate => candidate.Id == fileId);
        if (file is null) return null;
        var resolved = await UnrestrictFileAsync(info, selected, ToDebridFile(file), settings.Credential, cancellationToken).ConfigureAwait(false);
        if (resolved.Source is null) return null;
        if (handle.ExpectedLength is not null && resolved.Source.ExpectedLength is not null
            && handle.ExpectedLength != resolved.Source.ExpectedLength) return null;
        return resolved.Source;
    }

    public async Task<NativeSourceFailure?> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (!settings.Configured)
            return new NativeSourceFailure(Id, "Real-Debrid is not configured.", Name, "configuration", "not_configured");
        try
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, "user", settings.Credential);
            var user = await SendAsync<RealDebridUser>(request, cancellationToken).ConfigureAwait(false);
            if (user is null)
                return new NativeSourceFailure(Id, "Real-Debrid returned no account data.", Name, "account", "api_error");
            if (!string.Equals(user.Type, "premium", StringComparison.OrdinalIgnoreCase))
                return new NativeSourceFailure(Id, "The Real-Debrid account is not premium.", Name, "account", "premium_required");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            LogFailure("connection test", ex);
            return new NativeSourceFailure(Id, "Real-Debrid account check failed.", Name, "account", FailureReason(ex));
        }
    }

    private async Task<(DebridCompletedFileSource? Source, NativeSourceFailure? Failure)> UnrestrictFileAsync(
        RealDebridTorrentInfo info,
        IReadOnlyList<RealDebridTorrentFile> selected,
        DebridFile file,
        string token,
        CancellationToken cancellationToken)
    {
        // Real-Debrid returns one hoster link per selected file, in selection order — except that it
        // silently generates no link for files it does not consider media (a YIFY release's cover
        // .jpg, .nfo, .txt), even when they are marked selected. So the positional mapping is only a
        // first guess; the unrestricted link's own filename and length decide, and when the counts
        // disagree every link is checked until one proves it is this file.
        if (info.Links.Count == 0)
            return (null, new NativeSourceFailure(Id, "Real-Debrid has generated no links for this torrent.", Name, "files", "links_mismatch"));

        var index = -1;
        for (var i = 0; i < selected.Count; i++)
        {
            if (selected[i].Id == file.Id) { index = i; break; }
        }

        var order = new List<int>();
        if (index >= 0 && index < info.Links.Count && info.Links.Count == selected.Count)
            order.Add(index);
        else
            order.AddRange(Enumerable.Range(0, Math.Min(info.Links.Count, MaximumLinkProbes)));

        var expected = file.SizeBytes;
        var expectedName = Path.GetFileName(file.Name);
        var sawLengthMismatch = false;
        var sawArchive = false;
        foreach (var linkIndex in order)
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Post, "unrestrict/link", token);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["link"] = info.Links[linkIndex] });
            var unrestricted = await SendAsync<RealDebridUnrestrictedLink>(request, cancellationToken).ConfigureAwait(false);
            if (unrestricted is null || !Uri.TryCreate(unrestricted.Download, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
                return (null, new NativeSourceFailure(Id, "Real-Debrid did not return a safe HTTPS download URL.", Name, "url", "invalid_url"));

            if (!DescribesFile(unrestricted, expectedName, expected))
            {
                sawLengthMismatch = true;
                sawArchive |= IsArchive(unrestricted);
                continue;
            }

            await targetValidator.ValidateAsync(url, cancellationToken).ConfigureAwait(false);
            return (new DebridCompletedFileSource(url, expected ?? (unrestricted.Filesize > 0 ? unrestricted.Filesize : null), SupportsRanges: true), null);
        }

        // When several files of a torrent are selected, Real-Debrid packs them into one archive
        // link instead of a link per file. Nothing in that archive is streamable; only re-adding
        // the torrent with the media file alone selected gives a direct link. The torrent, not the
        // account, is what is unusable, so the reason is one the health tracker ignores.
        if (sawArchive)
            return (null, new NativeSourceFailure(Id, "Real-Debrid only offers this torrent as an archive; re-add it in Real-Debrid with only the media file selected.", Name, "files", "archive_link"));
        return sawLengthMismatch && order.Count == 1
            ? (null, new NativeSourceFailure(Id, "Real-Debrid reported a different file length than the torrent.", Name, "url", "length_mismatch"))
            : (null, new NativeSourceFailure(Id, "Real-Debrid links do not line up with the selected files.", Name, "files", "links_mismatch"));
    }

    private static readonly string[] ArchiveExtensions = [".rar", ".zip", ".7z", ".tar", ".gz"];

    /// <summary>Real-Debrid marks its multi-file bundles with an archive MIME type or extension.</summary>
    internal static bool IsArchive(RealDebridUnrestrictedLink unrestricted)
    {
        var mime = unrestricted.MimeType ?? string.Empty;
        if (mime.Contains("rar", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("zip", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("7z", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("tar", StringComparison.OrdinalIgnoreCase))
            return true;
        var extension = Path.GetExtension(unrestricted.Filename ?? string.Empty);
        return ArchiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unrestricted link is accepted only when what Real-Debrid says about it agrees with the
    /// torrent file we chose: the length when both sides know it, and the file name when both sides
    /// report one. A link that states neither is trusted only as a positional match.
    /// </summary>
    internal static bool DescribesFile(RealDebridUnrestrictedLink unrestricted, string expectedName, long? expectedLength)
    {
        if (unrestricted.Filesize > 0 && expectedLength is not null && unrestricted.Filesize != expectedLength)
            return false;
        var name = Path.GetFileName(unrestricted.Filename ?? string.Empty);
        if (name.Length > 0 && expectedName.Length > 0 && !string.Equals(name, expectedName, StringComparison.Ordinal))
            return false;
        return true;
    }

    private async Task<IReadOnlyList<RealDebridTorrentSummary>> GetDownloadedTorrentsAsync(string token, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _listGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_listCache is { } cached && cached.ExpiresUtc > now)
                return cached.Torrents;

            var all = new List<RealDebridTorrentSummary>();
            for (var page = 1; page <= MaximumTorrentPages; page++)
            {
                using var request = CreateAuthorizedRequest(HttpMethod.Get, $"torrents?page={page}&limit={TorrentPageSize}", token);
                using var response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NoContent)
                    break;
                var element = await ReadEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
                var batch = element.ValueKind == JsonValueKind.Array
                    ? element.Deserialize<List<RealDebridTorrentSummary>>(JsonOptions) ?? []
                    : [];
                all.AddRange(batch);
                var total = response.Headers.TryGetValues("X-Total-Count", out var values)
                    && int.TryParse(values.FirstOrDefault(), out var parsed) ? parsed : (int?)null;
                if (batch.Count < TorrentPageSize || (total is not null && all.Count >= total))
                    break;
            }

            var downloaded = all.Where(torrent => IsDownloaded(torrent.Status)).ToList();
            _listCache = (downloaded, now + TorrentListCacheTtl);
            return downloaded;
        }
        finally
        {
            _listGate.Release();
        }
    }

    private async Task<RealDebridTorrentInfo?> GetTorrentInfoAsync(string torrentId, string token, CancellationToken cancellationToken)
    {
        if (!TorrentIdPattern().IsMatch(torrentId))
            return null;
        var now = DateTimeOffset.UtcNow;
        lock (_infoCache)
        {
            if (_infoCache.TryGetValue(torrentId, out var cached) && cached.ExpiresUtc > now)
                return cached.Info;
        }

        using var request = CreateAuthorizedRequest(HttpMethod.Get, $"torrents/info/{Uri.EscapeDataString(torrentId)}", token);
        var info = await SendAsync<RealDebridTorrentInfo>(request, cancellationToken).ConfigureAwait(false);
        if (info is null)
            return null;
        lock (_infoCache)
        {
            if (_infoCache.Count > 512)
            {
                foreach (var key in _infoCache.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToList())
                    _infoCache.Remove(key);
            }

            // Only settled torrents are cached; an in-progress one must be re-read.
            if (IsDownloaded(info.Status))
                _infoCache[torrentId] = (info, now + TorrentInfoCacheTtl);
        }

        return info;
    }

    internal void InvalidateCaches()
    {
        _listCache = null;
        lock (_infoCache) _infoCache.Clear();
    }

    private static bool IsDownloaded(string? status) =>
        string.Equals(status, "downloaded", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RealDebridTorrentFile> SelectedFiles(RealDebridTorrentInfo info) =>
        info.Files.Where(file => file.Selected == 1).ToList();

    private static DebridFile ToDebridFile(RealDebridTorrentFile file) =>
        new(file.Id, DebridFileIdentity.NormalizePath(file.Path), file.Bytes > 0 ? file.Bytes : null);

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return default;
        var element = await ReadEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        return element.ValueKind == JsonValueKind.Undefined ? default : element.Deserialize<T>(JsonOptions);
    }

    private async Task<JsonElement> ReadEnvelopeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException("The Real-Debrid API response exceeded 8 MiB.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (buffer.Length + count > MaximumResponseBytes)
                throw new InvalidOperationException("The Real-Debrid API response exceeded 8 MiB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        JsonElement element = default;
        if (buffer.Length > 0)
        {
            using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
            element = document.RootElement.Clone();
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorCode = element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("error_code", out var codeElement)
                && codeElement.TryGetInt32(out var code) ? code : (int?)null;
            logger.LogWarning(
                "Real-Debrid API request failed with HTTP {StatusCode} and error code {ErrorCode}",
                (int)response.StatusCode,
                errorCode);
            throw new RealDebridApiException((int)response.StatusCode, errorCode);
        }

        return element;
    }

    private DebridCacheCheckResult FailureResult(string stage, string reason, string message) =>
        new(new Dictionary<string, DebridAvailability>(StringComparer.OrdinalIgnoreCase), new NativeSourceFailure(Id, message, Name, stage, reason));

    private DebridPlaybackResult PlaybackFailure(string stage, string reason, string message) =>
        new(null, new NativeSourceFailure(Id, message, Name, stage, reason));

    private static bool IsExpectedFailure(Exception exception) =>
        exception is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException or RealDebridApiException;

    /// <summary>Maps Real-Debrid HTTP status and error codes onto the shared failure vocabulary.</summary>
    internal static string FailureReason(Exception exception) =>
        exception switch
        {
            TaskCanceledException => "timeout",
            RealDebridApiException { ErrorCode: 8 } or RealDebridApiException { StatusCode: 401 } => "authentication_rejected",
            RealDebridApiException { ErrorCode: 14 } => "account_locked",
            RealDebridApiException { ErrorCode: 20 or 36 } => "premium_required",
            RealDebridApiException { ErrorCode: 5 or 34 } or RealDebridApiException { StatusCode: 429 } => "rate_limited",
            RealDebridApiException { ErrorCode: 25 } or RealDebridApiException { StatusCode: >= 500 } => "provider_unavailable",
            RealDebridApiException { ErrorCode: 37 } => "endpoint_disabled",
            RealDebridApiException { StatusCode: 403 } => "permission_denied",
            // Real-Debrid refuses to unrestrict specific files it has been told to block; the
            // account is fine and the next release usually plays.
            RealDebridApiException { StatusCode: 451 } => "blocked_content",
            RealDebridApiException => "api_error",
            _ => exception.GetType().Name.ToLowerInvariant(),
        };

    private void LogFailure(string operation, Exception exception)
    {
        // Never attach the exception: unrestrict responses can contain personal signed URLs.
        logger.LogWarning("Real-Debrid {Operation} failed ({FailureType})", operation, exception.GetType().Name);
    }

    internal sealed class RealDebridApiException(int statusCode, int? errorCode)
        : Exception("Real-Debrid API request failed.")
    {
        public int StatusCode { get; } = statusCode;

        public int? ErrorCode { get; } = errorCode;
    }

    private sealed class RealDebridUser
    {
        public string? Type { get; init; }

        public long Premium { get; init; }

        public string? Expiration { get; init; }
    }

    private sealed class RealDebridTorrentSummary
    {
        public string Id { get; init; } = string.Empty;

        public string? Hash { get; init; }

        public string? Status { get; init; }

        public long Bytes { get; init; }
    }

    private sealed class RealDebridTorrentInfo
    {
        public string Id { get; init; } = string.Empty;

        public string? Hash { get; init; }

        public string? Status { get; init; }

        public List<RealDebridTorrentFile> Files { get; init; } = [];

        public List<string> Links { get; init; } = [];
    }

    private sealed class RealDebridTorrentFile
    {
        public long Id { get; init; }

        public string? Path { get; init; }

        public long Bytes { get; init; }

        public int Selected { get; init; }
    }

    internal sealed class RealDebridUnrestrictedLink
    {
        public string? Download { get; init; }

        public long Filesize { get; init; }

        public string? Filename { get; init; }

        public string? MimeType { get; init; }

        public int Streamable { get; init; }
    }

    [GeneratedRegex("^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex InfoHashPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TorrentIdPattern();
}
