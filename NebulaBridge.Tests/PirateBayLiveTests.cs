using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class PirateBayLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task OfficialDefinitionUsesGenericConfiguredApiAndNormalizesResults()
    {
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable("NEBULA_BRIDGE_LIVE_TPB_TEST"),
                "1",
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable("NEBULA_BRIDGE_LIVE_TPB_DEFINITION")
            ?? throw new InvalidOperationException("Set the live TPB definition path.");
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(
                [new IndexerDefinitionSource(path, await File.ReadAllTextAsync(path))]
            ),
            new CardigannTestSupport.MemoryPreferenceStore(["thepiratebay"])
        );
        var refresh = await loader.RefreshAsync(CancellationToken.None);
        Assert.True(refresh.Success);
        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.True(summary.Compatible, summary.Error);

        var templates = new CardigannTemplateEngine();
        var filters = new CardigannValueFilters(templates);
        var client = new NativeIndexerClient(
            new LiveHttpClientFactory(),
            new NetworkTargetValidator(),
            templates,
            filters,
            new CardigannResponseParser(templates, filters),
            NullLogger<NativeIndexerClient>.Instance
        );

        var results = await client.SearchAsync(
            loader.GetRequired("thepiratebay"),
            new NativeMediaQuery("ubuntu"),
            CancellationToken.None
        );

        Assert.NotEmpty(results);
        Assert.All(results, result =>
        {
            Assert.Equal("thepiratebay", result.SourceId);
            Assert.NotNull(result.InfoHash);
            Assert.NotNull(result.MagnetUrl);
        });
    }

    private sealed class LiveHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                }
            )
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
    }
}
