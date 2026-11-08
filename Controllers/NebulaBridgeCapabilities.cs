using NebulaBridge.Services;
using System.Text.Json.Serialization;

namespace NebulaBridge.Controllers;

public sealed record NebulaBridgeCapabilities(
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("features")] NebulaBridgeFeatures Features,
    [property: JsonPropertyName("supportedVersions")] IReadOnlyList<int>? SupportedVersions = null,
    [property: JsonPropertyName("availability")] NebulaBridgeAvailability? Availability = null
);

public sealed record NebulaBridgeAvailability(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("hierarchyPrefetchAllowed")] bool HierarchyPrefetchAllowed
);

public sealed record NebulaBridgeFeatures(
    [property: JsonPropertyName("hierarchyPrefetch")] bool HierarchyPrefetch,
    [property: JsonPropertyName("seriesHydration")] bool SeriesHydration,
    [property: JsonPropertyName("seasonHydration")] bool SeasonHydration,
    [property: JsonPropertyName("playbackPrefetch")] bool PlaybackPrefetch,
    [property: JsonPropertyName("localAcquisition")] bool LocalAcquisition = false,
    [property: JsonPropertyName("retentionStatus")] bool RetentionStatus = false
);

public sealed record AcquisitionRetentionStatus(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("hasTemporaryCache")] bool HasTemporaryCache,
    [property: JsonPropertyName("jobId")] Guid? JobId,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("retention")] string? Retention,
    [property: JsonPropertyName("cachedBytes")] long CachedBytes,
    [property: JsonPropertyName("expectedBytes")] long? ExpectedBytes,
    [property: JsonPropertyName("expiresUtc")] DateTimeOffset? ExpiresUtc,
    [property: JsonPropertyName("imported")] bool Imported,
    [property: JsonPropertyName("canKeepItem")] bool CanKeepItem,
    [property: JsonPropertyName("canKeepSeries")] bool CanKeepSeries,
    [property: JsonPropertyName("seriesKeepPolicy")] bool SeriesKeepPolicy
);

public sealed record HierarchyHydrationResponse(
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("seasonCount")] int SeasonCount,
    [property: JsonPropertyName("episodeCount")] int EpisodeCount,
    [property: JsonPropertyName("error")] string? Error
)
{
    public static HierarchyHydrationResponse From(HierarchyHydrationResult result) =>
        new(
            result.ItemId,
            result.State.ToString(),
            result.SeasonCount,
            result.EpisodeCount,
            result.Error
        );
}
