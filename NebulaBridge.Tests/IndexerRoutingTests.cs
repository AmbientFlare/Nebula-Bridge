using System.Net;
using System.Net.Http.Headers;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public class CloudflareChallengeDetectorTests
{
    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string? server = null,
        string? mitigated = null,
        bool cfRay = false
    )
    {
        var response = new HttpResponseMessage(status);
        if (server is not null)
        {
            response.Headers.Server.Add(new ProductInfoHeaderValue(server, null));
        }

        if (mitigated is not null)
        {
            response.Headers.TryAddWithoutValidation("cf-mitigated", mitigated);
        }

        if (cfRay)
        {
            response.Headers.TryAddWithoutValidation("cf-ray", "8d0000000000-IAD");
        }

        return response;
    }

    [Fact]
    public void MitigatedHeaderIsConclusive()
    {
        using var response = Response(HttpStatusCode.Forbidden, mitigated: "challenge");
        Assert.True(CloudflareChallengeDetector.HasChallengeHeaders(response));
        Assert.True(CloudflareChallengeDetector.IsChallenge(response, content: null));
    }

    [Fact]
    public void ChallengeBodyFromCloudflareIsDetected()
    {
        using var response = Response(HttpStatusCode.Forbidden, server: "cloudflare");
        Assert.True(
            CloudflareChallengeDetector.IsChallenge(
                response,
                "<title>Just a moment...</title><script>window._cf_chl_opt={}</script>"
            )
        );
    }

    [Fact]
    public void ChallengeServedWithTwoHundredIsDetected()
    {
        using var response = Response(HttpStatusCode.OK, cfRay: true);
        Assert.True(
            CloudflareChallengeDetector.IsChallenge(response, "please enable JavaScript and cookies to continue")
        );
    }

    [Fact]
    public void GenuineForbiddenFromOriginIsNotAChallenge()
    {
        // The whole point of direct-first is that a real error stays an error. A 403 from an
        // ordinary server must not cost an ~11s solver round trip.
        using var response = Response(HttpStatusCode.Forbidden, server: "nginx");
        Assert.False(CloudflareChallengeDetector.IsChallenge(response, "<h1>403 Forbidden</h1>"));
    }

    [Fact]
    public void ChallengeMarkersWithoutCloudflareHeadersAreNotTrusted()
    {
        // A torrent site legitimately listing a release named "Just a moment" must not be
        // mistaken for an interstitial.
        using var response = Response(HttpStatusCode.OK, server: "nginx");
        Assert.False(CloudflareChallengeDetector.IsChallenge(response, "Just a moment..."));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void OnlyCloudflareStatusesAreProbed(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, CloudflareChallengeDetector.IsChallengeStatus(status));
}

public class IndexerRouteCacheTests
{
    [Fact]
    public void UnknownHostHasNoRouteSoTheCallerProbesDirect() =>
        Assert.Null(new IndexerRouteCache().GetRoute("example.org"));

    [Fact]
    public void RecordedRoutesAreReturned()
    {
        var cache = new IndexerRouteCache();
        cache.RecordDirect("direct.example");
        cache.RecordChallenge("protected.example");

        Assert.Equal(IndexerRoute.Direct, cache.GetRoute("direct.example"));
        Assert.Equal(IndexerRoute.FlareSolverr, cache.GetRoute("protected.example"));
    }

    [Fact]
    public void HostsAreMatchedCaseInsensitively()
    {
        var cache = new IndexerRouteCache();
        cache.RecordChallenge("Protected.Example");
        Assert.Equal(IndexerRoute.FlareSolverr, cache.GetRoute("protected.example"));
    }

    [Fact]
    public void RouteIsForgottenAfterTheTtlSoAPostureChangeIsFollowed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new IndexerRouteCache(time);
        cache.RecordChallenge("protected.example");
        Assert.Equal(IndexerRoute.FlareSolverr, cache.GetRoute("protected.example"));

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(cache.GetRoute("protected.example"));
    }

    [Fact]
    public void RouteSurvivesWithinTheTtl()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new IndexerRouteCache(time);
        cache.RecordDirect("direct.example");

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(IndexerRoute.Direct, cache.GetRoute("direct.example"));
    }

    [Fact]
    public void ALaterDecisionReplacesAnEarlierOne()
    {
        var cache = new IndexerRouteCache();
        cache.RecordDirect("switching.example");
        cache.RecordChallenge("switching.example");
        Assert.Equal(IndexerRoute.FlareSolverr, cache.GetRoute("switching.example"));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
