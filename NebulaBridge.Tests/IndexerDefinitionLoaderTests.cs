using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class IndexerDefinitionLoaderTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task ExactCatalogDefinitionCanBeDiagnosed()
    {
        var path = Environment.GetEnvironmentVariable("NEBULA_BRIDGE_CATALOG_DEFINITION_TEST");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(
                [new IndexerDefinitionSource(path, await File.ReadAllTextAsync(path))]
            ),
            new CardigannTestSupport.MemoryPreferenceStore()
        );
        await loader.RefreshAsync(CancellationToken.None);
        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.True(summary.Loaded, summary.Error);
    }

    [Fact]
    public async Task DiscoversAndValidatesOfficialV11ProofDefinitions()
    {
        var sources = new[] { "internetarchive.yml", "linuxtracker.yml", "showrss.yml" }
            .Select(name => new IndexerDefinitionSource(
                CardigannTestSupport.FixturePath(name),
                File.ReadAllText(CardigannTestSupport.FixturePath(name))
            ))
            .ToList();
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(sources),
            new CardigannTestSupport.MemoryPreferenceStore()
        );

        var refresh = await loader.RefreshAsync(CancellationToken.None);

        Assert.True(refresh.Success);
        var diagnostic = string.Join(
            " | ",
            loader.GetAllSummaries().Select(item => $"{item.Id}: {item.Error}")
        );
        Assert.Equal(3, refresh.LoadedCount);
        Assert.True(refresh.CompatibleCount == 3, diagnostic);
        Assert.All(loader.GetAllSummaries(), summary =>
        {
            Assert.True(summary.Loaded, summary.Error);
            Assert.True(summary.Compatible, summary.Error);
            Assert.False(summary.Enabled);
            Assert.Equal("disabled", summary.State);
            Assert.Equal(11, summary.DefinitionVersion);
        });
    }

    [Fact]
    public async Task HttpDecodedUtf8BomDoesNotBreakYamlLoading()
    {
        var yaml = "\uFEFF" + File.ReadAllText(
            CardigannTestSupport.FixturePath("showrss.yml")
        );
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider([new("bom.yml", yaml)]),
            new CardigannTestSupport.MemoryPreferenceStore()
        );

        await loader.RefreshAsync(CancellationToken.None);

        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.True(summary.Loaded, summary.Error);
        Assert.True(summary.Compatible, summary.Error);
    }

    [Fact]
    public async Task MalformedYamlIsReportedWithoutBreakingOtherDefinitions()
    {
        var valid = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"));
        var provider = new CardigannTestSupport.MemoryDefinitionProvider(
            [new("showrss.yml", valid), new("broken.yml", "id: [unterminated")]
        );
        var loader = CardigannTestSupport.CreateLoader(
            provider,
            new CardigannTestSupport.MemoryPreferenceStore()
        );

        var refresh = await loader.RefreshAsync(CancellationToken.None);

        Assert.True(refresh.Success);
        Assert.Equal(1, refresh.InvalidCount);
        var broken = Assert.Single(loader.GetAllSummaries(), item => item.Id == "broken");
        Assert.Equal("invalid", broken.State);
        Assert.Contains("Malformed YAML", broken.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaViolationIsClassifiedAsInvalid()
    {
        var yaml = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"))
            .Replace("description: \"showRSS is a service that allows you to keep track of your favorite TV shows\"\n", string.Empty, StringComparison.Ordinal);
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider([new("invalid-schema.yml", yaml)]),
            new CardigannTestSupport.MemoryPreferenceStore()
        );

        await loader.RefreshAsync(CancellationToken.None);

        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.False(summary.Loaded);
        Assert.Equal("invalid", summary.State);
        Assert.Contains("schema validation failed", summary.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticationRequirementsAreVisibleAndCannotBeEnabled()
    {
        var yaml = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"))
            .Replace("type: public", "type: private", StringComparison.Ordinal);
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider([new("unsupported.yml", yaml)]),
            new CardigannTestSupport.MemoryPreferenceStore()
        );

        await loader.RefreshAsync(CancellationToken.None);

        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.Equal("unsupported", summary.State);
        Assert.Contains("authentication", summary.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(loader.SetEnabled(summary.Id, true));
    }

    [Fact]
    public async Task SchemaValidUnsupportedFilterIsReportedExplicitly()
    {
        var yaml = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"))
            .Replace("name: andmatch", "name: strdump", StringComparison.Ordinal);
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider([new("unsupported-filter.yml", yaml)]),
            new CardigannTestSupport.MemoryPreferenceStore()
        );

        await loader.RefreshAsync(CancellationToken.None);

        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.True(summary.Loaded);
        Assert.False(summary.Compatible);
        Assert.Equal("unsupported", summary.State);
        Assert.Contains("Unsupported Cardigann filter: strdump", summary.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyLinksKeywordFiltersAndOptionalTextSettingsAreCompatible()
    {
        var yaml = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"))
            .Replace(
                "  - https://showrss.info/",
                "  - https://showrss.info/\nlegacylinks:\n  - https://legacy.showrss.info/",
                StringComparison.Ordinal
            )
            .Replace(
                "settings: []",
                "settings:\n  - name: uploader\n    type: text\n    label: Optional uploader",
                StringComparison.Ordinal
            )
            .Replace(
                "search:\n  paths:",
                "search:\n  keywordsfilters:\n    - name: tolower\n  paths:",
                StringComparison.Ordinal
            );
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider([new("extended.yml", yaml)]),
            new CardigannTestSupport.MemoryPreferenceStore(["showrss-yml"])
        );

        await loader.RefreshAsync(CancellationToken.None);

        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.True(summary.Compatible, summary.Error);
        Assert.Equal(2, loader.GetRequired("showrss-yml").Links.Count);
    }

    [Fact]
    public async Task CertificateMetadataAndSimpleInfoHashDownloadFlowAreCompatible()
    {
        var yaml = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"))
            .Replace(
                "settings: []",
                "certificates:\n  - 1dad798f690521cfb37d6817b66f1f08fe8c8f36\nsettings: []\ndownload:\n  infohash:\n    hash:\n      selector: 'td:contains(\"Info Hash:\") ~ td'\n      filters:\n        - name: regexp\n          args: '[A-Fa-f0-9]{40}'\n    title:\n      selector: div.postname\n      filters:\n        - name: trim",
                StringComparison.Ordinal
            );
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider([new("download.yml", yaml)]),
            new CardigannTestSupport.MemoryPreferenceStore()
        );

        await loader.RefreshAsync(CancellationToken.None);

        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.True(summary.Compatible, summary.Error);
    }

    [Fact]
    public async Task FutureSchemaVersionIsRejectedCleanly()
    {
        var yaml = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"));
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(
                [new IndexerDefinitionSource("future.yml", yaml, DefinitionVersion: 12)]
            ),
            new CardigannTestSupport.MemoryPreferenceStore()
        );

        await loader.RefreshAsync(CancellationToken.None);

        var summary = Assert.Single(loader.GetAllSummaries());
        Assert.Equal(12, summary.DefinitionVersion);
        Assert.Equal("unsupported", summary.State);
        Assert.Contains("schema version 12", summary.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RescanPreservesPreferencesAndNewDefinitionsDefaultDisabled()
    {
        var showRss = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"));
        var archive = File.ReadAllText(CardigannTestSupport.FixturePath("internetarchive.yml"));
        var preferences = new CardigannTestSupport.MemoryPreferenceStore();
        var provider = new CardigannTestSupport.MemoryDefinitionProvider(
            [new("showrss.yml", showRss)]
        );
        var loader = CardigannTestSupport.CreateLoader(provider, preferences);
        await loader.RefreshAsync(CancellationToken.None);
        Assert.True(loader.SetEnabled("showrss-yml", true));

        loader = CardigannTestSupport.CreateLoader(provider, preferences);
        await loader.RefreshAsync(CancellationToken.None);
        Assert.True(Assert.Single(loader.GetAllSummaries()).Enabled);

        provider.Sources =
        [
            new(
                "showrss-renamed-file.yml",
                showRss.Replace("name: showRSS", "name: showRSS Renamed", StringComparison.Ordinal)
            ),
            new("internetarchive.yml", archive),
        ];
        await loader.RefreshAsync(CancellationToken.None);

        var summaries = loader.GetAllSummaries();
        Assert.True(Assert.Single(summaries, item => item.Id == "showrss-yml").Enabled);
        Assert.Equal(
            "showRSS Renamed",
            Assert.Single(summaries, item => item.Id == "showrss-yml").Name
        );
        Assert.False(Assert.Single(summaries, item => item.Id == "internetarchive").Enabled);
    }

    [Fact]
    public async Task LocalProviderDiscoversYamlAndYamlExtensions()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nebula-cardigann-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "one.yml"), "id: one");
            await File.WriteAllTextAsync(Path.Combine(directory, "two.yaml"), "id: two");
            await File.WriteAllTextAsync(Path.Combine(directory, "ignore.txt"), "id: ignore");
            var provider = new LocalIndexerDefinitionProvider(
                directory,
                NullLogger<LocalIndexerDefinitionProvider>.Instance
            );

            var sources = await provider.LoadAsync(CancellationToken.None);

            Assert.Equal(2, sources.Count);
            Assert.Contains(sources, source =>
                source.Path.EndsWith("one.yml", StringComparison.Ordinal)
            );
            Assert.Contains(sources, source =>
                source.Path.EndsWith("two.yaml", StringComparison.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
