using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.NativeSources;

public sealed record TorBoxResolverSettings(bool Enabled, string ApiToken);

public interface ITorBoxSettingsProvider
{
    TorBoxResolverSettings GetSettings();
}

public sealed class PluginTorBoxSettingsProvider : ITorBoxSettingsProvider
{
    public TorBoxResolverSettings GetSettings()
    {
        var configuration = NebulaBridgePlugin.Instance?.Configuration;
        var environmentToken = Environment.GetEnvironmentVariable("NEBULA_BRIDGE_TORBOX_API_TOKEN");
        if (string.IsNullOrWhiteSpace(environmentToken))
        {
            environmentToken = Environment.GetEnvironmentVariable("GELATO_TORBOX_API_TOKEN");
        }

        return new TorBoxResolverSettings(
            configuration?.EnableTorBoxResolver == true,
            string.IsNullOrWhiteSpace(environmentToken)
                ? configuration?.TorBoxApiToken?.Trim() ?? string.Empty
                : environmentToken.Trim()
        );
    }
}

/// <summary>
/// Dedicated client without the default IHttpClientFactory logging handlers. TorBox requires the
/// token in the request-download query string, so keeping those URLs out of application logs is
/// part of the credential boundary.
/// </summary>
public sealed class TorBoxHttpClient : IDisposable
{
    public TorBoxHttpClient()
        : this(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            }
        )
    { }

    public TorBoxHttpClient(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("https://api.torbox.app/v1/api/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// TorBox implementation of the provider-neutral cache and playback interface. Cache checks are
/// read-only and batched; account mutation happens only from ResolvePlaybackAsync after selection.
/// </summary>
public sealed partial class TorBoxStreamResolver(
    TorBoxHttpClient http,
    ITorBoxSettingsProvider settingsProvider,
    INetworkTargetValidator targetValidator,
    ILogger<TorBoxStreamResolver> logger
) : IDebridProvider
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public string Id => "torbox";

    public string Name => "TorBox";

    public bool Enabled => settingsProvider.GetSettings().Enabled;

    public bool Configured
    {
        get
        {
            var settings = settingsProvider.GetSettings();
            return settings.Enabled && !string.IsNullOrWhiteSpace(settings.ApiToken);
        }
    }

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
        {
            return new DebridCacheCheckResult(
                new Dictionary<string, DebridAvailability>(StringComparer.OrdinalIgnoreCase)
            );
        }

        var settings = settingsProvider.GetSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            return FailureResult("configuration", "not_configured", "TorBox is not configured.");
        }

        try
        {
            var data = new List<TorBoxCachedTorrent>();
            foreach (var batch in hashes.Chunk(100))
            {
                using var request = CreateAuthorizedRequest(
                    HttpMethod.Post,
                    "torrents/checkcached?format=list&list_files=true",
                    settings.ApiToken
                );
                request.Content = JsonContent.Create(new TorBoxCacheRequest(batch));
                data.AddRange(
                    await SendAsync<List<TorBoxCachedTorrent>>(request, cancellationToken)
                        .ConfigureAwait(false) ?? []
                );
            }
            var returned = data
                .Where(item => !string.IsNullOrWhiteSpace(item.Hash))
                .GroupBy(item => item.Hash, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var availability = hashes.ToDictionary(
                hash => hash,
                hash =>
                {
                    if (!returned.TryGetValue(hash, out var cached))
                    {
                        return new DebridAvailability(Id, false, []);
                    }

                    return new DebridAvailability(
                        Id,
                        true,
                        cached.Files.Select(ToDebridFile).ToList()
                    );
                },
                StringComparer.OrdinalIgnoreCase
            );
            logger.LogInformation(
                "TorBox cache check completed: {HashCount} hashes, {CachedCount} cached",
                hashes.Length,
                availability.Values.Count(value => value.Cached)
            );
            return new DebridCacheCheckResult(availability);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            LogFailure("cache check", ex);
            return FailureResult("cache", FailureReason(ex), "TorBox cache check failed.");
        }
    }

    public async Task<DebridPlaybackResult> ResolvePlaybackAsync(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    )
    {
        var settings = settingsProvider.GetSettings();
        var hash = CardigannResultNormalizer.NormalizeInfoHash(candidate.InfoHash);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiToken))
        {
            return PlaybackFailure("configuration", "not_configured", "TorBox is not configured.");
        }

        if (hash is null || !InfoHashPattern().IsMatch(hash))
        {
            return PlaybackFailure("selection", "invalid_hash", "The torrent has no valid info hash.");
        }

        if (candidate.Availability?.Any(value => value.Provider == Id && value.Cached) != true)
        {
            return PlaybackFailure("selection", "not_cached", "The selected torrent is not cached.");
        }

        try
        {
            logger.LogInformation(
                "Resolving selected cached torrent from {SourceId} through TorBox",
                candidate.SourceId
            );
            var torrentId = await AddCachedTorrentAsync(
                    candidate with { InfoHash = hash },
                    settings.ApiToken,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (torrentId is null)
            {
                return PlaybackFailure(
                    "add",
                    "not_cached_or_add_failed",
                    "TorBox did not add the torrent with cached-only semantics."
                );
            }

            logger.LogInformation(
                "TorBox accepted cached-only add for selected source {SourceId}",
                candidate.SourceId
            );

            var torrent = await GetUserTorrentAsync(
                    torrentId.Value,
                    settings.ApiToken,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (torrent is null)
            {
                return PlaybackFailure("files", "torrent_missing", "The TorBox torrent could not be loaded.");
            }

            var selection = DebridMediaFileSelector.SelectWithDiagnostics(
                torrent.Files.Select(ToDebridFile).ToList(),
                query
            );
            var file = selection.File;
            if (file is null)
            {
                logger.LogWarning(
                    "Rejected TorBox candidate {CandidateTitle} from {SourceId}: {Reason} — {Message}",
                    candidate.Title,
                    candidate.SourceId,
                    selection.Reason,
                    selection.Message
                );
                return PlaybackFailure(
                    "files",
                    selection.Reason,
                    selection.Message
                );
            }

            var url = await RequestDownloadLinkAsync(
                    torrentId.Value,
                    file.Id,
                    settings.ApiToken,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (url is null || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
            {
                return PlaybackFailure("url", "invalid_url", "TorBox did not return a safe HTTPS playback URL.");
            }

            await targetValidator.ValidateAsync(url, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "TorBox playback URL generated for selected source {SourceId}",
                candidate.SourceId
            );
            return new DebridPlaybackResult(
                new NativeResolvedStream(
                    $"torbox:{candidate.SourceId}",
                    $"{candidate.Title} · TorBox",
                    url,
                    file.SizeBytes ?? candidate.SizeBytes,
                    Path.GetFileName(file.Name)
                )
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            LogFailure("playback resolution", ex);
            return PlaybackFailure("provider", FailureReason(ex), "TorBox playback resolution failed.");
        }
    }

    private async Task<long?> AddCachedTorrentAsync(
        NativeReleaseCandidate candidate,
        string apiToken,
        CancellationToken cancellationToken
    )
    {
        var magnet = candidate.MagnetUrl?.AbsoluteUri
            ?? (candidate.Link.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
                ? candidate.Link.AbsoluteUri
                : $"magnet:?xt=urn:btih:{candidate.InfoHash}");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(magnet), "magnet" },
            { new StringContent("true"), "add_only_if_cached" },
        };
        using var request = CreateAuthorizedRequest(HttpMethod.Post, "torrents/createtorrent", apiToken);
        request.Content = form;
        var data = await SendAsync<TorBoxCreateTorrentData>(request, cancellationToken)
            .ConfigureAwait(false);
        return data?.TorrentId;
    }

    private async Task<TorBoxUserTorrent?> GetUserTorrentAsync(
        long torrentId,
        string apiToken,
        CancellationToken cancellationToken
    )
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"torrents/mylist?id={torrentId}&bypass_cache=true",
            apiToken
        );
        var data = await SendJsonElementAsync(request, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            return null;
        }

        var element = data.Value;
        if (element.ValueKind == JsonValueKind.Array)
        {
            element = element.EnumerateArray().FirstOrDefault();
        }

        return element.ValueKind == JsonValueKind.Object
            ? element.Deserialize<TorBoxUserTorrent>(JsonOptions)
            : null;
    }

    private async Task<Uri?> RequestDownloadLinkAsync(
        long torrentId,
        long fileId,
        string apiToken,
        CancellationToken cancellationToken
    )
    {
        var path =
            $"torrents/requestdl?token={Uri.EscapeDataString(apiToken)}"
            + $"&torrent_id={torrentId}&file_id={fileId}&redirect=false&append_name=true";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, path, apiToken);
        var data = await SendAsync<string>(request, cancellationToken).ConfigureAwait(false);
        return Uri.TryCreate(data, UriKind.Absolute, out var url) ? url : null;
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string path,
        string apiToken
    )
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var element = await SendJsonElementAsync(request, cancellationToken).ConfigureAwait(false);
        return element is null ? default : element.Value.Deserialize<T>(JsonOptions);
    }

    private async Task<JsonElement?> SendJsonElementAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        using var response = await http.Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidOperationException("The TorBox API response exceeded 8 MiB.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (buffer.Length + count > MaximumResponseBytes)
            {
                throw new InvalidOperationException("The TorBox API response exceeded 8 MiB.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        var envelope = await JsonSerializer.DeserializeAsync<TorBoxEnvelope>(
                buffer,
                JsonOptions,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || envelope?.Success != true || envelope.Data is null)
        {
            logger.LogWarning(
                "TorBox API request failed with HTTP {StatusCode} and code {ErrorCode}",
                (int)response.StatusCode,
                SanitizeErrorCode(envelope?.Error)
            );
            throw new TorBoxApiException(
                (int)response.StatusCode,
                SanitizeErrorCode(envelope?.Error)
            );
        }

        return envelope.Data.Value.Clone();
    }

    private DebridCacheCheckResult FailureResult(string stage, string reason, string message) =>
        new(
            new Dictionary<string, DebridAvailability>(StringComparer.OrdinalIgnoreCase),
            new NativeSourceFailure(Id, message, Name, stage, reason)
        );

    private DebridPlaybackResult PlaybackFailure(string stage, string reason, string message) =>
        new(null, new NativeSourceFailure(Id, message, Name, stage, reason));

    private static DebridFile ToDebridFile(TorBoxFile file) =>
        new(file.Id, file.Name ?? string.Empty, file.Size > 0 ? file.Size : null, file.MimeType);

    private static bool IsExpectedFailure(Exception exception) =>
        exception is HttpRequestException
            or JsonException
            or InvalidOperationException
            or TaskCanceledException
            or TorBoxApiException;

    private static string FailureReason(Exception exception) =>
        exception switch
        {
            TaskCanceledException => "timeout",
            TorBoxApiException apiException when apiException.StatusCode is 401 or 403 => "authentication_rejected",
            TorBoxApiException => "api_error",
            _ => exception.GetType().Name.ToLowerInvariant(),
        };

    private void LogFailure(string operation, Exception exception)
    {
        // Never attach the exception: request-download errors can contain the token-bearing URI.
        logger.LogWarning(
            "TorBox {Operation} failed ({FailureType})",
            operation,
            exception.GetType().Name
        );
    }

    private static string SanitizeErrorCode(string? value)
    {
        var cleaned = new string(
            (value ?? "UNKNOWN")
                .Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                .Take(64)
                .ToArray()
        );
        return string.IsNullOrEmpty(cleaned) ? "UNKNOWN" : cleaned;
    }

    private sealed record TorBoxCacheRequest([property: JsonPropertyName("hashes")] string[] Hashes);

    private sealed class TorBoxApiException(int statusCode, string errorCode)
        : Exception("TorBox API request failed.")
    {
        public int StatusCode { get; } = statusCode;

        public string ErrorCode { get; } = errorCode;
    }

    private sealed class TorBoxEnvelope
    {
        public bool Success { get; init; }

        public string? Error { get; init; }

        public JsonElement? Data { get; init; }
    }

    private sealed class TorBoxCachedTorrent
    {
        public string Hash { get; init; } = string.Empty;

        public List<TorBoxFile> Files { get; init; } = [];
    }

    private sealed class TorBoxUserTorrent
    {
        public List<TorBoxFile> Files { get; init; } = [];
    }

    private sealed class TorBoxFile
    {
        public long Id { get; init; }

        public string? Name { get; init; }

        public long Size { get; init; }

        [JsonPropertyName("mimetype")]
        public string? MimeType { get; init; }
    }

    private sealed class TorBoxCreateTorrentData
    {
        [JsonPropertyName("torrent_id")]
        public long TorrentId { get; init; }
    }

    [GeneratedRegex("^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex InfoHashPattern();
}
