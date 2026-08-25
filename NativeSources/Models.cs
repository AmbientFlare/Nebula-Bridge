using System.Text.Json.Nodes;

namespace NebulaBridge.NativeSources;

public sealed class IndexerDefinition
{
    public const int SupportedSchemaVersion = 11;

    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Language { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Encoding { get; init; } = "UTF-8";

    public double RequestDelaySeconds { get; init; }

    public IReadOnlyList<string> CertificateFingerprints { get; init; } = [];

    public required IReadOnlyList<string> Links { get; init; }

    public required JsonObject Document { get; init; }

    public required string SourcePath { get; init; }

    public int SchemaVersion { get; init; } = SupportedSchemaVersion;

    public string DefinitionHash { get; init; } = string.Empty;
}

public sealed record NativeMediaQuery(
    string Title,
    int? Year = null,
    int? Season = null,
    int? Episode = null,
    string? ImdbId = null,
    string? TmdbId = null,
    string? TvdbId = null
);

public sealed record NativeReleaseCandidate(
    string SourceId,
    string Title,
    Uri Link,
    string Kind = "unknown",
    string? InfoHash = null,
    long? SizeBytes = null,
    int? Seeders = null,
    DateTimeOffset? PublishedAt = null,
    string? Category = null,
    string SourceName = "",
    Uri? MagnetUrl = null,
    Uri? DownloadUrl = null,
    Uri? DetailsUrl = null,
    int? Leechers = null,
    int? Peers = null,
    string? Uploader = null,
    IReadOnlyList<NativeResultSource>? Sources = null,
    int DefinitionVersion = IndexerDefinition.SupportedSchemaVersion,
    string? DefinitionHash = null,
    IReadOnlyList<DebridAvailability>? Availability = null,
    bool Playable = false
);

public sealed record NativeResultSource(
    string IndexerId,
    string IndexerName,
    Uri Link,
    int DefinitionVersion = IndexerDefinition.SupportedSchemaVersion,
    string? DefinitionHash = null
);

public sealed record NativeSourceFailure(
    string SourceId,
    string Message,
    string SourceName = "",
    string Stage = "request",
    string Reason = "failed"
);

public sealed record NativeSearchResult(
    IReadOnlyList<NativeReleaseCandidate> Candidates,
    IReadOnlyList<NativeSourceFailure> Failures
);

public sealed record NativeResolvedStream(
    string SourceId,
    string Name,
    Uri Url,
    long? SizeBytes = null,
    string? Filename = null
);

public sealed record NativePreparedStream(
    string SourceId,
    string Name,
    long? SizeBytes,
    string? Filename,
    Uri? DirectUrl = null,
    DebridPlaybackRequest? DebridRequest = null
);

public sealed record NativeDefinitionSummary(
    string Id,
    string Name,
    string Description,
    string Source = "local",
    bool Enabled = false,
    bool Loaded = true,
    bool Compatible = true,
    string State = "disabled",
    string? Error = null,
    int DefinitionVersion = IndexerDefinition.SupportedSchemaVersion,
    string Language = "",
    string Type = "public"
);

public sealed record NativeSearchRequest(string? DefinitionId, NativeMediaQuery Query);

public sealed record NativeDefinitionValidationRequest(string Yaml);

public sealed record NativeDefinitionValidationResponse(
    bool Valid,
    IReadOnlyList<string> Errors,
    NativeDefinitionSummary? Definition
);

public sealed record IndexerRefreshResponse(
    bool Success,
    string Message,
    int DefinitionCount,
    DateTime? LastRefreshUtc,
    int LoadedCount = 0,
    int CompatibleCount = 0,
    int InvalidCount = 0,
    string? DefinitionsDirectory = null
);

public sealed record NativeIndexerEnabledRequest(bool Enabled);

internal sealed record IndexerDefinitionRecord(
    string Id,
    string Name,
    string Description,
    string SourcePath,
    IndexerDefinition? Definition,
    bool Loaded,
    bool Compatible,
    string? Error,
    int DefinitionVersion = IndexerDefinition.SupportedSchemaVersion
);

internal sealed record IndexerDefinitionSnapshot(
    IReadOnlyDictionary<string, IndexerDefinitionRecord> Records,
    DateTime RefreshedUtc
)
{
    public static IndexerDefinitionSnapshot Empty { get; } = new(
        new Dictionary<string, IndexerDefinitionRecord>(StringComparer.OrdinalIgnoreCase),
        DateTime.MinValue
    );
}
