using System.Net;
using NebulaBridge;
using Microsoft.Extensions.Logging.Abstractions;

namespace NebulaBridge.Tests;

public sealed class NebulaBridgeStremioProviderTests
{
    [Fact]
    public async Task ManifestAcceptsStringAndObjectResources()
    {
        const string manifest = """
            {
              "id": "test.catalog",
              "name": "Test catalog",
              "version": "1.0.0",
              "types": ["movie", "series"],
              "resources": [
                "stream",
                {"name":"catalog","types":["movie"],"idPrefixes":["tt"]}
              ],
              "catalogs": [
                {"type":"movie","id":"top","name":"Popular","extra":[{"name":"search"}]}
              ]
            }
            """;
        var provider = new NebulaBridgeStremioProvider(
            "https://catalog.example",
            new StubHttpClientFactory(manifest),
            NullLogger<NebulaBridgeStremioProvider>.Instance
        );

        var result = await provider.GetManifestAsync();

        Assert.NotNull(result);
        Assert.Equal(2, result.Resources.Count);
        Assert.Equal("stream", result.Resources[0].Name);
        Assert.Equal("catalog", result.Resources[1].Name);
        Assert.Equal(["movie"], result.Resources[1].Types);
        Assert.Equal(["tt"], result.Resources[1].IdPrefixes);
        Assert.Single(result.Catalogs);
        Assert.True(result.Catalogs[0].IsSearchCapable());
    }

    [Fact]
    public async Task InvalidResourceShapeFailsManifestWithoutThrowingToCaller()
    {
        const string manifest = """
            {"id":"bad","name":"Bad","version":"1","resources":[42],"catalogs":[]}
            """;
        var provider = new NebulaBridgeStremioProvider(
            "https://catalog.example",
            new StubHttpClientFactory(manifest),
            NullLogger<NebulaBridgeStremioProvider>.Instance
        );

        var result = await provider.GetManifestAsync();

        Assert.Null(result);
    }

    [Fact]
    public void StreamIdentityIsStableWithinOneMediaItem()
    {
        var stream = new StremioStream { Url = "https://media.example/shared.mp4" };

        Assert.Equal(stream.GetGuid("tt123:1:1"), stream.GetGuid("tt123:1:1"));
    }

    [Fact]
    public void StreamIdentityIsScopedToOwningMediaItem()
    {
        var stream = new StremioStream { Url = "https://media.example/shared.mp4" };

        Assert.NotEqual(stream.GetGuid("tt123:1:1"), stream.GetGuid("tt456:2:1"));
    }

    private sealed class StubHttpClientFactory(string response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(response));
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response),
                }
            );
    }
}
