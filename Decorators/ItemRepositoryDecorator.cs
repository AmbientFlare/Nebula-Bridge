#nullable disable
#pragma warning disable CS1591

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace NebulaBridge.Decorators;

#if JELLYFIN_12
// Jellyfin 12 split IItemRepository in two: the read/query half kept the name,
// and the write half moved to IItemPersistenceService. Nebula only ever
// reshaped the query half, but its callers still write through this type, so
// the decorator takes the persistence service as well and forwards the writes.
public sealed class NebulaBridgeItemRepository(
    IItemRepository inner,
    IItemPersistenceService persistence,
    IHttpContextAccessor http
) : IItemRepository
#else
public sealed class NebulaBridgeItemRepository(IItemRepository inner, IHttpContextAccessor http)
    : IItemRepository
#endif
{
#if JELLYFIN_12
    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken) =>
        persistence.SaveItems(items, cancellationToken);
#endif

    private static readonly BaseItemKind[] ListScopeMediaKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Episode,
    ];

    private static readonly BaseItemKind[] PremiereFilterMediaKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.Episode,
    ];

    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));

#if !JELLYFIN_12
    // Jellyfin 12 split the write/aggregate half of IItemRepository out into
    // IItemPersistenceService, IItemCountService, INextUpService and
    // ILinkedChildrenService. Nebula never reshaped these -- they were plain
    // pass-throughs -- so on the 12 line the decorator simply stops carrying
    // them and the host services are used undecorated.
    public void DeleteItem(params IReadOnlyList<Guid> ids) => inner.DeleteItem(ids);

    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken) =>
        inner.SaveItems(items, cancellationToken);

    public void SaveImages(BaseItem item) => inner.SaveImages(item);
#endif

    public BaseItem RetrieveItem(Guid id) => inner.RetrieveItem(id);

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        return inner.GetItems(ApplyFilters(filter));
    }

    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) =>
        inner.GetItemIdsList(ApplyFilters(filter));

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter)
    {
        return inner.GetItemList(ApplyFilters(filter));
    }

    /// <summary>
    /// Jellyfin enumerates a series' episodes by SeriesPresentationUniqueKey for its own
    /// bookkeeping, with or without a request: SeriesMetadataService.RemoveObsoleteEpisodes
    /// deletes every virtual episode that shares an index number with a real one on each
    /// library scan, and stream rows are virtual by design. They are not episodes of the
    /// series in any of those walks, so keep them out regardless of the calling context.
    /// </summary>
    internal static void ExcludeStreamRowsFromSeriesWalk(InternalItemsQuery filter)
    {
        if (
            string.IsNullOrEmpty(filter.SeriesPresentationUniqueKey)
            || filter.Tags.Contains(NebulaBridgeManager.StreamTag, StringComparer.OrdinalIgnoreCase)
            || filter.ExcludeTags.Contains(NebulaBridgeManager.StreamTag, StringComparer.OrdinalIgnoreCase)
        )
        {
            return;
        }

        filter.ExcludeTags = [.. filter.ExcludeTags, NebulaBridgeManager.StreamTag];
    }

    private InternalItemsQuery ApplyFilters(InternalItemsQuery filter)
    {
        var includeTypes = filter.IncludeItemTypes;
        var includesPerson = includeTypes.Contains(BaseItemKind.Person);
        // Internal NebulaBridge/library lookups should never be reshaped by listing filters.
        // Path-based queries are commonly used to resolve configured root folders.
        if (filter.IsDeadPerson == true || !string.IsNullOrWhiteSpace(filter.Path))
        {
            if (!includesPerson)
            {
                filter.IsDeadPerson = null;
            }
            return filter;
        }

        ExcludeStreamRowsFromSeriesWalk(filter);

        var ctx = _http.HttpContext;
        var isListingIntent =
            ctx is not null && (ctx.IsApiListing() || ctx.IsHomeScreenSectionListing());
        if (!isListingIntent)
            return filter;

        var filterUnreleased = NebulaBridgePlugin.Instance!.Configuration.FilterUnreleased;
        var bufferDays = NebulaBridgePlugin.Instance.Configuration.FilterUnreleasedBufferDays;
        var hasIncludeTypes = includeTypes.Length != 0;
        var isStreamTagQuery = filter.Tags.Any(tag =>
            tag.Equals(NebulaBridgeManager.StreamTag, StringComparison.OrdinalIgnoreCase)
        );
        var isTargetedLookup =
            filter.ItemIds.Length > 0 || (ctx is not null && ctx.IsSingleItemList());

        // Targeted ItemIds lookups are generally internal existence/permission checks.
        // Keep those untouched so the caller gets strict results from the underlying query.
        if (isTargetedLookup)
            return filter;

        if (!includesPerson)
            filter.IsDeadPerson = null;

        // Query-shape based media list detection: empty IncludeItemTypes is broad-list scope,
        // otherwise only media kinds we manage are considered for stream-row exclusion.
        var isMediaListQuery =
            !hasIncludeTypes || includeTypes.Intersect(ListScopeMediaKinds).Any();
        if (!isMediaListQuery)
            return filter;

        // Do not override queries that explicitly target stream-tagged rows.
        if (!isStreamTagQuery && filter.ExcludeTags.Length == 0)
            filter.ExcludeTags = [NebulaBridgeManager.StreamTag];

        if (filter.MaxPremiereDate is not null || !filterUnreleased)
            return filter;

        var isPremiereFilteredQuery =
            !hasIncludeTypes || includeTypes.Intersect(PremiereFilterMediaKinds).Any();
        if (!isPremiereFilteredQuery)
            return filter;

        // All media types use EndDate (digital for movies, premiere for series/episodes).
        // sentinel 9999 = no release date known → excluded by MaxEndDate <= today + bufferDays.
        if (filter.MaxEndDate is null)
            filter.MaxEndDate = DateTime.Today.AddDays(bufferDays);

        return filter;
    }

    public IReadOnlyList<BaseItem> GetLatestItemList(
        InternalItemsQuery filter,
        CollectionType collectionType
    ) => inner.GetLatestItemList(filter, collectionType);

