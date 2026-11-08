using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NebulaBridge;

public sealed class StremioUri
{
    public StremioMediaType MediaType { get; }
    public string ExternalId { get; }
    private readonly string? _streamId;

    public StremioUri(StremioMediaType mediaType, string? externalId, string? streamId = null)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("externalId cannot be null or empty.", nameof(externalId));

        MediaType = mediaType;
        ExternalId = externalId;
        _streamId = string.IsNullOrWhiteSpace(streamId) ? null : streamId;
    }

    public static StremioUri? FromBaseItem(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var kind = item.GetBaseItemKind();
        var mediaType = kind switch
        {
            BaseItemKind.Movie => StremioMediaType.Movie,
            BaseItemKind.Series or BaseItemKind.Episode => StremioMediaType.Series,
            _ => throw new NotSupportedException($"Unsupported BaseItemKind: {kind}"),
        };

        var stremioId = item.GetProviderId("Stremio");
        StremioUri? uri = null;
        if (!string.IsNullOrWhiteSpace(stremioId))
            uri = new StremioUri(mediaType, stremioId);

        switch (kind)
        {
            case BaseItemKind.Movie:
                {
                    var imdb = item.GetProviderId(MetadataProvider.Imdb);
                    return string.IsNullOrWhiteSpace(imdb)
                        ? uri
                        : new StremioUri(StremioMediaType.Movie, imdb);
                }
            case BaseItemKind.Series:
                {
                    var imdb = item.GetProviderId(MetadataProvider.Imdb);
                    return string.IsNullOrWhiteSpace(imdb)
                        ? uri
                        : new StremioUri(StremioMediaType.Series, imdb);
                }
            case BaseItemKind.Episode:
                {
                    var ep = (Episode)item;
                    var seriesImdb = ep.Series?.GetProviderId(MetadataProvider.Imdb);
                    if (
                        string.IsNullOrWhiteSpace(seriesImdb)
                        || ep.ParentIndexNumber is null
                        || ep.IndexNumber is null
                    )
                        return uri;

                    var ext = $"{seriesImdb}:{ep.ParentIndexNumber}:{ep.IndexNumber}";
                    return new StremioUri(StremioMediaType.Series, ext);
                }
        }

        return null;
    }

    public override string ToString()
    {
        var type = MediaType == StremioMediaType.Movie ? "movie" : "series";
        return _streamId is null
            ? $"stremio://{type}/{ExternalId}"
            : $"stremio://{type}/{ExternalId}/{_streamId}";
    }

    public Guid ToGuid()
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(ToString()));
        return new Guid(hash);
    }
}

public static class Utils
{
    public static readonly long MeaningfulPlaybackTicks = TimeSpan.FromMinutes(5).Ticks;

    public static bool HasMeaningfulInteraction(this UserItemData? data) =>
        data is not null
        && (data.IsFavorite || data.Played || data.PlaybackPositionTicks >= MeaningfulPlaybackTicks);

    public static long? ParseToTicks(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim().ToLowerInvariant();

        // Try built-in parse (hh:mm:ss)
        if (TimeSpan.TryParse(input, out var ts))
            return ts.Ticks;

        // Try XML ISO8601 style (PT2H29M)
        try
        {
            ts = System.Xml.XmlConvert.ToTimeSpan(input);
            return ts.Ticks;
        }
        catch
        {
            // ignore
        }
        // Regex fallback for human formats like "2h29min"
        var h = Regex.Match(input, @"(\d+)\s*h");
        var m = Regex.Match(input, @"(\d+)\s*min");
        var s = Regex.Match(input, @"(\d+)\s*s(ec)?");

        var hours = h.Success ? int.Parse(h.Groups[1].Value) : 0;
        var mins = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        var secs = s.Success ? int.Parse(s.Groups[1].Value) : 0;

        // If plain number like "149" → minutes
        if (!h.Success && !m.Success && !s.Success && int.TryParse(input, out var onlyNum))
            mins = onlyNum;

        return new TimeSpan(hours, mins, secs).Ticks;
    }
}

public sealed class KeyLock
{
    private sealed class QueueEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, QueueEntry> _queues = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _inflight = new();

    internal int QueuedKeyCount => _queues.Count;

