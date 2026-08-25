using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NebulaBridge.Config;
using Microsoft.Extensions.Logging;
using Net.Codecrete.QrCodeGenerator;

namespace NebulaBridge.Services;

public sealed record TraktSettings(
    bool Enabled,
    string ClientId,
    string ClientSecret,
    string RedirectUri,
    string AccessToken,
    string RefreshToken,
    long TokenCreatedAt,
    int TokenExpiresIn,
    string ConnectedUser,
    string ConnectionSource = "nebula",
    string? LinkedJellyfinUserId = null
);

public interface ITraktSettingsProvider
{
    TraktSettings GetSettings();
    void SaveTokens(
        TraktTokenResponse token,
        string connectedUser,
        TraktSettings? sourceSettings = null
    );
    void Disconnect();
}

public sealed class PluginTraktSettingsProvider(JellyfinTraktAccountBridge jellyfinBridge)
    : ITraktSettingsProvider
{
    public TraktSettings GetSettings()
    {
        var configuration = NebulaBridgePlugin.Instance?.Configuration ?? new PluginConfiguration();
        var environmentClientId = Environment.GetEnvironmentVariable("NEBULA_BRIDGE_TRAKT_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(environmentClientId))
        {
            environmentClientId = Environment.GetEnvironmentVariable("GELATO_TRAKT_CLIENT_ID");
        }
        var environmentClientSecret = Environment.GetEnvironmentVariable(
            "NEBULA_BRIDGE_TRAKT_CLIENT_SECRET"
        );
        if (string.IsNullOrWhiteSpace(environmentClientSecret))
        {
            environmentClientSecret = Environment.GetEnvironmentVariable(
                "GELATO_TRAKT_CLIENT_SECRET"
            );
        }
        var environmentRedirectUri = Environment.GetEnvironmentVariable(
            "NEBULA_BRIDGE_TRAKT_REDIRECT_URI"
        );
        if (string.IsNullOrWhiteSpace(environmentRedirectUri))
        {
            environmentRedirectUri = Environment.GetEnvironmentVariable(
                "GELATO_TRAKT_REDIRECT_URI"
            );
        }
        var inherited = jellyfinBridge.GetSettings(configuration.EnableTraktCatalogs);
        if (inherited is not null)
        {
            return inherited;
        }

        return new TraktSettings(
            configuration.EnableTraktCatalogs,
            string.IsNullOrWhiteSpace(environmentClientId)
                ? configuration.TraktClientId.Trim()
                : environmentClientId.Trim(),
            string.IsNullOrWhiteSpace(environmentClientSecret)
                ? configuration.TraktClientSecret.Trim()
                : environmentClientSecret.Trim(),
            string.IsNullOrWhiteSpace(environmentRedirectUri)
                ? configuration.TraktRedirectUri.Trim()
                : environmentRedirectUri.Trim(),
            configuration.TraktAccessToken.Trim(),
            configuration.TraktRefreshToken.Trim(),
            configuration.TraktTokenCreatedAt,
            configuration.TraktTokenExpiresIn,
            configuration.TraktConnectedUser.Trim()
        );
    }

    public void SaveTokens(
        TraktTokenResponse token,
        string connectedUser,
        TraktSettings? sourceSettings = null
    )
    {
        if (
            string.Equals(sourceSettings?.ConnectionSource, "jellyfin", StringComparison.Ordinal)
            && jellyfinBridge.TrySaveTokens(sourceSettings!.LinkedJellyfinUserId, token)
        )
        {
            return;
        }

        var plugin = NebulaBridgePlugin.Instance ?? throw new InvalidOperationException(
            "Nebula Bridge is not initialized."
        );
        var configuration = plugin.Configuration;
        configuration.TraktAccessToken = token.AccessToken;
        configuration.TraktRefreshToken = token.RefreshToken;
        configuration.TraktTokenCreatedAt = token.CreatedAt;
        configuration.TraktTokenExpiresIn = token.ExpiresIn;
        configuration.TraktConnectedUser = connectedUser;
        plugin.SaveConfiguration();
    }

    public void Disconnect()
    {
        var plugin = NebulaBridgePlugin.Instance ?? throw new InvalidOperationException(
            "Nebula Bridge is not initialized."
        );
        var configuration = plugin.Configuration;
        configuration.TraktAccessToken = string.Empty;
        configuration.TraktRefreshToken = string.Empty;
        configuration.TraktTokenCreatedAt = 0;
        configuration.TraktTokenExpiresIn = 0;
        configuration.TraktConnectedUser = string.Empty;
        plugin.SaveConfiguration();
    }
}

/// <summary>A dedicated client keeps Trakt authorization headers out of factory logging.</summary>
public sealed class TraktHttpClient : IDisposable
{
    public TraktHttpClient()
        : this(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
            }
        )
    { }

    public TraktHttpClient(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("https://api.trakt.tv/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

public sealed record TraktCatalogDefinition(
    string Id,
    string Type,
    string Name,
    string Path,
    string? MediaProperty,
    bool RequiresAccount = false
);

public sealed record TraktDeviceAuthorizationStatus(
    string State,
    string? UserCode,
    string? VerificationUrl,
    DateTimeOffset? ExpiresAt,
    string? ConnectedUser,
    string? Message = null,
    string? ConnectionSource = null,
    string? ActivationUrl = null,
    string? QrCodeDataUri = null
);

public sealed record TraktTokenResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    long CreatedAt
);

public sealed record TraktWatchedEpisode(
    string? ShowImdbId,
    string? ShowTmdbId,
    string? ShowTvdbId,
    string? ShowTraktId,
    int Season,
    int Episode,
    int Plays,
    DateTimeOffset? LastWatchedAt
);

public sealed class NativeTraktClient(
    TraktHttpClient http,
    ITraktSettingsProvider settingsProvider,
    ILogger<NativeTraktClient> logger
)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly object _deviceLock = new();
    private TraktDeviceCode? _deviceCode;
    private DateTimeOffset _nextDevicePoll;

    private static readonly TraktCatalogDefinition[] PublicCatalogs =
    [
        new("trakt-movies-trending", "movie", "Trakt Trending Movies", "movies/trending", "movie"),
        new("trakt-movies-popular", "movie", "Trakt Popular Movies", "movies/popular", null),
        new("trakt-movies-anticipated", "movie", "Trakt Most Anticipated Movies", "movies/anticipated", "movie"),
        new("trakt-shows-trending", "series", "Trakt Trending TV Shows", "shows/trending", "show"),
        new("trakt-shows-popular", "series", "Trakt Popular TV Shows", "shows/popular", null),
        new("trakt-shows-anticipated", "series", "Trakt Most Anticipated TV Shows", "shows/anticipated", "show"),
    ];

    private static readonly TraktCatalogDefinition[] AccountCatalogs =
    [
        new("trakt-watchlist-movies", "movie", "My Trakt Movie Watchlist", "users/me/watchlist/movies", "movie", true),
        new("trakt-watchlist-shows", "series", "My Trakt TV Watchlist", "users/me/watchlist/shows", "show", true),
        new("trakt-collection-movies", "movie", "My Trakt Movie Collection", "sync/collection/movies", "movie", true),
        new("trakt-collection-shows", "series", "My Trakt TV Collection", "sync/collection/shows", "show", true),
        new("trakt-recommendations-movies", "movie", "My Trakt Movie Recommendations", "recommendations/movies", null, true),
        new("trakt-recommendations-shows", "series", "My Trakt TV Recommendations", "recommendations/shows", null, true),
        new("trakt-history-movies", "movie", "My Trakt Movie History", "users/me/history/movies", "movie", true),
        new("trakt-history-shows", "series", "My Trakt TV History", "users/me/history/shows", "show", true),
        new("trakt-ratings-movies", "movie", "My Trakt Rated Movies", "users/me/ratings/movies", "movie", true),
        new("trakt-ratings-shows", "series", "My Trakt Rated TV Shows", "users/me/ratings/shows", "show", true),
        new("trakt-progress-movies", "movie", "My Trakt Movies In Progress", "sync/playback/movies", "movie", true),
        new("trakt-progress-shows", "series", "My Trakt TV In Progress", "sync/playback/episodes", "show", true),
    ];

    public IReadOnlyList<TraktCatalogDefinition> GetCatalogDefinitions()
    {
        var settings = settingsProvider.GetSettings();
        if (!settings.Enabled)
        {
            return [];
        }

        return string.IsNullOrWhiteSpace(settings.AccessToken)
            ? PublicCatalogs
            : [.. PublicCatalogs, .. AccountCatalogs];
    }

    public async Task<IReadOnlyList<StremioMeta>> GetCatalogMetasAsync(
        string catalogId,
        int skip,
        CancellationToken cancellationToken
    )
    {
        var definition = PublicCatalogs
            .Concat(AccountCatalogs)
            .FirstOrDefault(item => string.Equals(item.Id, catalogId, StringComparison.Ordinal));
        if (definition is null)
        {
            throw new KeyNotFoundException($"Unknown Trakt catalog: {catalogId}");
        }

        var page = Math.Max(1, skip / 50 + 1);
        var separator = definition.Path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var path = $"{definition.Path}{separator}extended=full&page={page}&limit=50";
        using var document = await GetDocumentAsync(
                path,
                definition.RequiresAccount,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var type = definition.Type == "movie"
            ? StremioMediaType.Movie
            : StremioMediaType.Series;
        var results = new List<StremioMeta>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var media = GetMediaElement(element, definition.MediaProperty);
            if (media is null)
            {
                continue;
            }

            var meta = MapMedia(media.Value, type);
            if (meta is not null && seen.Add(meta.Id))
            {
                results.Add(meta);
            }
        }

        return results;
    }

    public async Task EnrichSeriesEpisodesAsync(
        StremioMeta series,
        CancellationToken cancellationToken
    )
    {
        if (series.Type != StremioMediaType.Series || string.IsNullOrWhiteSpace(series.TraktId))
        {
            return;
        }

        using var seasonsDocument = await GetDocumentAsync(
                $"shows/{Uri.EscapeDataString(series.TraktId)}/seasons?extended=full,episodes",
                false,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (seasonsDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var videos = new List<StremioMeta>();
        var seasonNumbers = new List<int>();
        foreach (var season in seasonsDocument.RootElement.EnumerateArray())
        {
            var seasonNumber = GetInt(season, "number");
            if (seasonNumber is null)
            {
                continue;
            }

            seasonNumbers.Add(seasonNumber.Value);
            if (TryGetProperty(season, "episodes", out var episodes))
            {
                AddEpisodes(series, episodes, videos);
            }
        }

        if (videos.Count == 0)
        {
            foreach (var seasonNumber in seasonNumbers.Distinct().OrderBy(number => number))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var episodeDocument = await GetDocumentAsync(
                        $"shows/{Uri.EscapeDataString(series.TraktId)}/seasons/{seasonNumber}?extended=full",
                        false,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                AddEpisodes(series, episodeDocument.RootElement, videos);
            }
        }

        series.Videos = videos
            .GroupBy(item => $"{item.Season}:{item.Episode}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Season)
            .ThenBy(item => item.Episode)
            .ToList();
    }

    public async Task<IReadOnlyList<TraktWatchedEpisode>> GetWatchedEpisodesAsync(
        CancellationToken cancellationToken
    )
    {
        var results = new List<TraktWatchedEpisode>();
        const int pageSize = 100;
        for (var page = 1; ; page++)
        {
            // Since July 2026 Trakt omits season progress from the default/full watched
            // response. extended=progress is the supported shape for next-episode clients,
            // and watched endpoints must now be paged explicitly.
            using var document = await GetDocumentAsync(
                    $"sync/watched/shows?extended=progress&page={page}&limit={pageSize}",
                    true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var watchedShows = document.RootElement.EnumerateArray().ToList();
            foreach (var watchedShow in watchedShows)
            {
                if (!TryGetProperty(watchedShow, "show", out var show)
                    || !TryGetProperty(show, "ids", out var ids)
                    || !TryGetProperty(watchedShow, "seasons", out var seasons)
                    || seasons.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var imdb = GetString(ids, "imdb");
                var tmdb = GetId(ids, "tmdb");
                var tvdb = GetId(ids, "tvdb");
                var trakt = GetId(ids, "trakt");
                foreach (var season in seasons.EnumerateArray())
                {
                    var seasonNumber = GetInt(season, "number");
                    if (!seasonNumber.HasValue
                        || !TryGetProperty(season, "episodes", out var episodes)
                        || episodes.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var episode in episodes.EnumerateArray())
                    {
                        var episodeNumber = GetInt(episode, "number");
                        if (!episodeNumber.HasValue)
                        {
                            continue;
                        }

                        results.Add(new TraktWatchedEpisode(
                            imdb,
                            tmdb,
                            tvdb,
                            trakt,
                            seasonNumber.Value,
                            episodeNumber.Value,
                            Math.Max(1, GetInt(episode, "plays") ?? 1),
                            ParseDateOffset(GetString(episode, "last_watched_at"))
                        ));
                    }
                }
            }

            if (watchedShows.Count < pageSize)
            {
                break;
            }
        }

        return results;
    }

    public async Task<TraktDeviceAuthorizationStatus> StartDeviceAuthorizationAsync(
        CancellationToken cancellationToken
    )
    {
        var settings = RequireAppCredentials();
        if (!string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            return ConnectedStatus(settings);
        }

        using var document = await PostDocumentAsync(
                "https://auth.trakt.tv/oauth/device/code",
                new { client_id = settings.ClientId },
                cancellationToken,
                settings
            )
            .ConfigureAwait(false);
        var root = document.RootElement;
        var code = new TraktDeviceCode(
            GetRequiredString(root, "device_code"),
            GetRequiredString(root, "user_code"),
            GetRequiredString(root, "verification_url"),
            GetRequiredInt(root, "expires_in"),
            Math.Max(1, GetRequiredInt(root, "interval")),
            DateTimeOffset.UtcNow
        );
        lock (_deviceLock)
        {
            _deviceCode = code;
            _nextDevicePoll = DateTimeOffset.UtcNow.AddSeconds(code.Interval);
        }

        return ToPendingStatus(code);
    }

    public async Task<TraktDeviceAuthorizationStatus> GetDeviceAuthorizationStatusAsync(
        CancellationToken cancellationToken
    )
    {
        var settings = settingsProvider.GetSettings();
        if (!string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            return ConnectedStatus(settings);
        }

        TraktDeviceCode? code;
        DateTimeOffset nextPoll;
        lock (_deviceLock)
        {
            code = _deviceCode;
            nextPoll = _nextDevicePoll;
        }

        if (code is null)
        {
            return new TraktDeviceAuthorizationStatus("disconnected", null, null, null, null);
        }

        if (code.CreatedAt.AddSeconds(code.ExpiresIn) <= DateTimeOffset.UtcNow)
        {
            ClearDeviceCode();
            return new TraktDeviceAuthorizationStatus(
                "expired",
                code.UserCode,
                code.VerificationUrl,
                code.CreatedAt.AddSeconds(code.ExpiresIn),
                null,
                "The activation code expired. Start a new connection."
            );
        }

        if (DateTimeOffset.UtcNow < nextPoll)
        {
            return ToPendingStatus(code);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://auth.trakt.tv/oauth/device/token"
        )
        {
            Content = JsonContent(
                new
                {
                    code = code.DeviceCode,
                    client_id = settings.ClientId,
                    client_secret = settings.ClientSecret,
                }
            ),
        };
        AddApiHeaders(request, settings, authorized: false);
        using var response = await http.Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            ScheduleNextPoll(code.Interval);
            return ToPendingStatus(code);
        }

        if ((int)response.StatusCode == 429)
        {
            ScheduleNextPoll(GetRetryAfterSeconds(response, code.Interval + 5));
            return ToPendingStatus(code) with
            {
                Message = "Trakt asked the plugin to slow down; authorization is still pending.",
            };
        }

        if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            ClearDeviceCode();
            return new TraktDeviceAuthorizationStatus(
                "expired",
                code.UserCode,
                code.VerificationUrl,
                code.CreatedAt.AddSeconds(code.ExpiresIn),
                null,
                "The activation code is no longer valid."
            );
        }

        if ((int)response.StatusCode is 409 or 418)
        {
            ClearDeviceCode();
            return new TraktDeviceAuthorizationStatus(
                "denied",
                code.UserCode,
                code.VerificationUrl,
                null,
                null,
                "Trakt authorization was denied or already used."
            );
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var tokenDocument = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        using (tokenDocument)
        {
            var token = ParseToken(tokenDocument.RootElement);
            settingsProvider.SaveTokens(token, string.Empty, settings);
            var connectedUser = await GetConnectedUserAsync(cancellationToken).ConfigureAwait(false);
            settingsProvider.SaveTokens(token, connectedUser, settings);
            ClearDeviceCode();
            return new TraktDeviceAuthorizationStatus(
                "connected",
                null,
                null,
                null,
                connectedUser,
                ConnectionSource: settings.ConnectionSource
            );
        }
    }

    public void Disconnect()
    {
        ClearDeviceCode();
        settingsProvider.Disconnect();
    }

    private async Task<JsonDocument> GetDocumentAsync(
        string path,
        bool requiresAccount,
        CancellationToken cancellationToken
    )
    {
        var settings = requiresAccount
            ? await GetAuthorizedSettingsAsync(cancellationToken).ConfigureAwait(false)
            : settingsProvider.GetSettings();
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new InvalidOperationException("A Trakt client ID (API key) is required.");
        }

        if (requiresAccount && string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            throw new InvalidOperationException("Connect a Trakt account before importing this catalog.");
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            AddApiHeaders(request, settings, requiresAccount);
            using var response = await http.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode == 429 && attempt == 0)
            {
                await Task.Delay(
                        TimeSpan.FromSeconds(GetRetryAfterSeconds(response, 1)),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument> PostDocumentAsync(
        string path,
        object payload,
        CancellationToken cancellationToken,
        TraktSettings? settings = null
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent(payload),
        };
        if (settings is not null)
        {
            AddApiHeaders(request, settings, authorized: false);
        }
        using var response = await http.Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TraktSettings> GetAuthorizedSettingsAsync(
        CancellationToken cancellationToken
    )
    {
        var settings = settingsProvider.GetSettings();
        if (!TokenNeedsRefresh(settings))
        {
            return settings;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings = settingsProvider.GetSettings();
            if (!TokenNeedsRefresh(settings))
            {
                return settings;
            }

            if (
                string.IsNullOrWhiteSpace(settings.RefreshToken)
                || string.IsNullOrWhiteSpace(settings.ClientId)
                || string.IsNullOrWhiteSpace(settings.ClientSecret)
                || string.IsNullOrWhiteSpace(settings.RedirectUri)
            )
            {
                return settings;
            }

            using var document = await PostDocumentAsync(
                    "https://auth.trakt.tv/oauth/token",
                    new
                    {
                        refresh_token = settings.RefreshToken,
                        client_id = settings.ClientId,
                        client_secret = settings.ClientSecret,
                        redirect_uri = settings.RedirectUri,
                        grant_type = "refresh_token",
                    },
                    cancellationToken,
                    settings
                )
                .ConfigureAwait(false);
            var token = ParseToken(document.RootElement);
            settingsProvider.SaveTokens(token, settings.ConnectedUser, settings);
            return settingsProvider.GetSettings();
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> GetConnectedUserAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetDocumentAsync(
                    "users/settings",
                    true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (TryGetProperty(root, "user", out var user))
            {
                var name = GetString(user, "username") ?? GetString(user, "name");
                return name ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning("Trakt connected, but the account name could not be loaded.");
        }

        return string.Empty;
    }

    private TraktSettings RequireAppCredentials()
    {
        var settings = settingsProvider.GetSettings();
        if (
            string.IsNullOrWhiteSpace(settings.ClientId)
            || string.IsNullOrWhiteSpace(settings.ClientSecret)
            || string.IsNullOrWhiteSpace(settings.RedirectUri)
        )
        {
            throw new InvalidOperationException(
                "Save the Trakt client ID, client secret, and exact registered redirect URI before connecting an account."
            );
        }

        return settings;
    }

    private static void AddApiHeaders(
        HttpRequestMessage request,
        TraktSettings settings,
        bool authorized
    )
    {
        request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        request.Headers.TryAddWithoutValidation("trakt-api-key", settings.ClientId);
        request.Headers.UserAgent.ParseAdd("NebulaBridge/0.26");
        if (authorized)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        }
    }

    private static bool TokenNeedsRefresh(TraktSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AccessToken) || settings.TokenCreatedAt <= 0 || settings.TokenExpiresIn <= 0)
        {
            return false;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(settings.TokenCreatedAt)
            .AddSeconds(settings.TokenExpiresIn);
        return expiresAt <= DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private static StremioMeta? MapMedia(JsonElement media, StremioMediaType type)
    {
        var title = GetString(media, "title");
        if (string.IsNullOrWhiteSpace(title) || !TryGetProperty(media, "ids", out var ids))
        {
            return null;
        }

        var imdb = GetString(ids, "imdb");
        var tmdb = GetId(ids, "tmdb");
        var tvdb = GetId(ids, "tvdb");
        var trakt = GetId(ids, "trakt");
        var id = !string.IsNullOrWhiteSpace(imdb)
            ? imdb
            : !string.IsNullOrWhiteSpace(tmdb)
                ? $"tmdb:{tmdb}"
                : !string.IsNullOrWhiteSpace(tvdb)
                    ? $"tvdb:{tvdb}"
                    : !string.IsNullOrWhiteSpace(trakt)
                        ? $"trakt:{trakt}"
                        : string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var released = ParseDate(GetString(media, type == StremioMediaType.Movie ? "released" : "first_aired"));
        return new StremioMeta
        {
            Id = id,
            Type = type,
            Name = title,
            Title = title,
            Year = GetInt(media, "year"),
            ReleaseInfo = GetInt(media, "year")?.ToString(CultureInfo.InvariantCulture),
            Released = released,
            Description = GetString(media, "overview"),
            Overview = GetString(media, "overview"),
            Runtime = GetInt(media, "runtime")?.ToString(CultureInfo.InvariantCulture),
            Genres = GetStrings(media, "genres"),
            ImdbRating = GetFloat(media, "rating"),
            ImdbId = imdb,
            TmdbId = tmdb,
            TvdbId = tvdb,
            TraktId = trakt,
            Slug = GetString(ids, "slug"),
        };
    }

    private static void AddEpisodes(
        StremioMeta series,
        JsonElement episodes,
        List<StremioMeta> target
    )
    {
        if (episodes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var episode in episodes.EnumerateArray())
        {
            var seasonNumber = GetInt(episode, "season");
            var episodeNumber = GetInt(episode, "number");
            if (seasonNumber is null || episodeNumber is null)
            {
                continue;
            }

            TryGetProperty(episode, "ids", out var ids);
            var tvdb = ids.ValueKind == JsonValueKind.Object ? GetId(ids, "tvdb") : null;
            var trakt = ids.ValueKind == JsonValueKind.Object ? GetId(ids, "trakt") : null;
            target.Add(
                new StremioMeta
                {
                    Id = $"{series.Id}:{seasonNumber}:{episodeNumber}",
                    Type = StremioMediaType.Episode,
                    Name = GetString(episode, "title") ?? $"Episode {episodeNumber}",
                    Title = GetString(episode, "title") ?? $"Episode {episodeNumber}",
                    Season = seasonNumber,
                    Episode = episodeNumber,
                    Number = episodeNumber,
                    FirstAired = ParseDate(GetString(episode, "first_aired")),
                    Released = ParseDate(GetString(episode, "first_aired")),
                    Description = GetString(episode, "overview"),
                    Overview = GetString(episode, "overview"),
                    Runtime = GetInt(episode, "runtime")?.ToString(CultureInfo.InvariantCulture),
                    TvdbId = tvdb,
                    TraktId = trakt,
                }
            );
        }
    }

    private static JsonElement? GetMediaElement(JsonElement element, string? propertyName)
    {
        if (propertyName is null)
        {
            return element;
        }

        return TryGetProperty(element, propertyName, out var media) ? media : null;
    }

    private static HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    private static TraktTokenResponse ParseToken(JsonElement root) =>
        new(
            GetRequiredString(root, "access_token"),
            GetRequiredString(root, "refresh_token"),
            GetRequiredInt(root, "expires_in"),
            GetRequiredLong(root, "created_at")
        );

    private static TraktDeviceAuthorizationStatus ConnectedStatus(TraktSettings settings) =>
        new(
            "connected",
            null,
            null,
            null,
            settings.ConnectedUser,
            ConnectionSource: settings.ConnectionSource,
            Message: string.Equals(
                settings.ConnectionSource,
                "jellyfin",
                StringComparison.Ordinal
            )
                ? "Using the Trakt account already connected through Jellyfin's Trakt plugin."
                : null
        );

    private static TraktDeviceAuthorizationStatus ToPendingStatus(TraktDeviceCode code)
    {
        var activationUrl = BuildActivationUrl(code);
        var qrCode = QrCode.EncodeText(activationUrl, QrCode.Ecc.Medium);
        var svg = qrCode.ToSvgString(4);
        var qrCodeDataUri = "data:image/svg+xml;base64,"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return new TraktDeviceAuthorizationStatus(
            "pending",
            code.UserCode,
            code.VerificationUrl,
            code.CreatedAt.AddSeconds(code.ExpiresIn),
            null,
            "Scan the QR code or enter this code on Trakt to connect your account.",
            ActivationUrl: activationUrl,
            QrCodeDataUri: qrCodeDataUri
        );
    }

    private static string BuildActivationUrl(TraktDeviceCode code)
    {
        if (!Uri.TryCreate(code.VerificationUrl, UriKind.Absolute, out var verificationUri))
        {
            verificationUri = new Uri("https://auth.trakt.tv/activate", UriKind.Absolute);
        }

        var builder = new UriBuilder(verificationUri)
        {
            Query = $"code={Uri.EscapeDataString(code.UserCode)}",
        };
        return builder.Uri.AbsoluteUri;
    }

    private static int GetRetryAfterSeconds(HttpResponseMessage response, int fallback)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
        }

        if (retryAfter?.Date is { } date)
        {
            return Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        }

        return Math.Max(1, fallback);
    }

    private void ScheduleNextPoll(int seconds)
    {
        lock (_deviceLock)
        {
            _nextDevicePoll = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
    }

    private void ClearDeviceCode()
    {
        lock (_deviceLock)
        {
            _deviceCode = null;
            _nextDevicePoll = default;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetId(JsonElement ids, string name)
    {
        if (!TryGetProperty(ids, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };
    }

    private static float? GetFloat(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number)
            ? number
            : null;
    }

    private static List<string> GetStrings(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed
        )
            ? parsed
            : null;

    private static DateTimeOffset? ParseDateOffset(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed
        )
            ? parsed
            : null;

    private static string GetRequiredString(JsonElement root, string name) =>
        GetString(root, name) ?? throw new JsonException($"Trakt response omitted {name}.");

    private static int GetRequiredInt(JsonElement root, string name) =>
        GetInt(root, name) ?? throw new JsonException($"Trakt response omitted {name}.");

    private static long GetRequiredLong(JsonElement root, string name)
    {
        if (TryGetProperty(root, name, out var value) && value.TryGetInt64(out var number))
        {
            return number;
        }

        throw new JsonException($"Trakt response omitted {name}.");
    }

    private sealed record TraktDeviceCode(
        string DeviceCode,
        string UserCode,
        string VerificationUrl,
        int ExpiresIn,
        int Interval,
        DateTimeOffset CreatedAt
    );
}
