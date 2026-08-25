using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class EBookBayLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task OfficialDefinitionUsesPinnedCertificateAndDownloadInfoHashFlow()
    {
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable("NEBULA_BRIDGE_LIVE_EBOOKBAY_TEST"),
                "1",
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable("NEBULA_BRIDGE_LIVE_EBOOKBAY_DEFINITION")
            ?? throw new InvalidOperationException("Set the live EBookBay definition path.");
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(
                [new IndexerDefinitionSource(path, await File.ReadAllTextAsync(path))]
            ),
            new CardigannTestSupport.MemoryPreferenceStore(["ebookbay"])
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var results = await client.SearchAsync(
            loader.GetRequired("ebookbay"),
            new NativeMediaQuery("Moby-Dick"),
            timeout.Token
        );

        Assert.NotEmpty(results);
        Assert.Contains(results, result =>
            result.InfoHash is not null && result.MagnetUrl is not null
        );
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