    public Task RunSingleFlightAsync(
        Guid key,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default
    )
    {
        var lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task>(
                () => Once(key, action, ct),
                LazyThreadSafetyMode.ExecutionAndPublication
            )
        );
        return lazy.Value;
    }

    public async Task RunQueuedAsync(
        Guid key,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(action);

        var entry = AcquireQueueEntry(key);
        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                entry.Semaphore.Release();
            }

            ReleaseQueueEntry(key, entry);
        }
    }

    private QueueEntry AcquireQueueEntry(Guid key)
    {
        while (true)
        {
            var entry = _queues.GetOrAdd(key, _ => new QueueEntry());
            lock (entry)
            {
                if (
                    _queues.TryGetValue(key, out var current)
                    && ReferenceEquals(entry, current)
                )
                {
                    entry.ReferenceCount++;
                    return entry;
                }
            }
        }
    }

    private void ReleaseQueueEntry(Guid key, QueueEntry entry)
    {
        lock (entry)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount != 0)
            {
                return;
            }

            var entries = (ICollection<KeyValuePair<Guid, QueueEntry>>)_queues;
            if (entries.Remove(new KeyValuePair<Guid, QueueEntry>(key, entry)))
            {
                entry.Semaphore.Dispose();
            }
        }
    }

    private async Task Once(Guid key, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }
}

public static class EnumMappingExtensions
{
    public static StremioMediaType ToStremio(this BaseItemKind kind)
    {
        return kind switch
        {
            BaseItemKind.Movie => StremioMediaType.Movie,
            BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.Episode =>
                StremioMediaType.Series,
            _ => StremioMediaType.Unknown,
        };
    }

    public static BaseItemKind ToBaseItem(this StremioMediaType type)
    {
        return type switch
        {
            StremioMediaType.Movie => BaseItemKind.Movie,
            StremioMediaType.Series => BaseItemKind.Series,
            _ => throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Unknown StremioMediaType"
            ),
        };
    }
}

public static class ActionContextExtensions
{
    private static readonly string[] RouteGuidKeys =
    [
        "id",
        "Id",
        "ID",
        "itemId",
        "ItemId",
        "ItemID",
        "seriesId",
        "SeriesId",
    ];

    private static readonly string[] IdsGuidKeys = ["ids", "Ids", "IDs"];

