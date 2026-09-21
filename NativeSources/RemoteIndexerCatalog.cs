using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.NativeSources;

public interface IIndexerCatalogSettings
{
    bool Enabled { get; }
    Uri ManifestUri { get; }
    string PublicKeyBase64 { get; }
    Version ClientVersion { get; }
}

public sealed class PluginIndexerCatalogSettings : IIndexerCatalogSettings
{
    public bool Enabled =>
        NebulaBridgePlugin.Instance?.Configuration.EnableRemoteIndexerCatalog == true;

    public Uri ManifestUri
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(
                "NEBULA_BRIDGE_INDEXER_CATALOG_URL"
            );
            configured = string.IsNullOrWhiteSpace(configured)
                ? NebulaBridgePlugin.Instance?.Configuration.IndexerCatalogManifestUrl
                : configured;
            return new Uri(configured ?? IndexerCatalogDefaults.ManifestUrl, UriKind.Absolute);
        }
    }

    public string PublicKeyBase64
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(
                "NEBULA_BRIDGE_INDEXER_CATALOG_PUBLIC_KEY"
            );
            return string.IsNullOrWhiteSpace(configured)
                ? NebulaBridgePlugin.Instance?.Configuration.IndexerCatalogPublicKey
                    ?? IndexerCatalogDefaults.PublicKeyBase64
                : configured;
        }
    }

    public Version ClientVersion => new(1, 0, 0);
}

public static class IndexerCatalogDefaults
{
    public const string ManifestUrl =
        "https://indexers.watchastra.com/api/v1/indexers/manifest";

    // This is populated with the deployment's public verification key. The private key lives
    // only on the catalog server and may also be overridden through plugin configuration.
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEdP6w9C0wyE6weuNKvr//k9utpVqVW9JV0S7bkgdaSdK0gqZfJHCnu8Ng73nSv18W3Qv4kkCzgjRCf4T71lnAOg==";
}

public sealed record IndexerCatalogUpdateResult(
    bool Success,
    bool Changed,
    string Message,
    string? CatalogVersion = null,
    int Downloaded = 0,
    int Reused = 0
);

internal sealed record IndexerCatalogManifest(
    [property: JsonPropertyName("api_version")] int ApiVersion,
    [property: JsonPropertyName("catalog_version")] string CatalogVersion,
    [property: JsonPropertyName("cardigann_schema")] int CardigannSchema,
    [property: JsonPropertyName("minimum_client_version")] string MinimumClientVersion,
    [property: JsonPropertyName("generated_at")] string? GeneratedAt,
    [property: JsonPropertyName("indexers")] IReadOnlyList<IndexerCatalogEntry> Indexers
);

internal sealed record IndexerCatalogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("definition_version")] int DefinitionVersion,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size_bytes")] int SizeBytes,
    [property: JsonPropertyName("definition_url")] string DefinitionUrl
);

internal sealed record LocalCatalogState(
    [property: JsonPropertyName("catalog_version")] string CatalogVersion,
    [property: JsonPropertyName("definitions")] Dictionary<string, string> Definitions
);

