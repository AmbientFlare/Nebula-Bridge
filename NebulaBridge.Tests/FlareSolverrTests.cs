using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class FlareSolverrTests
{
    [Theory]
    [InlineData("192.168.1.20:8191", "http://192.168.1.20:8191/v1")]
    [InlineData("http://solver:8191/", "http://solver:8191/v1")]
    [InlineData("https://solver.example/v1", "https://solver.example/v1")]
    public void NormalizesSafeEndpoints(string input, string expected) =>
        Assert.Equal(expected, PluginFlareSolverrSettings.NormalizeEndpoint(input)?.AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("ftp://solver:8191")]
    [InlineData("http://user:password@solver:8191")]
    [InlineData("http://solver:8191/v1?token=secret")]
    [InlineData("http://solver:8191/admin")]
    public void RejectsInvalidEndpoints(string input) =>
        Assert.Null(PluginFlareSolverrSettings.NormalizeEndpoint(input));

    [Fact]
    public async Task FlareSolverrClientSendsFormPostAndParsesSolution()
    {
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(request =>
            {
                Assert.Equal(new Uri("http://solver:8191/v1"), request.RequestUri);
                Assert.Equal(HttpMethod.Post, request.Method);
                using var payload = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
                );
                Assert.Equal("request.post", payload.RootElement.GetProperty("cmd").GetString());
                Assert.Equal(
                    "https://example.com/search",
                    payload.RootElement.GetProperty("url").GetString()
                );
                Assert.Equal("q=ubuntu", payload.RootElement.GetProperty("postData").GetString());
                var cookies = payload.RootElement.GetProperty("cookies");
                Assert.Equal("layout", cookies[0].GetProperty("name").GetString());
                Assert.Equal("links", cookies[0].GetProperty("value").GetString());
                Assert.Equal("empty", cookies[1].GetProperty("name").GetString());
                Assert.Equal(string.Empty, cookies[1].GetProperty("value").GetString());
                var headers = payload.RootElement.GetProperty("headers");
                Assert.Equal("https://example.com/", headers.GetProperty("Referer").GetString());
                Assert.Equal("en-US", headers.GetProperty("Accept-Language").GetString());
                return Json("""
                    {"status":"ok","message":"","solution":{"url":"https://example.com/results","status":200,"response":"<html>solved</html>"}}
                    """);
            })
        );
        var client = new FlareSolverrClient(
            factory,
            new CardigannTestSupport.FixedFlareSolverrSettings("http://solver:8191/v1")
        );

        var result = await client.SendAsync(
            new Uri("https://example.com/search"),
            HttpMethod.Post,
            "q=ubuntu",
            "layout=links; empty=; malformed",
            CancellationToken.None,
            new Dictionary<string, string>
            {
                ["Referer"] = "https://example.com/",
                ["Accept-Language"] = "en-US",
            }
        );

        Assert.Equal("https://example.com/results", result.ResponseUri.AbsoluteUri);
        Assert.Equal("<html>solved</html>", result.Content);
    }

    [Fact]
    public async Task NativeIndexerPrefersDirectEvenWhenDefinitionDeclaresFlareSolverr()
    {
        // The info_flaresolverr flag is only an authoring hint. If the host answers an ordinary
        // request, the solver must not be touched at all — this is the whole saving.
        var solverCalls = 0;
        var directCalls = 0;
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(request =>
            {
                if (request.RequestUri!.Host == "solver")
                {
                    solverCalls++;
                    return Json("""{"status":"ok","message":"","solution":{"url":"https://example.com/","status":200,"response":"<html></html>"}}""");
                }

                directCalls++;
                return Html(HttpStatusCode.OK, ResultMarkup);
            })
        );

        var result = Assert.Single(await BuildClient(factory).SearchAsync(
            FlareDefinition(),
            new NativeMediaQuery("ubuntu"),
            CancellationToken.None
        ));

        Assert.Equal(1, directCalls);
        Assert.Equal(0, solverCalls);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", result.InfoHash);
    }

    [Fact]
    public async Task NativeIndexerFallsBackToFlareSolverrOnACloudflareChallenge()
    {
        var solverCalls = 0;
        var directCalls = 0;
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(request =>
            {
                if (request.RequestUri!.Host == "solver")
                {
                    solverCalls++;
                    using var payload = JsonDocument.Parse(
                        request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
                    );
                    Assert.Equal("request.get", payload.RootElement.GetProperty("cmd").GetString());
                    Assert.Contains("q=ubuntu", payload.RootElement.GetProperty("url").GetString());
                    return Json(SolvedJson);
                }

                directCalls++;
                var challenge = Html(
                    HttpStatusCode.Forbidden,
                    "<title>Just a moment...</title><script>window._cf_chl_opt={}</script>"
                );
                challenge.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
                return challenge;
            })
        );

        var result = Assert.Single(await BuildClient(factory).SearchAsync(
            FlareDefinition(),
            new NativeMediaQuery("ubuntu"),
            CancellationToken.None
        ));

        Assert.Equal(1, directCalls);
        Assert.Equal(1, solverCalls);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", result.InfoHash);
    }

    [Fact]
    public async Task NativeIndexerRemembersAChallengedHostAndGoesStraightToTheSolver()
    {
        var solverCalls = 0;
        var directCalls = 0;
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(request =>
            {
                if (request.RequestUri!.Host == "solver")
                {
                    solverCalls++;
                    return Json(SolvedJson);
                }

                directCalls++;
                var challenge = Html(HttpStatusCode.Forbidden, "blocked");
                challenge.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
                return challenge;
            })
        );

        var client = BuildClient(factory);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Single(await client.SearchAsync(
                FlareDefinition(),
                new NativeMediaQuery("ubuntu"),
                CancellationToken.None
            ));
        }

        // The wasted direct probe is paid once per host, not once per search.
        Assert.Equal(1, directCalls);
        Assert.Equal(3, solverCalls);
    }

    [Fact]
    public async Task NativeIndexerDoesNotCallTheSolverForAGenuineOriginError()
    {
        var solverCalls = 0;
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(request =>
            {
                if (request.RequestUri!.Host == "solver")
                {
                    solverCalls++;
                    return Json("""{"status":"ok","message":"","solution":{"url":"https://example.com/","status":200,"response":"<html></html>"}}""");
                }

                var denied = Html(HttpStatusCode.Forbidden, "<h1>403 Forbidden</h1>");
                denied.Headers.Server.Add(new ProductInfoHeaderValue("nginx", null));
                return denied;
            })
        );

        await Assert.ThrowsAnyAsync<Exception>(() => BuildClient(factory).SearchAsync(
            FlareDefinition(),
            new NativeMediaQuery("ubuntu"),
            CancellationToken.None
        ));

        Assert.Equal(0, solverCalls);
    }

    [Fact]
    public void AnUnprobedHostGetsTheSolverBudget()
    {
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(_ => Html(HttpStatusCode.OK, ResultMarkup))
        );

        // Nothing is known about the host yet, so the probe may still end at the solver. The
        // budget has to cover that, and the probe itself is capped so it can.
        Assert.Equal(
            NativeIndexerClient.FlareSolverrSearchTimeout,
            BuildClient(factory).GetSearchTimeout(FlareDefinition())
        );
    }

    [Fact]
    public async Task AHostThatAnsweredDirectlyGetsTheDirectBudget()
    {
        // The flag says FlareSolverr; the host says otherwise. The budget follows the host.
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(_ => Html(HttpStatusCode.OK, ResultMarkup))
        );
        var client = BuildClient(factory);
        await client.SearchAsync(FlareDefinition(), new NativeMediaQuery("ubuntu"), CancellationToken.None);

        Assert.Equal(
            NativeIndexerClient.DirectSearchTimeout,
            client.GetSearchTimeout(FlareDefinition())
        );
    }

    [Fact]
    public async Task AChallengedHostGetsTheSolverBudgetEvenWhenTheDefinitionIsUnflagged()
    {
        // This is the regression direct-first introduced: an unflagged definition whose host
        // does challenge used to be given fifteen seconds, which the solver cannot meet.
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(request =>
            {
                if (request.RequestUri!.Host == "solver")
                {
                    return Json(SolvedJson);
                }

                var challenge = Html(
                    HttpStatusCode.Forbidden,
                    "<title>Just a moment...</title><script>window._cf_chl_opt={}</script>"
                );
                challenge.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
                return challenge;
            })
        );
        var unflagged = DirectDefinition();
        var client = BuildClient(factory);
        await client.SearchAsync(unflagged, new NativeMediaQuery("ubuntu"), CancellationToken.None);

        Assert.Equal(
            NativeIndexerClient.FlareSolverrSearchTimeout,
            client.GetSearchTimeout(unflagged)
        );
    }

    private const string ResultMarkup =
        "<div class='result'><a class='title'>Ubuntu 26.04</a><a class='download' href='magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567'>download</a></div>";

    private static readonly string SolvedJson =
        """{"status":"ok","message":"","solution":{"url":"https://example.com/search?q=ubuntu","status":200,"response":"""
        + System.Text.Json.JsonSerializer.Serialize(ResultMarkup)
        + "}}";

    private static NativeIndexerClient BuildClient(IHttpClientFactory factory)
    {
        var templates = new CardigannTemplateEngine();
        var filters = new CardigannValueFilters(templates);
        return new NativeIndexerClient(
            factory,
            new CardigannTestSupport.AllowTestTargets(),
            templates,
            filters,
            new CardigannResponseParser(templates, filters),
            new FlareSolverrClient(
                factory,
                new CardigannTestSupport.FixedFlareSolverrSettings("http://solver:8191/v1")
            ),
            NullLogger<NativeIndexerClient>.Instance
        );
    }

    private static IndexerDefinition FlareDefinition()
    {
        var definition = CardigannTestSupport.BuildDefinition(
            "flare-fixture",
            "html",
            ".result",
            JsonNode.Parse(
                """{"title":{"selector":".title"},"download":{"selector":".download","attribute":"href"}}"""
            )!.AsObject(),
            JsonNode.Parse("""{"q":"{{ .Keywords }}"}""")!.AsObject()
        );
        return new IndexerDefinition
        {
            Id = definition.Id,
            Name = definition.Name,
            Type = definition.Type,
            Links = definition.Links,
            Document = definition.Document,
            SourcePath = definition.SourcePath,
            RequiresFlareSolverr = true,
        };
    }

    private static IndexerDefinition DirectDefinition()
    {
        var flagged = FlareDefinition();
        return new IndexerDefinition
        {
            Id = flagged.Id,
            Name = flagged.Name,
            Type = flagged.Type,
            Links = flagged.Links,
            Document = flagged.Document,
            SourcePath = flagged.SourcePath,
            RequiresFlareSolverr = false,
        };
    }

    private static HttpResponseMessage Html(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "text/html") };

    [Fact]
    public async Task SolverErrorsDoNotExposeAnUnusableResponse()
    {
        var factory = new CardigannTestSupport.StubHttpClientFactory(
            new CardigannTestSupport.StubHandler(_ => Json(
                """{"status":"error","message":"challenge failed","solution":null}"""
            ))
        );
        var client = new FlareSolverrClient(
            factory,
            new CardigannTestSupport.FixedFlareSolverrSettings("http://solver:8191/v1")
        );

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(
            new Uri("https://example.com/"),
            HttpMethod.Get,
            null,
            null,
            CancellationToken.None
        ));

        Assert.Contains("challenge failed", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };
}