    private static readonly HashSet<string> SearchActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItemsByUserIdLegacy",
    };

    private static readonly HashSet<string> BaseItemListActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItemsByUserIdLegacy",
        "NextUp",
        "GetResumeItems",
        "GetResumeItemsLegacy",
        "GetNextUp",
        "GetEpisodes",
        "GetSeasons",
        "GetSimilarItems",
        "GetLatestMedia",
        "GetLatestMediaLegacy",
        "GetUpcomingEpisodes",
        "GetRecommendedItems",
        "GetMovieRecommendations",
        "GetSuggestionsLegacy",
        "GetSuggestions",
        "GetItemCounts",
        "GetSectionContent",
    };

    private static readonly HashSet<string> InsertableActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItem",
        "GetItemLegacy",
        "GetItemsByUserIdLegacy",
        "GetPlaybackInfo",
        "GetPostedPlaybackInfo",
        "GetSeasons",
        "GetEpisodes",
        "GetVideoStream",
        "GetDownload",
        "GetSubtitleWithTicks",
    };

    private static readonly HashSet<string> InsertableListActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetItems",
        "GetItemsByUserIdLegacy",
    };

    // Hierarchy listings hydrate metadata stubs; they must never trigger source
    // discovery. They reach the decorator once per child item, so allowing a
    // sync here fires a full indexer sweep for every episode in a season just to
    // draw the list -- minutes of work, and the stub identity is not settled yet,
    // so the matcher runs against S00E01 defaults and rejects every candidate.
    // Sources are resolved when playback is requested instead.
    private static readonly HashSet<string> SourceSyncExcludedActionNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "GetSeasons",
        "GetEpisodes",
    };

    public static string? GetActionName(this ActionExecutingContext ctx) =>
        (ctx.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

    public static string? GetActionName(this HttpContext ctx) =>
        ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ActionName;

    public static bool IsApiListing(this HttpContext ctx)
    {
        var actionName = ctx.GetActionName();
        return actionName != null && BaseItemListActionNames.Contains(actionName);
    }

    public static bool IsHomeScreenSectionListing(this HttpContext ctx)
    {
        var action = ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (
            action is not null
            && string.Equals(
                action.ControllerName,
                "HomeScreen",
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(
                action.ActionName,
                "GetSectionContent",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return true;
        }

        var path = ctx.Request.Path;
        return path.HasValue
            && path.Value?.Contains("/HomeScreen/Section/", StringComparison.OrdinalIgnoreCase)
                == true;
    }

    public static bool IsApiSearchAction(this ActionExecutingContext ctx) =>
        ctx.GetActionName() is { } actionName && SearchActionNames.Contains(actionName);

    public static bool IsInsertableAction(this HttpContext ctx)
    {
        var actionName = ctx.GetActionName();
        return actionName != null
            && InsertableActionNames.Contains(actionName)
            && (
                !InsertableListActionNames.Contains(actionName)
                || InsertableListActionNames.Contains(actionName) && IsSingleItemList(ctx)
            );
    }

    public static bool IsInsertableAction(this ActionExecutingContext ctx) =>
        ctx.HttpContext.IsInsertableAction();

    // Insertion and source discovery are gated differently on purpose.
    // Insertion is broad: browsing a series has to materialize its hierarchy.
    // Source discovery is narrow: it only runs when the user asks to play.
    public static bool IsSourceSyncAction(this HttpContext ctx)
    {
        var actionName = ctx.GetActionName();
        return actionName != null
            && !SourceSyncExcludedActionNames.Contains(actionName)
            && ctx.IsInsertableAction();
    }

    public static bool IsSingleItemList(this HttpContext ctx)
    {
        var q = ctx.Request.Query;
        if (!q.TryGetValue("ids", out var idsRaw))
            return false;

        var ids = idsRaw
            .ToString()
            .Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );

        return ids.Length == 1;
    }

    public static bool IsSingleItemList(this ActionExecutingContext ctx) =>
        ctx.HttpContext.IsSingleItemList();

    public static bool TryGetRouteGuid(this ActionExecutingContext ctx, out Guid value)
    {
        value = Guid.Empty;
        return ctx.TryGetRouteGuidString(out var s) && Guid.TryParse(s, out value);
    }

    private static bool TryGetRouteGuidString(this ActionExecutingContext ctx, out string value)
    {
        value = string.Empty;

        // Check if already resolved
        if (ctx.HttpContext.Items["GuidResolved"] is Guid g)
        {
            value = g.ToString("N");
            return true;
        }

        var rd = ctx.RouteData.Values;

        // Check route values
        foreach (var key in RouteGuidKeys)
        {
            if (
                rd.TryGetValue(key, out var raw)
                && raw?.ToString() is { } s
                && !string.IsNullOrWhiteSpace(s)
            )
            {
                value = s;
                return true;
            }
        }

        // Fallback: check query string "ids"
        var query = ctx.HttpContext.Request.Query;
        if (
            query.TryGetValue("ids", out var ids)
            && ids.Count == 1
            && !string.IsNullOrWhiteSpace(ids[0])
        )
        {
            value = ids[0]!;
            return true;
        }

        return false;
    }

    public static void ReplaceGuid(this ActionExecutingContext ctx, Guid value)
    {
        var rd = ctx.RouteData.Values;

        foreach (var key in RouteGuidKeys)
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                rd[key] = value.ToString();
                ctx.ActionArguments[key] = value;
            }
        }

        // Also replace ids query-parameter argument (used by list actions like GetItems)
        foreach (var key in IdsGuidKeys)
        {
            if (ctx.ActionArguments.ContainsKey(key))
            {
                ctx.ActionArguments[key] = new[] { value };
                break;
            }
        }

        ctx.HttpContext.Items["GuidResolved"] = value;
    }

    public static bool TryGetUserId(this ActionExecutingContext ctx, out Guid userId)
    {
        return ctx.HttpContext.TryGetUserId(out userId);
    }

    public static bool TryGetUserId(this HttpContext ctx, out Guid userId)
    {
        if (ctx.TryGetAuthenticatedUserId(out userId))
        {
            return true;
        }

        var queryUserId = ctx.Request.Query["userId"].FirstOrDefault();
        return Guid.TryParse(queryUserId, out userId) && userId != Guid.Empty;
    }

    /// <summary>
    /// Resolves identity only from Jellyfin's authenticated principal. Filters which can
    /// replace a host controller response must use this rather than a bindable request value.
    /// </summary>
    public static bool TryGetAuthenticatedUserId(this HttpContext ctx, out Guid userId)
    {
        userId = Guid.Empty;
        var claimUserId = ctx.User.Claims
            .FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")
            ?.Value;
        return Guid.TryParse(claimUserId, out userId) && userId != Guid.Empty;
    }

    public static bool TryGetActionArgument<T>(
        this ActionExecutingContext ctx,
        string key,
        out T value,
        T defaultValue = default!
    )
    {
        if (ctx.ActionArguments.TryGetValue(key, out var objValue) && objValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = defaultValue;
        return false;
    }
}

public static class BaseItemExtensions
{
    public static bool IsNebulaBridge(this BaseItem item)
    {
        return !string.IsNullOrWhiteSpace(item.GetProviderId("Stremio"));
    }

    public static bool HasStreamTag(this BaseItem item) =>
        item.Tags?.Contains(
            NebulaBridgeManager.StreamTag,
            StringComparer.OrdinalIgnoreCase
        ) == true;

    public static bool HasTreeSyncedTag(this BaseItem item) =>
        item.Tags?.Contains(
            NebulaBridgeManager.TreeSyncedTag,
            StringComparer.OrdinalIgnoreCase
        ) == true;

