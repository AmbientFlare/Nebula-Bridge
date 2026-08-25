using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class IndexerCatalogUpdaterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "nebula-catalog-tests",
        Guid.NewGuid().ToString("N")
    );
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public async Task SignedUpdateIsTransactionalAndNewDefinitionsDefaultDisabled()
    {
        var showRss = await File.ReadAllBytesAsync(
            CardigannTestSupport.FixturePath("showrss.yml")
        );
        var internetArchive = await File.ReadAllBytesAsync(
            CardigannTestSupport.FixturePath("internetarchive.yml")
        );
        var catalog = new TestCatalog(_signingKey, new() { ["showrss-yml"] = showRss });
        var preferences = new CardigannTestSupport.MemoryPreferenceStore();
        var (updater, loader) = Build(catalog, preferences);

        var first = await updater.UpdateAsync(CancellationToken.None);
        Assert.True(first.Success, first.Message);
        Assert.True(first.Changed);
        await loader.RefreshAsync(CancellationToken.None);
        Assert.True(loader.SetEnabled("showrss-yml", true));

        catalog.Definitions["internetarchive"] = internetArchive;
        catalog.Revision++;
        var second = await updater.UpdateAsync(CancellationToken.None);
        Assert.True(second.Success, second.Message);
        await loader.RefreshAsync(CancellationToken.None);

        var summaries = loader.GetAllSummaries();
        Assert.True(Assert.Single(summaries, item => item.Id == "showrss-yml").Enabled);
        Assert.False(Assert.Single(summaries, item => item.Id == "internetarchive").Enabled);
        Assert.Equal(2, Directory.EnumerateFiles(_directory, "*.yml").Count());
    }

    [Fact]
    public async Task HashFailureRetainsLastKnownGoodDefinitions()
    {
        var original = await File.ReadAllBytesAsync(
            CardigannTestSupport.FixturePath("showrss.yml")
        );
        var catalog = new TestCatalog(_signingKey, new() { ["showrss-yml"] = original });
        var (updater, _) = Build(catalog, new CardigannTestSupport.MemoryPreferenceStore());
        Assert.True((await updater.UpdateAsync(CancellationToken.None)).Success);
        var previousYaml = await File.ReadAllBytesAsync(Path.Combine(_directory, "showrss-yml.yml"));
        var previousState = await File.ReadAllBytesAsync(
            Path.Combine(_directory, ".catalog-state.json")
        );

        catalog.Definitions["showrss-yml"] = original
            .Concat(Encoding.UTF8.GetBytes("\n# changed\n"))
            .ToArray();
        catalog.Revision++;
        catalog.CorruptDefinitionResponse = true;
        var failed = await updater.UpdateAsync(CancellationToken.None);

        Assert.False(failed.Success);
        Assert.Contains("last-known-good", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previousYaml, await File.ReadAllBytesAsync(Path.Combine(_directory, "showrss-yml.yml")));
        Assert.Equal(previousState, await File.ReadAllBytesAsync(Path.Combine(_directory, ".catalog-state.json")));
    }

    [Fact]
    public async Task InvalidManifestSignatureIsRejectedBeforeAnyDefinitionDownload()
    {
        var yaml = await File.ReadAllBytesAsync(
            CardigannTestSupport.FixturePath("showrss.yml")
        );
        var catalog = new TestCatalog(_signingKey, new() { ["showrss-yml"] = yaml })
        {
            CorruptSignature = true,
        };
        var (updater, _) = Build(catalog, new CardigannTestSupport.MemoryPreferenceStore());

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(_directory));
        Assert.Equal(0, catalog.DefinitionRequests);
    }

    [Fact]
    public async Task DefinitionNoLongerInManifestIsRemovedButPreferenceRemains()
    {
        var showRss = await File.ReadAllBytesAsync(
            CardigannTestSupport.FixturePath("showrss.yml")
        );
        var internetArchive = await File.ReadAllBytesAsync(
            CardigannTestSupport.FixturePath("internetarchive.yml")
        );
        var catalog = new TestCatalog(
            _signingKey,
            new() { ["showrss-yml"] = showRss, ["internetarchive"] = internetArchive }
        );
        var preferences = new CardigannTestSupport.MemoryPreferenceStore(["internetarchive"]);
        var (updater, loader) = Build(catalog, preferences);
        Assert.True((await updater.UpdateAsync(CancellationToken.None)).Success);
        await loader.RefreshAsync(CancellationToken.None);
        Assert.True(Assert.Single(loader.GetAllSummaries(), item => item.Id == "internetarchive").Enabled);

        catalog.Definitions.Remove("internetarchive");
        catalog.Revision++;
        Assert.True((await updater.UpdateAsync(CancellationToken.None)).Success);
        await loader.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain(loader.GetAllSummaries(), item => item.Id == "internetarchive");
        Assert.Contains("internetarchive", preferences.GetEnabledIds());
    }

    private (IndexerCatalogUpdater Updater, IndexerDefinitionLoader Loader) Build(
        TestCatalog catalog,
        IIndexerPreferenceStore preferences
    )
    {
        var provider = new LocalIndexerDefinitionProvider(
            _directory,
            NullLogger<LocalIndexerDefinitionProvider>.Instance
        );
        var parser = CardigannTestSupport.CreateParser();
        var settings = new TestSettings(
            Convert.ToBase64String(
                _signingKey.ExportSubjectPublicKeyInfo()
            )
        );
        var updater = new IndexerCatalogUpdater(
            new CardigannTestSupport.StubHttpClientFactory(
                new CardigannTestSupport.StubHandler(catalog.Respond)
            ),
            settings,
            provider,
            parser,
            NullLogger<IndexerCatalogUpdater>.Instance
        );
        return (updater, CardigannTestSupport.CreateLoader(provider, preferences));
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestSettings(string publicKey) : IIndexerCatalogSettings
    {
        public bool Enabled => true;
        public Uri ManifestUri { get; } = new(
            "http://127.0.0.1/api/v1/indexers/manifest"
        );
        public string PublicKeyBase64 { get; } = publicKey;
        public Version ClientVersion { get; } = new(1, 0, 0);
    }

    private sealed class TestCatalog(ECDsa signingKey, Dictionary<string, byte[]> definitions)
    {
        public Dictionary<string, byte[]> Definitions { get; } = definitions;
        public int Revision { get; set; } = 1;
        public bool CorruptDefinitionResponse { get; set; }
        public bool CorruptSignature { get; set; }
        public int DefinitionRequests { get; private set; }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal))
            {
                var payload = ManifestBytes();
                var signature = signingKey.SignData(
                    payload,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence
                );
                if (CorruptSignature)
                    signature[0] ^= 0x40;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                };
                response.Headers.Add("X-Nebula-Signature-Algorithm", "ecdsa-p256-sha256");
                response.Headers.Add("X-Nebula-Signature", Convert.ToBase64String(signature));
                return response;
            }

            var id = request.RequestUri.AbsolutePath.Split('/').Last();
            DefinitionRequests++;
            var content = Definitions[id];
            if (CorruptDefinitionResponse)
                content = content.Concat(Encoding.UTF8.GetBytes("corrupt")).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        }

        private byte[] ManifestBytes()
        {
            var entries = Definitions
                .OrderBy(item => item.Key)
                .Select(item => new
                {
                    id = item.Key,
                    name = item.Key,
                    definition_version = 11,
                    sha256 = Convert.ToHexString(SHA256.HashData(item.Value)).ToLowerInvariant(),
                    size_bytes = item.Value.Length,
                    definition_url = $"/api/v1/indexers/{item.Key}",
                });
            return JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    api_version = 1,
                    catalog_version = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes($"catalog-{Revision}"))
                    ).ToLowerInvariant(),
                    cardigann_schema = 11,
                    minimum_client_version = "1.0.0",
                    generated_at = "2026-08-23T20:00:00Z",
                    indexers = entries,
                }
            );
        }
    }
}
