using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NebulaBridge.NativeSources;
using Microsoft.Extensions.Logging.Abstractions;

namespace NebulaBridge.Tests;

public sealed class TorBoxStreamResolverTests
{
    private const string InfoHash = "0123456789abcdef0123456789abcdef01234567";
    private const string SecondHash = "89abcdef0123456789abcdef0123456789abcdef";
    private const string ApiToken = "test-token-that-must-not-be-logged";

    [Fact]
    public async Task DisabledProviderMakesNoRequests()
    {
        var handler = new RecordingHandler([]);
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(false, ApiToken));

        var result = await provider.CheckCachedAsync([InfoHash], CancellationToken.None);

        Assert.NotNull(result.Failure);
        Assert.Equal("not_configured", result.Failure.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CacheCheckBatchesHashesAndMapsCachedAndUncachedResults()
    {
        var handler = new RecordingHandler(
            [
                JsonResponse(
                    $$"""
                    {"success":true,"data":[{"hash":"{{InfoHash.ToUpperInvariant()}}","files":[
                      {"id":7,"name":"release.mkv","size":900,"mimetype":"video/x-matroska"}
                    ]}]}
                    """
                ),
            ]
        );
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));

        var result = await provider.CheckCachedAsync(
            [InfoHash.ToUpperInvariant(), InfoHash, SecondHash],
            CancellationToken.None
        );

        Assert.Null(result.Failure);
        Assert.True(result.Availability[InfoHash].Cached);
        Assert.Equal("release.mkv", Assert.Single(result.Availability[InfoHash].Files).Name);
        Assert.False(result.Availability[SecondHash].Cached);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("format=list", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("list_files=true", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains(InfoHash, request.Body, StringComparison.Ordinal);
        Assert.Contains(SecondHash, request.Body, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(request.Body, InfoHash));
    }

    [Fact]
    public async Task ProviderFailureIsStructured()
    {
        var handler = new RecordingHandler([JsonResponse("not-json")]);
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));

        var result = await provider.CheckCachedAsync([InfoHash], CancellationToken.None);