    public static bool HasSeasonsHydratedTag(this BaseItem item) =>
        item.Tags?.Contains(
            NebulaBridgeManager.SeasonsHydratedTag,
            StringComparer.OrdinalIgnoreCase
        ) == true;

    public static bool HasEpisodesHydratedTag(this BaseItem item) =>
        item.Tags?.Contains(
            NebulaBridgeManager.EpisodesHydratedTag,
            StringComparer.OrdinalIgnoreCase
        ) == true;

    /// <summary>
    /// True for an item created by a raw indexer search. Those are throwaway
    /// and get swept up on a timer; everything from a metadata provider is
    /// left alone.
    /// </summary>
    public static bool IsRawSearchResult(this BaseItem item) =>
        item.GetProviderId("Stremio")
            ?.StartsWith(NebulaBridgeManager.RawSearchIdPrefix, StringComparison.OrdinalIgnoreCase)
        == true;

    public static bool HasDiscoveryTag(this BaseItem item) =>
        item.Tags?.Contains(
            NebulaBridgeManager.DiscoveryTag,
            StringComparer.OrdinalIgnoreCase
        ) == true;

    /// <summary>
    /// Raw results are disposable only while they remain untouched discovery items. Promotion
    /// deliberately preserves their provider identity for playback, so the lifecycle marker is
    /// the authority for retention rather than the raw provider-id prefix alone.
    /// </summary>
    public static bool IsDisposableRawSearchResult(this BaseItem item) =>
        item.IsRawSearchResult()
        && item.HasDiscoveryTag()
        && item.Tags?.Contains(NebulaBridgeManager.PromotedTag, StringComparer.OrdinalIgnoreCase) != true;

    /// <summary>
    /// Jellyfin 12 retyped <see cref="Video.PrimaryVersionId"/> from a string to a
    /// nullable Guid. These two helpers are the only place that difference is
    /// visible; every caller works in terms of "has one" / "which id".
    /// </summary>
#if JELLYFIN_12
    public static bool HasPrimaryVersion(this Video? video) => video?.PrimaryVersionId is not null;

    public static Guid? GetPrimaryVersionId(this Video? video) => video?.PrimaryVersionId;
#else
    public static bool HasPrimaryVersion(this Video? video) =>
        !string.IsNullOrWhiteSpace(video?.PrimaryVersionId);

    public static Guid? GetPrimaryVersionId(this Video? video) =>
        Guid.TryParse(video?.PrimaryVersionId, out var id) ? id : null;
#endif

    /// <summary>
    /// Jellyfin 10.11's <c>LinkedChild.Create</c> links by Path whenever the item has
    /// one, and a Nebula stub's path is a URL, so those have to be linked by library
    /// item id by hand. Jellyfin 12 links every child by <c>ItemId</c>, which is
    /// already what Nebula wants, so <c>Create</c> is correct for both cases there.
    /// </summary>
    public static LinkedChild CreateLinkedChild(this BaseItem item)
    {
#if JELLYFIN_12
        return LinkedChild.Create(item);
#else
        return item.IsNebulaBridge()
            ? new LinkedChild
            {
                LibraryItemId = item.Id.ToString(
                    "N",
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                Type = LinkedChildType.Manual,
            }
            : LinkedChild.Create(item);
#endif
    }

    public static bool IsPrimaryVersion(this BaseItem item)
    {
        return !item.HasStreamTag() && !(item as Video).HasPrimaryVersion() && !item.IsVirtualItem;
    }

    public static bool IsStream(this BaseItem item)
    {
        return !string.IsNullOrWhiteSpace(item.GetProviderId("Stremio"))
            && !item.IsPrimaryVersion();
    }

    public static T? NebulaBridgeData<T>(this BaseItem item, string key)
    {
        if (string.IsNullOrEmpty(item.ExternalId))
            return default;

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.ExternalId);
            return dict != null && dict.TryGetValue(key, out var el)
                ? el.Deserialize<T>()
                : default;
        }
        catch
        {
            return default;
        }
    }

    public static void SetNebulaBridgeData<T>(this BaseItem item, string key, T value)
    {
        Dictionary<string, JsonElement> data;

        try
        {
            data = string.IsNullOrEmpty(item.ExternalId)
                ? new Dictionary<string, JsonElement>()
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.ExternalId)
                    ?? new Dictionary<string, JsonElement>();
        }
        catch
        {
            data = new Dictionary<string, JsonElement>();
        }

        data[key] = JsonSerializer.SerializeToElement(value);
        item.ExternalId = JsonSerializer.Serialize(data);
    }
}

public static class StringExtensions
{
    public static bool IsUrl(this string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
