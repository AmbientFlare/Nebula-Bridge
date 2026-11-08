using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NebulaBridge.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace NebulaBridge.Tests;

public sealed class NativeTraktClientTests
{
    [Fact]
    public async Task TrendingMoviesBecomeImportableMetadata()
    {
        var handler = new RecordingHandler(
            [
                JsonResponse(
                    """
                    [{
                      "watchers": 42,
                      "movie": {
                        "title": "Example Movie",
                        "year": 2024,
                        "overview": "Example overview",
                        "runtime": 121,
                        "rating": 8.25,
                        "genres": ["drama"],
                        "ids": {"trakt": 7, "slug": "example-movie", "imdb": "tt1234567", "tmdb": 99}
                      }
                    }]
                    """
                ),
            ]
        );
        var settings = new MutableSettingsProvider(ConnectedSettings(accessToken: string.Empty));
        using var http = new TraktHttpClient(handler);
        var client = CreateClient(http, settings);

        var result = await client.GetCatalogMetasAsync(
            "trakt-movies-trending",
            0,
            CancellationToken.None
        );

        var movie = Assert.Single(result);
        Assert.Equal("tt1234567", movie.Id);
        Assert.Equal("99", movie.TmdbId);
        Assert.Equal("7", movie.TraktId);
        Assert.Equal("Example Movie", movie.Name);
        Assert.Equal(2024, movie.Year);
        Assert.Equal(["drama"], movie.Genres);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("test-client-id", request.TraktApiKey);
        Assert.Equal("2", request.TraktApiVersion);
        Assert.Null(request.Authorization);
        Assert.Contains("extended=full", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeriesExpansionUsesEpisodesEmbeddedByTrakt()
    {
        var handler = new RecordingHandler(
            [
                JsonResponse(
                    """
                    [{
                      "number": 1,
                      "episodes": [{
                        "season": 1,
                        "number": 2,
                        "title": "Second Episode",
                        "overview": "Episode overview",
                        "first_aired": "2024-02-03T04:00:00.000Z",
                        "runtime": 47,
                        "ids": {"trakt": 81, "tvdb": 9001}
                      }]
                    }]
                    """
                ),
            ]
        );
        var settings = new MutableSettingsProvider(ConnectedSettings(accessToken: string.Empty));
        using var http = new TraktHttpClient(handler);
        var client = CreateClient(http, settings);
        var series = new StremioMeta
        {
            Id = "tt7654321",
            Type = StremioMediaType.Series,
            Name = "Example Show",
            TraktId = "55",
        };

        await client.EnrichSeriesEpisodesAsync(series, CancellationToken.None);

        var episode = Assert.Single(series.Videos!);
        Assert.Equal(1, episode.Season);
        Assert.Equal(2, episode.Episode);
        Assert.Equal("9001", episode.TvdbId);
        Assert.Equal("tt7654321:1:2", episode.Id);
        Assert.Single(handler.Requests);
        Assert.Contains("extended=full,episodes", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceCodeFlowPersistsConnectedAccount()
    {
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var handler = new RecordingHandler(
            [
                JsonResponse(
                    """{"device_code":"device-secret","user_code":"ABCD1234","verification_url":"https://trakt.tv/activate","expires_in":600,"interval":1}"""
                ),
                JsonResponse(
                    $$"""{"access_token":"access-secret","refresh_token":"refresh-secret","expires_in":604800,"created_at":{{createdAt}}}"""
                ),
                JsonResponse("""{"user":{"username":"mediafan","name":"Media Fan"}}"""),
            ]
        );
        var settings = new MutableSettingsProvider(ConnectedSettings(accessToken: string.Empty));
        using var http = new TraktHttpClient(handler);
        var client = CreateClient(http, settings);

        var started = await client.StartDeviceAuthorizationAsync(CancellationToken.None);
        Assert.Equal("pending", started.State);
        Assert.Equal("ABCD1234", started.UserCode);
        Assert.Equal("https://trakt.tv/activate?code=ABCD1234", started.ActivationUrl);
        Assert.StartsWith("data:image/svg+xml;base64,", started.QrCodeDataUri, StringComparison.Ordinal);
        var svg = Encoding.UTF8.GetString(
            Convert.FromBase64String(started.QrCodeDataUri!["data:image/svg+xml;base64,".Length..])
        );
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Equal("auth.trakt.tv", handler.Requests[0].Uri.Host);
        Assert.Equal("test-client-id", handler.Requests[0].TraktApiKey);

        await Task.Delay(1100);
        var completed = await client.GetDeviceAuthorizationStatusAsync(CancellationToken.None);

        Assert.Equal("connected", completed.State);
        Assert.Equal("mediafan", completed.ConnectedUser);
        Assert.Equal("access-secret", settings.Current.AccessToken);
        Assert.Equal("refresh-secret", settings.Current.RefreshToken);
        Assert.Equal("mediafan", settings.Current.ConnectedUser);
        Assert.Contains("device-secret", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal("auth.trakt.tv", handler.Requests[1].Uri.Host);
        Assert.DoesNotContain("device-secret", handler.Requests[1].Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.Requests[2].Authorization?.Scheme);
        Assert.Equal("access-secret", handler.Requests[2].Authorization?.Parameter);
    }

    [Fact]
    public async Task ExpiredAccessTokenIsRefreshedBeforePersonalCatalogRequest()
    {
        var oldCreatedAt = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeSeconds();
        var newCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var handler = new RecordingHandler(
            [
                JsonResponse(
                    $$"""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":604800,"created_at":{{newCreatedAt}}}"""
                ),
                JsonResponse(
                    """[{"movie":{"title":"Saved Movie","year":2020,"ids":{"trakt":4,"imdb":"tt0000004"}}}]"""
                ),
            ]
        );
        var settings = new MutableSettingsProvider(
            ConnectedSettings("old-access") with
            {
                RefreshToken = "old-refresh",
                TokenCreatedAt = oldCreatedAt,
                TokenExpiresIn = 60,
            }
        );
        using var http = new TraktHttpClient(handler);
        var client = CreateClient(http, settings);

        var result = await client.GetCatalogMetasAsync(
            "trakt-watchlist-movies",
            0,
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Contains("old-refresh", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains(
            "urn:ietf:wg:oauth:2.0:oob",
            handler.Requests[0].Body,
            StringComparison.Ordinal
        );
        Assert.Equal("auth.trakt.tv", handler.Requests[0].Uri.Host);
        Assert.Equal("new-access", handler.Requests[1].Authorization?.Parameter);
        Assert.Equal("new-refresh", settings.Current.RefreshToken);
    }

    [Fact]
    public void AccountConnectionAddsPersonalCatalogs()
    {
        var handler = new RecordingHandler([]);
        using var http = new TraktHttpClient(handler);
        var settings = new MutableSettingsProvider(ConnectedSettings(accessToken: string.Empty));
        var client = CreateClient(http, settings);

        Assert.Equal(6, client.GetCatalogDefinitions().Count);

        settings.Current = ConnectedSettings("connected-access");

        var catalogs = client.GetCatalogDefinitions();
        Assert.Equal(18, catalogs.Count);
        Assert.Contains(catalogs, item => item.Id == "trakt-watchlist-shows");
        Assert.Contains(catalogs, item => item.Id == "trakt-progress-movies");
        Assert.Contains(catalogs, item => item.Id == "trakt-ratings-shows");
    }

    [Fact]
    public async Task ApiGetRetriesOnceAfterRateLimitResponse()
    {
        var limited = new HttpResponseMessage((HttpStatusCode)429);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var handler = new RecordingHandler(
            [
                limited,
                JsonResponse(
                    """[{"movie":{"title":"Recovered Movie","ids":{"trakt":12,"imdb":"tt0000012"}}}]"""
                ),
            ]
        );
        var settings = new MutableSettingsProvider(ConnectedSettings(accessToken: string.Empty));
        using var http = new TraktHttpClient(handler);
        var client = CreateClient(http, settings);

        var result = await client.GetCatalogMetasAsync(
            "trakt-movies-trending",
            0,
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExistingJellyfinTraktConnectionIsReportedAsInherited()
    {
        var handler = new RecordingHandler([]);
        var settings = new MutableSettingsProvider(
            ConnectedSettings("existing-access") with
            {
                ConnectionSource = "jellyfin",
                LinkedJellyfinUserId = Guid.NewGuid().ToString("D"),
            }
        );
        using var http = new TraktHttpClient(handler);
        var client = CreateClient(http, settings);

        var status = await client.GetDeviceAuthorizationStatusAsync(CancellationToken.None);

        Assert.Equal("connected", status.State);
        Assert.Equal("jellyfin", status.ConnectionSource);
        Assert.Contains("already connected", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WatchedShowsMapToEpisodeHistoryForJellyfinNextUp()
    {
        var handler = new RecordingHandler(
            [
                JsonResponse(
                    """
                    [{
                      "show":{"title":"Ted Lasso","ids":{"trakt":123,"imdb":"tt10986410","tmdb":97546,"tvdb":383203}},
                      "seasons":[{"number":1,"episodes":[
                        {"number":1,"plays":2,"last_watched_at":"2026-08-23T12:34:56.000Z"},
                        {"number":2,"plays":1,"last_watched_at":"2026-08-23T13:34:56.000Z"}
                      ]}]
                    }]
                    """
                ),
            ]
        );
        using var http = new TraktHttpClient(handler);
        var client = CreateClient(
            http,
            new MutableSettingsProvider(ConnectedSettings("connected-access"))
        );

        var watched = await client.GetWatchedEpisodesAsync(CancellationToken.None);

        Assert.Equal(2, watched.Count);
        Assert.Equal("tt10986410", watched[0].ShowImdbId);
        Assert.Equal("97546", watched[0].ShowTmdbId);
        Assert.Equal((1, 1, 2), (watched[0].Season, watched[0].Episode, watched[0].Plays));
        Assert.Equal(2026, watched[0].LastWatchedAt!.Value.Year);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Authorization!.Scheme);
        Assert.Equal("/sync/watched/shows", request.Uri.AbsolutePath);
        Assert.Contains("extended=progress", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("page=1", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=100", request.Uri.Query, StringComparison.Ordinal);
    }

    private static NativeTraktClient CreateClient(
        TraktHttpClient http,
        ITraktSettingsProvider settings
    ) => new(http, settings, NullLogger<NativeTraktClient>.Instance);

    private static TraktSettings ConnectedSettings(string accessToken) =>
        new(
            true,
            "test-client-id",
            "test-client-secret",
            "urn:ietf:wg:oauth:2.0:oob",
            accessToken,
            string.Empty,
            0,
            0,
            string.Empty
        );

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private sealed class MutableSettingsProvider(TraktSettings initial) : ITraktSettingsProvider
    {
        public TraktSettings Current { get; set; } = initial;

        public TraktSettings GetSettings() => Current;

        public void SaveTokens(
            TraktTokenResponse token,
            string connectedUser,
            TraktSettings? sourceSettings = null
        )
        {
            Current = Current with
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                TokenCreatedAt = token.CreatedAt,
                TokenExpiresIn = token.ExpiresIn,
                ConnectedUser = connectedUser,
            };
        }

        public void Disconnect()
        {
            Current = Current with
            {
                AccessToken = string.Empty,
                RefreshToken = string.Empty,
                TokenCreatedAt = 0,
                TokenExpiresIn = 0,
                ConnectedUser = string.Empty,
            };
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        string? TraktApiKey,
        string? TraktApiVersion,
        string Body
    );

    private sealed class RecordingHandler(IEnumerable<HttpResponseMessage> responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(
                new RequestSnapshot(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization,
                    request.Headers.TryGetValues("trakt-api-key", out var keys)
                        ? keys.Single()
                        : null,
                    request.Headers.TryGetValues("trakt-api-version", out var versions)
                        ? versions.Single()
                        : null,
                    request.Content is null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync(cancellationToken)
                )
            );
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("No mocked Trakt response remains.");
        }
    }
}
