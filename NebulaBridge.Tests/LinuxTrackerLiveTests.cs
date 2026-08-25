using NebulaBridge.NativeSources;
using Microsoft.Extensions.Logging.Abstractions;

namespace NebulaBridge.Tests;

public sealed class LinuxTrackerLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task OfficialHtmlDefinitionCanQueryItsPublicSite()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NEBULA_BRIDGE_LIVE_INDEXER_TEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.GetDirectoryName(
            CardigannTestSupport.FixturePath("linuxtracker.yml")
        )!;
        var preferences = new CardigannTestSupport.MemoryPreferenceStore(["linuxtracker"]);
        var loader = CardigannTestSupport.CreateLoader(
            new LocalIndexerDefinitionProvider(
                directory,
                NullLogger<LocalIndexerDefinitionProvider>.Instance
            ),
            preferences
        );
        await loader.RefreshAsync(CancellationToken.None);
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
            loader.GetRequired("linuxtracker"),
            new NativeMediaQuery("ubuntu"),
            CancellationToken.None
        );

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal("linuxtracker", result.SourceId));
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
                Timeout = TimeSpan.FromSeconds(15),
            };
    }
}