#if !JELLYFIN_12
    public IReadOnlyList<string> GetNextUpSeriesKeys(
        InternalItemsQuery filter,
        DateTime dateCutoff
    ) => inner.GetNextUpSeriesKeys(filter, dateCutoff);

    public void UpdateInheritedValues() => inner.UpdateInheritedValues();

    public int GetCount(InternalItemsQuery filter) => inner.GetCount(filter);

    public ItemCounts GetItemCounts(InternalItemsQuery filter) => inner.GetItemCounts(filter);
#endif

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(
        InternalItemsQuery filter
    ) => inner.GetGenres(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(
        InternalItemsQuery filter
    ) => inner.GetMusicGenres(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(
        InternalItemsQuery filter
    ) => inner.GetStudios(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(
        InternalItemsQuery filter
    ) => inner.GetArtists(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(
        InternalItemsQuery filter
    ) => inner.GetAlbumArtists(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(
        InternalItemsQuery filter
    ) => inner.GetAllArtists(filter);

#if JELLYFIN_12
    // Both new in Jellyfin 12. Forwarded raw, matching the other aggregate
    // queries here (GetCount/GetItemCounts/GetGenres) -- ApplyFilters is only
    // for the item-listing paths that can surface Nebula stubs.
    public IReadOnlyList<string> GetMediaStreamLanguages(
        InternalItemsQuery filter,
        MediaStreamType mediaStreamType
    ) => inner.GetMediaStreamLanguages(filter, mediaStreamType);

    public QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery filter) =>
        inner.GetQueryFiltersLegacy(filter);
#endif

    public IReadOnlyList<string> GetMusicGenreNames() => inner.GetMusicGenreNames();

    public IReadOnlyList<string> GetStudioNames() => inner.GetStudioNames();

    public IReadOnlyList<string> GetGenreNames() => inner.GetGenreNames();

    public IReadOnlyList<string> GetAllArtistNames() => inner.GetAllArtistNames();

    public Task<bool> ItemExistsAsync(Guid id) => inner.ItemExistsAsync(id);

    public bool GetIsPlayed(User user, Guid id, bool recursive) =>
        inner.GetIsPlayed(user, id, recursive);

#if !JELLYFIN_12
    public IReadOnlyDictionary<string, MusicArtist[]> FindArtists(
        IReadOnlyList<string> artistNames
    ) => inner.FindArtists(artistNames);

    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken) =>
        inner.ReattachUserDataAsync(item, cancellationToken);
#endif
}