public sealed class IndexerCatalogUpdater(
    IHttpClientFactory httpClientFactory,
    IIndexerCatalogSettings settings,
    IIndexerDefinitionProvider provider,
    CardigannDefinitionParser parser,
    ILogger<IndexerCatalogUpdater> logger
)
{
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const int MaximumDefinitionBytes = 512 * 1024;
    private const int MaximumDefinitions = 1000;
    private const string StateFileName = ".catalog-state.json";
    private static readonly Regex SafeId = new(
        "^[a-z0-9][a-z0-9-]{1,127}$",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant
    );
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public async Task<IndexerCatalogUpdateResult> UpdateAsync(
        CancellationToken cancellationToken
    )
    {
        if (!settings.Enabled)
        {
            return new(false, false, "Remote indexer catalog updates are disabled.");
        }

        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            logger.LogInformation("Checking indexer catalog");
            ValidateManifestUri(settings.ManifestUri);
            var client = httpClientFactory.CreateClient(nameof(IndexerCatalogUpdater));
            using var response = await client
                .GetAsync(settings.ManifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await ReadLimitedAsync(
                    response.Content,
                    MaximumManifestBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
            VerifySignature(response, payload);
            var manifest = JsonSerializer.Deserialize<IndexerCatalogManifest>(payload)
                ?? throw new InvalidDataException("Catalog manifest is empty.");
            ValidateManifest(manifest);

            var activeDirectory = Path.GetFullPath(provider.DefinitionsDirectory);
            var state = await ReadStateAsync(activeDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (state?.CatalogVersion == manifest.CatalogVersion)
            {
                logger.LogInformation("Catalog unchanged: {CatalogVersion}", manifest.CatalogVersion);
                return new(
                    true,
                    false,
                    "Indexer catalog is already current.",
                    manifest.CatalogVersion
                );
            }

            logger.LogInformation(
                "Catalog update available: {OldVersion} -> {NewVersion}",
                state?.CatalogVersion ?? "none",
                manifest.CatalogVersion
            );
            var parent = Directory.GetParent(activeDirectory)?.FullName
                ?? throw new InvalidDataException("Indexer definition directory has no parent.");
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, $".indexers.staging.{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var downloaded = 0;
            var reused = 0;
            try
            {
                foreach (var entry in manifest.Indexers.OrderBy(item => item.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = Path.Combine(staging, $"{entry.Id}.yml");
                    var current = Path.Combine(activeDirectory, $"{entry.Id}.yml");
                    byte[] content;
                    if (
                        state?.Definitions.TryGetValue(entry.Id, out var currentHash) == true
                        && currentHash == entry.Sha256
                        && File.Exists(current)
                    )
                    {
                        content = await File.ReadAllBytesAsync(current, cancellationToken)
                            .ConfigureAwait(false);
                        if (!HashMatches(content, entry.Sha256))
                        {
                            content = await DownloadDefinitionAsync(client, entry, cancellationToken)
                                .ConfigureAwait(false);
                            downloaded++;
                        }
                        else
                        {
                            reused++;
                        }
                    }
                    else
                    {
                        content = await DownloadDefinitionAsync(client, entry, cancellationToken)
                            .ConfigureAwait(false);
                        downloaded++;
                    }

                    ValidateDefinition(entry, content, target);
                    await File.WriteAllBytesAsync(target, content, cancellationToken)
                        .ConfigureAwait(false);
                    hashes[entry.Id] = entry.Sha256;
                    logger.LogInformation("Validated definition: {IndexerId}", entry.Id);
                }

                var nextState = new LocalCatalogState(manifest.CatalogVersion, hashes);
                await File.WriteAllTextAsync(
                        Path.Combine(staging, StateFileName),
                        JsonSerializer.Serialize(
                            nextState,
                            new JsonSerializerOptions { WriteIndented = true }
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                AtomicReplace(activeDirectory, staging);
                logger.LogInformation(
                    "Indexer update completed: {DefinitionCount} definitions",
                    hashes.Count
                );
                return new(
                    true,
                    true,
                    $"Updated {hashes.Count} indexer definition(s).",
                    manifest.CatalogVersion,
                    downloaded,
                    reused
                );
            }
            catch
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
                throw;
            }
        }
        catch (Exception error) when (
            error is HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or CryptographicException
                or JsonException
                or InvalidDataException
                or FormatException
        )
        {
            logger.LogError(error, "Indexer update failed — retained previous catalog");
            return new(
                false,
                false,
                $"Indexer update failed; retained last-known-good definitions: {error.Message}"
            );
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private void VerifySignature(HttpResponseMessage response, byte[] payload)
    {
        if (
            !response.Headers.TryGetValues("X-Nebula-Signature-Algorithm", out var algorithms)
            || algorithms.SingleOrDefault() != "ecdsa-p256-sha256"
        )
        {
            throw new CryptographicException("Catalog signature algorithm is missing or unsupported.");
        }
        if (
            !response.Headers.TryGetValues("X-Nebula-Signature", out var signatures)
            || signatures.SingleOrDefault() is not { } signatureText
        )
        {
            throw new CryptographicException("Catalog signature is missing.");
        }
        var publicKey = Convert.FromBase64String(settings.PublicKeyBase64);
        var signature = Convert.FromBase64String(signatureText);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
        if (
            consumed != publicKey.Length
            || !verifier.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence
            )
        )
        {
            throw new CryptographicException("Catalog manifest signature is invalid.");
        }
    }

    private void ValidateManifest(IndexerCatalogManifest manifest)
    {
        if (manifest.ApiVersion != 1)
            throw new InvalidDataException($"Unsupported catalog API version {manifest.ApiVersion}.");
        if (manifest.CardigannSchema != IndexerDefinition.SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported Cardigann schema v{manifest.CardigannSchema}.");
        if (
            !Version.TryParse(manifest.MinimumClientVersion, out var minimum)
            || settings.ClientVersion < minimum
        )
            throw new InvalidDataException(
                $"Catalog requires Nebula Bridge {manifest.MinimumClientVersion} or newer."
            );
        if (!Sha256Pattern.IsMatch(manifest.CatalogVersion))
            throw new InvalidDataException("Catalog version is invalid.");
        if (manifest.Indexers.Count > MaximumDefinitions)
            throw new InvalidDataException("Catalog exceeds the indexer definition limit.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Indexers)
        {
            if (!SafeId.IsMatch(entry.Id) || !ids.Add(entry.Id))
                throw new InvalidDataException($"Catalog contains an invalid or duplicate ID: {entry.Id}");
            if (
                entry.DefinitionVersion != IndexerDefinition.SupportedSchemaVersion
                || !Sha256Pattern.IsMatch(entry.Sha256)
                || entry.SizeBytes is < 1 or > MaximumDefinitionBytes
            )
                throw new InvalidDataException($"Catalog metadata is invalid for {entry.Id}.");
            var definitionUri = new Uri(settings.ManifestUri, entry.DefinitionUrl);
            if (
                definitionUri.Scheme != settings.ManifestUri.Scheme
                || definitionUri.Host != settings.ManifestUri.Host
                || definitionUri.Port != settings.ManifestUri.Port
            )
                throw new InvalidDataException($"Definition URL leaves the catalog origin: {entry.Id}");
        }
    }

    private static void ValidateManifestUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            throw new InvalidDataException("Catalog manifest URL must be absolute.");
        var loopback = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback))
            throw new InvalidDataException("Catalog transport must use HTTPS (except loopback development). ");
    }

    private async Task<byte[]> DownloadDefinitionAsync(
        HttpClient client,
        IndexerCatalogEntry entry,
        CancellationToken cancellationToken
    )
    {
        var uri = new Uri(settings.ManifestUri, entry.DefinitionUrl);
        using var response = await client
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await ReadLimitedAsync(
                response.Content,
                Math.Min(entry.SizeBytes + 1, MaximumDefinitionBytes),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (content.Length != entry.SizeBytes || !HashMatches(content, entry.Sha256))
            throw new InvalidDataException($"Definition hash or size mismatch: {entry.Id}");
        logger.LogInformation("Downloaded definition: {IndexerId}", entry.Id);
        return content;
    }

    private void ValidateDefinition(IndexerCatalogEntry entry, byte[] content, string path)
    {
        var yaml = StrictUtf8.GetString(content);
        var record = parser.Parse(yaml, path, entry.DefinitionVersion);
        if (!record.Loaded || !record.Compatible || record.Definition?.Id != entry.Id)
            throw new InvalidDataException(
                $"Downloaded definition {entry.Id} is invalid or unsupported: {record.Error}"
            );
    }

    private static async Task<LocalCatalogState?> ReadStateAsync(
        string directory,
        CancellationToken cancellationToken
    )
    {
        var path = Path.Combine(directory, StateFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<LocalCatalogState>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (content.Headers.ContentLength > limit)
            throw new InvalidDataException($"Catalog response exceeds {limit} bytes.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > limit)
                throw new InvalidDataException($"Catalog response exceeds {limit} bytes.");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static bool HashMatches(byte[] content, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(content),
            Convert.FromHexString(expected)
        );

    private static void AtomicReplace(string active, string staging)
    {
        var parent = Directory.GetParent(active)?.FullName
            ?? throw new InvalidDataException("Indexer definition directory has no parent.");
        var backup = Path.Combine(parent, $".indexers.backup.{Guid.NewGuid():N}");
        var hadActive = Directory.Exists(active);
        if (hadActive)
            Directory.Move(active, backup);
        try
        {
            Directory.Move(staging, active);
        }
        catch
        {
            if (hadActive && Directory.Exists(backup))
                Directory.Move(backup, active);
            throw;
        }
        if (Directory.Exists(backup))
            Directory.Delete(backup, recursive: true);
    }
}

public sealed class IndexerUpdateCoordinator(
    IndexerCatalogUpdater updater,
    IndexerDefinitionLoader loader,
    ILogger<IndexerUpdateCoordinator> logger
)
{
    public async Task<IndexerCatalogUpdateResult> UpdateAsync(CancellationToken cancellationToken)
    {
        var result = await updater.UpdateAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return result;
        var refresh = await loader.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refresh.Success)
        {
            logger.LogError("Catalog installed but definition reload failed: {Message}", refresh.Message);
            return result with { Success = false, Message = refresh.Message };
        }
        return result;
    }
}
