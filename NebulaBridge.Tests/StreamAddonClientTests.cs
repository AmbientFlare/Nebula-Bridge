using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class StreamAddonClientTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef01234567";
    private const string HashB = "89abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("https://torrentio.strem.fun", "https://torrentio.strem.fun/stream/series/tt1234567%3A4%3A7.json")]
    [InlineData("https://torrentio.strem.fun/", "https://torrentio.strem.fun/stream/series/tt1234567%3A4%3A7.json")]
    [InlineData("https://torrentio.strem.fun/manifest.json", "https://torrentio.strem.fun/stream/series/tt1234567%3A4%3A7.json")]
    [InlineData("https://comet.example/abc123/manifest.json", "https://comet.example/abc123/stream/series/tt1234567%3A4%3A7.json")]
    public void AnEpisodeIsAskedForBySeriesImdbIdSeasonAndEpisode(string addon, string expected)
    {
        var query = new NativeMediaQuery("Reacher", Season: 4, Episode: 7, ImdbId: "tt1234567");

        Assert.Equal(expected, StreamAddonClient.BuildStreamUri(addon, query)!.AbsoluteUri);
    }

    [Fact]
    public void AMovieIsAskedForByImdbIdAlone()
    {
        var query = new NativeMediaQuery("Heat", 1995, ImdbId: "tt0113277");

        Assert.Equal(
            "https://torrentio.strem.fun/stream/movie/tt0113277.json",
            StreamAddonClient.BuildStreamUri("https://torrentio.strem.fun", query)!.AbsoluteUri
        );
    }

    [Theory]
    [InlineData("ftp://torrentio.strem.fun")]
    [InlineData("https://user:secret@torrentio.strem.fun")]
    [InlineData("not a url")]
    public void AnUnusableAddonUrlYieldsNoRequest(string addon)
    {
        Assert.Null(StreamAddonClient.BuildStreamUri(addon, new NativeMediaQuery("Heat", ImdbId: "tt0113277")));
    }

    [Fact]
    public void ATitleWithoutAnImdbIdIsNotAsked()
    {
        Assert.Null(StreamAddonClient.BuildStreamUri("https://torrentio.strem.fun", new NativeMediaQuery("Heat")));
    }

    [Fact]
    public void TheSourceIdNeverCarriesTheConfiguredPath()
    {
        Assert.Equal("addon:comet.example", StreamAddonClient.SourceIdFor("https://comet.example/eyJkZWJyaWQiOiJzZWNyZXQifQ/manifest.json"));
    }

    [Fact]
    public void ATorrentioStreamBecomesAMagnetCandidateWithSeedersAndSize()
    {
        var stream = new StreamAddonClient.AddonStream(
            Name: "Torrentio\n1080p",
            Title: "Reacher.S04E07.1080p.WEB.H264-GROUP\n👤 61 💾 2.53 GB ⚙️ ThePirateBay",
            Description: null,
            InfoHash: HashA.ToUpperInvariant(),
            FileIdx: 0,
            Url: null,
            Sources: ["tracker:udp://tracker.opentrackr.org:1337/announce", "dht:" + HashA],
            BehaviorHints: new(Filename: null, VideoSize: null, BingeGroup: "torrentio|1080p")
        );

        var candidate = StreamAddonClient.ToCandidate(stream, "addon:torrentio.strem.fun", "torrentio.strem.fun")!;

        Assert.Equal("Reacher.S04E07.1080p.WEB.H264-GROUP", candidate.Title);
        Assert.Equal(HashA, candidate.InfoHash);
        Assert.Equal(61, candidate.Seeders);
        Assert.Equal((long)(2.53 * 1024 * 1024 * 1024), candidate.SizeBytes);
        Assert.Equal("torrent", candidate.Kind);
        Assert.Equal("addon:torrentio.strem.fun", candidate.SourceId);
        Assert.StartsWith("magnet:?xt=urn:btih:" + HashA, candidate.MagnetUrl!.AbsoluteUri);
        Assert.Contains("tr=udp%3A%2F%2Ftracker.opentrackr.org", candidate.MagnetUrl.AbsoluteUri);
        Assert.DoesNotContain("dht", candidate.MagnetUrl.AbsoluteUri);
    }

    [Fact]
    public void ACometStreamNamesTheReleaseFromItsFilenameAndSizeFromItsHints()
    {
        var stream = new StreamAddonClient.AddonStream(
            Name: "[TORRENT] Comet 1080p",
            Title: null,
            Description: "💿 Reacher.S04E07.1080p.WEB.H264-GROUP\n💾 2.53 GB 👤 61\n🔎 Torrentio",
            InfoHash: HashB,
            FileIdx: 3,
            Url: null,
            Sources: null,
            BehaviorHints: new(Filename: "Reacher.S04E07.1080p.WEB.H264-GROUP.mkv", VideoSize: 2_716_000_000, BingeGroup: null)
        );

        var candidate = StreamAddonClient.ToCandidate(stream, "addon:comet.example", "comet.example")!;

        Assert.Equal("Reacher.S04E07.1080p.WEB.H264-GROUP.mkv", candidate.Title);
        Assert.Equal(2_716_000_000, candidate.SizeBytes);
        Assert.Equal(61, candidate.Seeders);
    }

    [Fact]
    public void ADebridUrlStreamIsSomebodyElsesAccountAndIsSkipped()
    {
        var stream = new StreamAddonClient.AddonStream(
            "[RD+] Torrentio", "Reacher.S04E07.mkv", null, InfoHash: null, FileIdx: null,
            Url: "https://torrentio.strem.fun/realdebrid/KEY/abc/0/Reacher.mkv", Sources: null, BehaviorHints: null
        );

        Assert.Null(StreamAddonClient.ToCandidate(stream, "addon:torrentio.strem.fun", "torrentio.strem.fun"));
    }

    [Fact]
    public async Task EveryAddonIsAskedAtOnceAndAFailedOneOnlyReportsAFailure()
    {
        var requested = new List<Uri>();
        var handler = new CardigannTestSupport.StubHandler(request =>
        {
            lock (requested)
                requested.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "down.example")
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            var hash = request.RequestUri.Host == "torrentio.strem.fun" ? HashA : HashB;
            return Json($$"""{"streams":[{"name":"X","title":"Release.{{request.RequestUri.Host}}\n👤 5 💾 1 GB","infoHash":"{{hash}}"}]}""");
        });
        var client = new StreamAddonClient(new CardigannTestSupport.StubHttpClientFactory(handler), NullLogger<StreamAddonClient>.Instance);

        var result = await client.SearchAsync(
            ["https://torrentio.strem.fun", "https://comet.example/cfg", "https://down.example"],
            new NativeMediaQuery("Reacher", Season: 4, Episode: 7, ImdbId: "tt1234567"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None
        );

        Assert.Equal(3, requested.Count);
        Assert.Equal(2, result.Candidates.Count);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("addon:down.example", failure.SourceId);
        Assert.Equal("failed", failure.Reason);
    }

    [Fact]
    public async Task AnAddonThatDoesNotAnswerInTimeIsAFailureNotAWait()
    {
        var handler = new DelayingHandler(TimeSpan.FromSeconds(10));
        var client = new StreamAddonClient(new CardigannTestSupport.StubHttpClientFactory(handler), NullLogger<StreamAddonClient>.Instance);

        var result = await client.SearchAsync(
            ["https://slow.example"],
            new NativeMediaQuery("Heat", ImdbId: "tt0113277"),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None
        );

        Assert.Empty(result.Candidates);
        Assert.Equal("timeout", Assert.Single(result.Failures).Reason);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