        Assert.Empty(result.Availability);
        Assert.Equal("cache", result.Failure?.Stage);
        Assert.Equal("jsonexception", result.Failure?.Reason);
    }

    [Fact]
    public async Task AuthenticationFailureIsStructured()
    {
        var handler = new RecordingHandler(
            [
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        """{"success":false,"error":"BAD_TOKEN","data":null}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                },
            ]
        );
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));

        var result = await provider.CheckCachedAsync([InfoHash], CancellationToken.None);

        Assert.Equal("authentication_rejected", result.Failure?.Reason);
    }

    [Fact]
    public async Task LargeCacheLookupUsesBoundedBatches()
    {
        var handler = new RecordingHandler(
            [JsonResponse("""{"success":true,"data":[]}"""), JsonResponse("""{"success":true,"data":[]}""")]
        );
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));
        var hashes = Enumerable.Range(0, 101).Select(value => value.ToString("x40")).ToArray();

        var result = await provider.CheckCachedAsync(hashes, CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal(101, result.Availability.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PlaybackUsesCachedOnlyThenAuthoritativeFilesAndCurrentUrl()
    {
        var handler = new RecordingHandler(
            [
                JsonResponse("""{"success":true,"data":{"torrent_id":42}}"""),
                JsonResponse(
                    """
                    {"success":true,"data":{"id":42,"files":[
                    {"id":1,"name":"sample.mp4","size":5000,"mimetype":"video/mp4"},
                    {"id":7,"name":"Test.Release.2026.mkv","size":900,"mimetype":"video/x-matroska"}
                    ]}}
                    """
                ),
                JsonResponse("""{"success":true,"data":"https://cdn.torbox.test/release.mkv"}"""),
            ]
        );
        using var http = new TorBoxHttpClient(handler);
        var validator = new RecordingValidator();
        var provider = CreateProvider(http, new(true, ApiToken), validator);

        var result = await provider.ResolvePlaybackAsync(
            BuildCachedCandidate(),
            MovieQuery(),
            CancellationToken.None
        );

        Assert.NotNull(result.Stream);
        Assert.Equal("Test.Release.2026.mkv", result.Stream.Filename);
        Assert.Equal(900, result.Stream.SizeBytes);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("torrents/createtorrent", handler.Requests[0].Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("add_only_if_cached", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("true", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("torrents/mylist", handler.Requests[1].Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("bypass_cache=true", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("file_id=7", handler.Requests[2].Uri.Query, StringComparison.Ordinal);
        Assert.Equal(ApiToken, GetQueryValue(handler.Requests[2].Uri, "token"));
        Assert.Equal(1, validator.CallCount);
    }

    [Theory]
    [InlineData("Ted.Lasso.S01E01.1080p.mkv", "Ted Lasso", 1, 1)]
    [InlineData("Silo.2x01.2160p.mkv", "Silo", 2, 1)]
    public async Task PlaybackSelectsExactEpisodeFromSeasonPack(
        string expectedName,
        string title,
        int season,
        int episode
    )
    {
        var handler = new RecordingHandler(
            [
                JsonResponse("""{"success":true,"data":{"torrent_id":42}}"""),
                JsonResponse(
                    """
                    {"success":true,"data":{"files":[
                      {"id":1,"name":"EXPECTED_NAME","size":800},
                      {"id":2,"name":"Other.Show.S09E09.mkv","size":5000},
                      {"id":3,"name":"sample.mp4","size":9000}
                    ]}}
                    """
                        .Replace("EXPECTED_NAME", expectedName, StringComparison.Ordinal)
                ),
                JsonResponse("""{"success":true,"data":"https://cdn.torbox.test/episode.mkv"}"""),
            ]
        );
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));

        var result = await provider.ResolvePlaybackAsync(
            BuildCachedCandidate(),
            new NativeMediaQuery(title, Season: season, Episode: episode),
            CancellationToken.None
        );

        Assert.NotNull(result.Stream);
        Assert.Contains("file_id=1", handler.Requests[2].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaybackRejectsCorrectEpisodeNumberFromWrongShowBeforeUrlRequest()
    {
        var handler = new RecordingHandler(
            [
                JsonResponse("""{"success":true,"data":{"torrent_id":42}}"""),
                JsonResponse(
                    """
                    {"success":true,"data":{"files":[
                      {"id":1,"name":"The.President.Carter.S01E01.1080p.WEB-DL.mkv","size":800}
                    ]}}
                    """
                ),
            ]
        );
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));

        var result = await provider.ResolvePlaybackAsync(
            BuildCachedCandidate(),
            new NativeMediaQuery("Ted Lasso", Season: 1, Episode: 1),
            CancellationToken.None
        );

        Assert.Null(result.Stream);
        Assert.Equal("media_title_mismatch", result.Failure?.Reason);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("Ted.Laso.S01E01.1080p.mkv")]
    [InlineData("TedLasso.S01E01.WEB-DL.mkv")]
    public async Task PlaybackAcceptsLenientEpisodeTitleVariants(string filename)
    {
        var handler = new RecordingHandler(
            [
                JsonResponse("""{"success":true,"data":{"torrent_id":42}}"""),
                JsonResponse(
                    """
                    {"success":true,"data":{"files":[
                      {"id":1,"name":"EXPECTED_NAME","size":800}
                    ]}}
                    """
                        .Replace("EXPECTED_NAME", filename, StringComparison.Ordinal)
                ),
                JsonResponse("""{"success":true,"data":"https://cdn.torbox.test/episode.mkv"}"""),
            ]
        );
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));

        var result = await provider.ResolvePlaybackAsync(
            BuildCachedCandidate(),
            new NativeMediaQuery("Ted Lasso", Season: 1, Episode: 1),
            CancellationToken.None
        );

        Assert.NotNull(result.Stream);
        Assert.Contains("file_id=1", handler.Requests[2].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousSeasonPackIsRejectedBeforeUrlRequest()
    {
        var handler = new RecordingHandler(
            [
                JsonResponse("""{"success":true,"data":{"torrent_id":42}}"""),
                JsonResponse(
                    """
                    {"success":true,"data":{"files":[
                      {"id":1,"name":"Show.S01E02.mkv","size":800},
                      {"id":2,"name":"Show.S01E03.mkv","size":900}
                    ]}}
                    """
                ),
            ]
        );
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));

        var result = await provider.ResolvePlaybackAsync(
            BuildCachedCandidate(),
            new NativeMediaQuery("Show", Season: 1, Episode: 1),
            CancellationToken.None
        );

        Assert.Null(result.Stream);
        Assert.Equal("media_file_missing_or_ambiguous", result.Failure?.Reason);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UncachedCandidateCannotMutateAccount()
    {
        var handler = new RecordingHandler([]);
        using var http = new TorBoxHttpClient(handler);
        var provider = CreateProvider(http, new(true, ApiToken));

        var result = await provider.ResolvePlaybackAsync(
            BuildCachedCandidate() with { Availability = [new("torbox", false, [])] },
            MovieQuery(),
            CancellationToken.None
        );

        Assert.Null(result.Stream);
        Assert.Equal("not_cached", result.Failure?.Reason);
        Assert.Empty(handler.Requests);
    }

    private static TorBoxStreamResolver CreateProvider(
        TorBoxHttpClient http,
        TorBoxResolverSettings settings,
        INetworkTargetValidator? validator = null
    ) =>
        new(
            http,
            new StaticSettingsProvider(settings),
            validator ?? new RecordingValidator(),
            NullLogger<TorBoxStreamResolver>.Instance
        );

    private static NativeReleaseCandidate BuildCachedCandidate() =>
        new(
            "test-indexer",
            "Test Release",
            new Uri($"magnet:?xt=urn:btih:{InfoHash}"),
            "torrent",
            InfoHash,
            1000,
            Availability: [new DebridAvailability("torbox", true, [new(7, "release.mkv", 900)])],
            Playable: true
        );

    private static NativeMediaQuery MovieQuery() => new("Test Release", Year: 2026);

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private static int CountOccurrences(string input, string value) =>
        input.Split(value, StringSplitOptions.None).Length - 1;

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (var component in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = component.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private sealed class StaticSettingsProvider(TorBoxResolverSettings settings)
        : ITorBoxSettingsProvider
    {
        public TorBoxResolverSettings GetSettings() => settings;
    }

    private sealed class RecordingValidator : INetworkTargetValidator
    {
        public int CallCount { get; private set; }

        public Task ValidateAsync(Uri uri, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
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
                    request.Content is null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync(cancellationToken)
                )
            );
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("No mocked TorBox response remains.");
        }
    }
}
