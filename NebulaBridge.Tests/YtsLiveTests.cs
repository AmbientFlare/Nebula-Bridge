using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class YtsLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task OfficialDefinitionExpandsTorrentVariantsFromJsonApi()
    {
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable("NEBULA_BRIDGE_LIVE_YTS_TEST"),
                "1",
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable("NEBULA_BRIDGE_LIVE_YTS_DEFINITION")
            ?? throw new InvalidOperationException("Set the live YTS definition path.");
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(
                [new IndexerDefinitionSource(path, await File.ReadAllTextAsync(path))]
            ),
            new CardigannTestSupport.MemoryPreferenceStore(["yts"])
        );
        await loader.RefreshAsync(CancellationToken.None);
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var results = await client.SearchAsync(
            loader.GetRequired("yts"),
            new NativeMediaQuery("Night of the Living Dead", 1968),
            timeout.Token
        );

        Assert.NotEmpty(results);
        Assert.All(results, result =>
        {
            Assert.Equal("yts", result.SourceId);
            Assert.NotNull(result.InfoHash);
            Assert.NotNull(result.MagnetUrl);
        });
    }

    private sealed class LiveHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
    }
}
