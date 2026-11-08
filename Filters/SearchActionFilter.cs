using NebulaBridge.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using NebulaBridge.NativeSources;
using NebulaBridge.Services;

namespace NebulaBridge.Filters;

public class SearchActionFilter(
    IDtoService dtoService,
    ILibraryManager libraryManager,
    IUserManager userManager,
    NebulaBridgeManager manager,
    NebulaBridgeMetadataService metadata,
    NativeSourcePipeline nativePipeline,
    ILogger<SearchActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>
    /// Prefixing a search with this runs the query straight through the
    /// indexers instead of the metadata provider and returns whatever is
    /// already cached, for content the metadata databases do not carry.
    /// Mirrors the existing "local:" prefix.
    /// </summary>
    public const string RawSearchPrefix = "raw:";

    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // This filter can replace the Items controller response. Its policy must therefore
        // come from the authenticated Jellyfin principal, never from a caller-controlled
        // userId argument or query parameter.
        if (!ctx.HttpContext.TryGetAuthenticatedUserId(out var userId))
        {
            await next();
            return;
        }
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        if (
            cfg.DisableSearch
            || !ctx.IsApiSearchAction()
            || !ctx.TryGetActionArgument<string>("searchTerm", out var searchTerm)
        )
        {
            await next();
            return;
        }

        // Strip "local:" prefix if present and pass through to default handler
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm[6..].Trim();
            await next();
            return;
        }

        // Raw indexer search. Deliberately fails closed: anything unexpected
        // here falls through to the ordinary search rather than erroring, so a
        // problem in this path cannot take out normal searching.
        if (searchTerm.StartsWith(RawSearchPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rawQuery = searchTerm[RawSearchPrefix.Length..].Trim();
            if (rawQuery.Length == 0)
            {
                await next();
                return;
            }

            try
            {
                var rawResult = await BuildRawResultAsync(
                    rawQuery,
                    ctx,
                    ctx.HttpContext.RequestAborted
                );
                if (rawResult is not null)
                {
                    ctx.Result = rawResult;
                    return;
                }
            }
            catch (OperationCanceledException) when (ctx.HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Raw indexer search failed for \"{Query}\"", rawQuery);
            }

            ctx.ActionArguments["searchTerm"] = rawQuery;
            await next();
            return;
        }

        // Handle Stremio search
        var requestedTypes = GetRequestedItemTypes(ctx);
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        // A genre, watched or favourite filter cannot be applied to results
        // that are not on the server, so a narrowed search stays local.
        if (HasNarrowingFilter(ctx))
        {
            await next();
            return;
        }

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);
        ctx.TryGetActionArgument("parentId", out var parentId, Guid.Empty);
        var limited = ctx.ActionArguments.TryGetValue("limit", out var rawLimit) && rawLimit is not null;
        start = Math.Max(0, start);

        // A title can sit in a real library and in Nebula's own catalogue
        // libraries at once. Only one copy is shown, so Jellyfin is asked for
        // everything up to the end of this page plus the copies that will be
        // dropped, and the page is cut here instead.
        var (owned, hidden) = LocalMatches(userId, searchTerm, requestedTypes, parentId);
        if (hidden.Count > 0)
        {
            ctx.ActionArguments["startIndex"] = 0;
            if (limited)
            {
                ctx.ActionArguments["limit"] = start + limit + hidden.Count;
            }
        }

        // The server's own matches come first, so a client that only asked
        // for what is in a library still gets that, and the provider's
        // results follow them. Jellyfin pages the local part itself; the
        // provider search runs alongside it.
        var metasTask = SearchMetasAsync(
            searchTerm,
            requestedTypes,
            cfg,
            ctx.HttpContext.RequestAborted
        );

        var executed = await next();
        if (executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> local })
        {
            return;
        }

        List<StremioMeta> metas;
        try
        {
            metas = await metasTask;
        }
        catch (OperationCanceledException) when (ctx.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The local results are already good; a provider outage must not
            // take them down with it.
            log.LogError(ex, "Provider search failed for \"{Query}\"", searchTerm);
            return;
        }

        var localItems = local.Items;
        var localTotal = local.TotalRecordCount;
        if (hidden.Count > 0)
        {
            localItems = CutLocalPage(localItems, hidden, start, limited ? limit : 0);
            localTotal = Math.Max(0, localTotal - hidden.Count);
        }

        var extras = ConvertMetasToDtos(metas, item => IsOwned(item, owned));

        var page = MergePage(localItems, localTotal, extras, start, limited ? limit : 0);
        local.Items = page.Items;
        local.TotalRecordCount = page.TotalCount;

        log.LogInformation(
            "Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} local={Local} extras={Extras}",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            page.TotalCount - extras.Count,
            extras.Count
        );
    }

    /// <summary>
    /// Arguments that narrow a search by something only the server's own
    /// items carry. Any of them set means the provider has nothing to add.
    /// </summary>
    private static readonly string[] NarrowingArguments =
    [
        "genreIds",
        "genres",
        "isFavorite",
        "isPlayed",
        "years",
        "officialRatings",
        "tags",
        "studioIds",
        "studios",
        "personIds",
        "person",
        "minCommunityRating",
        "minCriticRating",
    ];

    private static readonly HashSet<ItemFilter> NarrowingItemFilters =
    [
        ItemFilter.IsFavorite,
        ItemFilter.IsFavoriteOrLikes,
        ItemFilter.IsPlayed,
        ItemFilter.IsUnplayed,
        ItemFilter.IsResumable,
        ItemFilter.Likes,
        ItemFilter.Dislikes,
    ];

    internal static bool HasNarrowingFilter(ActionExecutingContext ctx)
    {
        foreach (var key in NarrowingArguments)
        {
            if (ctx.ActionArguments.TryGetValue(key, out var value) && IsSet(value))
            {
                return true;
            }
        }

        return ctx.TryGetActionArgument<ItemFilter[]>("filters", out var filters)
            && filters.Any(NarrowingItemFilters.Contains);
    }

    private static bool IsSet(object? value) =>
        value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            Array array => array.Length > 0,
            _ => true,
        };

    /// <summary>
    /// Lays the provider's results after the server's. <paramref name="localPage"/>
    /// is the page Jellyfin already cut from its <paramref name="localTotal"/>
    /// matches; the provider results fill whatever room is left, continuing
    /// from where earlier pages stopped.
    /// </summary>
    internal static (BaseItemDto[] Items, int TotalCount) MergePage(
        IReadOnlyList<BaseItemDto> localPage,
        int localTotal,
        IReadOnlyList<BaseItemDto> extras,
        int start,
        int limit
    )
    {
        start = Math.Max(0, start);
        if (localPage.Count > 0)
        {
            // A client that turned the total off gets 0 back; the page itself
            // still proves at least this many local matches.
            localTotal = Math.Max(localTotal, start + localPage.Count);
        }
        var room = limit > 0 ? Math.Max(0, limit - localPage.Count) : int.MaxValue;
        var extrasStart = Math.Max(0, start - localTotal);
        var items = localPage.Concat(extras.Skip(extrasStart).Take(room)).ToArray();
        return (items, localTotal + extras.Count);
    }

    /// <summary>
    /// Everything on the server that matches the search in the same scope:
    /// the IMDb and TMDb ids, so a title the user already has is not shown
    /// again as a provider result, and the extra copies of titles that are
    /// on the server more than once.
    /// </summary>
    private (HashSet<string> Owned, HashSet<Guid> Hidden) LocalMatches(
        Guid userId,
        string searchTerm,
        HashSet<BaseItemKind> requestedTypes,
        Guid parentId
    )
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (userManager.GetUserById(userId) is not { } user)
        {
            return (keys, []);
        }

        var query = new InternalItemsQuery(user)
        {
            SearchTerm = searchTerm,
            IncludeItemTypes = [.. requestedTypes],
            Recursive = true,
        };
        if (parentId != Guid.Empty)
        {
            query.ParentId = parentId;
        }

        var items = libraryManager.GetItemList(query);
        foreach (var item in items)
        {
            AddKeys(keys, item);
        }

        return (keys, DuplicateCopies(items));
    }

    /// <summary>
    /// The copies to leave out when one title matches more than once. The
    /// user's own file wins over Nebula's catalogue copies, and Nebula's
    /// movie and show libraries win over its other catalogues.
    /// </summary>
    internal static HashSet<Guid> DuplicateCopies(IEnumerable<BaseItem> items)
    {
        var hidden = new HashSet<Guid>();
        foreach (var group in items.GroupBy(TitleKey).Where(group => group.Key is not null))
        {
            foreach (var copy in group.OrderBy(CopyRank).ThenBy(item => item.Id).Skip(1))
            {
                hidden.Add(copy.Id);
            }
        }

        return hidden;
    }

    private static string? TitleKey(BaseItem item)
    {
        foreach (var provider in new[] { MetadataProvider.Imdb, MetadataProvider.Tmdb })
        {
            var id = item.GetProviderId(provider);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return $"{provider}:{item.GetBaseItemKind()}:{id.Trim()}";
            }
        }

        return null;
    }

    private static int CopyRank(BaseItem item)
    {
        var path = item.Path ?? string.Empty;
        if (!path.StartsWith(LibraryRepairService.IdentityPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var rest = path[LibraryRepairService.IdentityPrefix.Length..];
        return rest.StartsWith("movies/", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("shows/", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 2;
    }

    /// <summary>
    /// Cuts one page from local results that start at the first match, after
    /// dropping the hidden copies. <paramref name="limit"/> of 0 means no limit.
    /// </summary>
    internal static BaseItemDto[] CutLocalPage(
        IReadOnlyList<BaseItemDto> fromFirst,
        HashSet<Guid> hidden,
        int start,
        int limit
    )
    {
        var kept = fromFirst.Where(item => !hidden.Contains(item.Id)).Skip(start);
        return (limit > 0 ? kept.Take(limit) : kept).ToArray();
    }

    internal static bool IsOwned(BaseItem item, HashSet<string> owned)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddKeys(keys, item);
        return keys.Overlaps(owned);
    }

    private static void AddKeys(HashSet<string> keys, BaseItem item)
    {
        foreach (var provider in new[] { MetadataProvider.Imdb, MetadataProvider.Tmdb })
        {
            var id = item.GetProviderId(provider);
            if (!string.IsNullOrWhiteSpace(id))
            {
                // Movie and series TMDb ids overlap, so the kind is part of the key.
                keys.Add($"{provider}:{item.GetBaseItemKind()}:{id.Trim()}");
            }
        }
    }

    /// <summary>
    /// Runs a query through the indexers and returns whatever the debrid
    /// provider already has cached, newest first. Returns null when there is
    /// nothing to show, so the caller can fall through to ordinary search.
    /// </summary>
    private async Task<IActionResult?> BuildRawResultAsync(
        string rawQuery,
        ActionExecutingContext ctx,
        CancellationToken cancellationToken
    )
    {
        // Clients fire this search several times in parallel (once per item
        // type) and again on every reload. Each miss sweeps every indexer, so
        // repeat queries are served from the last result instead.
        var cached = manager.GetRawSearch(rawQuery);
        if (cached is null)
        {
            // SearchAsync already batch-checks debrid availability and drops
            // anything not cached, so everything returned here is playable now.
            // A raw: search is unbounded on purpose: the person typing it wants
            // every indexer's answer, not whichever ones were quick, and the
            // result is cached so the wait is paid once per query.
            var search = await nativePipeline
                .SearchAsync(
                    new NativeMediaQuery(rawQuery),
                    null,
                    cancellationToken,
                    IndexerSearchBudget.Unbounded
                )
                .ConfigureAwait(false);

            // Seeders are irrelevant when every result is already cached, so
            // the useful ordering is simply newest first.
            cached = search
                .Candidates.OrderByDescending(candidate =>
                    candidate.PublishedAt ?? DateTimeOffset.MinValue
                )
                .ToList();
            manager.SaveRawSearch(rawQuery, cached);
        }

        var candidates = cached;

        log.LogInformation(
            "Raw indexer search \"{Query}\" returned {Count} cached candidate(s)",
            rawQuery,
            candidates.Count
        );

        if (candidates.Count == 0)
        {
            return null;
        }

        var options = new DtoOptions { EnableImages = false, EnableUserData = false };
        var dtos = new List<BaseItemDto>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var meta = manager.IntoRawMeta(rawQuery, candidate);
            if (meta is null)
            {
                continue;
            }

            var baseItem = manager.IntoBaseItem(meta);
            if (baseItem is null)
            {
                continue;
            }

            BaseItemDto dto;
            try
            {
                dto = dtoService.GetBaseItemDto(baseItem, options);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Skipping raw result {Name}: DTO conversion failed", meta.Name);
                continue;
            }

            var stremioUri = StremioUri.FromBaseItem(baseItem);
            if (stremioUri is null)
            {
                continue;
            }

            dto.Id = stremioUri.ToGuid();
            dtos.Add(dto);
            manager.SaveStremioMeta(dto.Id, meta);
            // Pin the release so playback resolves this exact file rather than
            // searching every indexer again. Keyed by the Stremio id, which is
            // what the materialised item carries.
            manager.SaveRawCandidate(stremioUri.ExternalId, candidate);
        }

        if (dtos.Count == 0)
        {
            return null;
        }

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);
        var paged = dtos.Skip(start).Take(limit).ToArray();

        return new OkObjectResult(
            new QueryResult<BaseItemDto> { Items = paged, TotalRecordCount = dtos.Count }
        );
    }

    private HashSet<BaseItemKind> GetRequestedItemTypes(ActionExecutingContext ctx)
    {
        var requested = new HashSet<BaseItemKind>([BaseItemKind.Movie, BaseItemKind.Series]);

        // Already parsed as BaseItemKind[] by model binder
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes is { Length: > 0 }
        )
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            // Only keep Movie and Series
            requested.IntersectWith([BaseItemKind.Movie, BaseItemKind.Series]);
        }

        // Remove excluded types
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes is { Length: > 0 }
        )
        {
            requested.ExceptWith(excludeTypes);
        }

        // If mediaTypes=Video, exclude Series
        if (
            ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
            && mediaTypes.Contains(MediaType.Video)
        )
        {
            requested.Remove(BaseItemKind.Series);
        }

        return requested;
    }

    private async Task<List<StremioMeta>> SearchMetasAsync(
        string searchTerm,
        HashSet<BaseItemKind> requestedTypes,
        PluginConfiguration cfg,
        CancellationToken cancellationToken
    )
    {
        var tasks = new List<Task<IReadOnlyList<StremioMeta>>>();
        if (requestedTypes.Contains(BaseItemKind.Movie))
        {
            tasks.Add(
                metadata.SearchAsync(
                    cfg,
                    searchTerm,
                    StremioMediaType.Movie,
                    cancellationToken
                )
            );
        }
        if (requestedTypes.Contains(BaseItemKind.Series))
        {
            tasks.Add(
                metadata.SearchAsync(
                    cfg,
                    searchTerm,
                    StremioMediaType.Series,
                    cancellationToken
                )
            );
        }
        var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();

        var filterUnreleased = cfg.FilterUnreleased;
        var bufferDays = cfg.FilterUnreleasedBufferDays;

        if (filterUnreleased)
        {
            results = results.Where(x => x.IsReleased(bufferDays)).ToList();
        }

        return results;
    }

    private List<BaseItemDto> ConvertMetasToDtos(
        List<StremioMeta> metas,
        Func<BaseItem, bool>? skip = null
    )
    {
        // theres a reason i initally disabled all fields but forgot....
        // infuse breaks if we do a small subset. Not sure which field it needs. Prolly mediasources
        var options = new DtoOptions { EnableImages = true, EnableUserData = false };

        var dtos = new List<BaseItemDto>(metas.Count);

        foreach (var meta in metas)
        {
            var baseItem = manager.IntoBaseItem(meta);
            if (baseItem is null || skip?.Invoke(baseItem) == true)
                continue;

            BaseItemDto dto;
            try
            {
                dto = dtoService.GetBaseItemDto(baseItem, options);
            }
            catch (Exception ex)
            {
                // Stock DtoService indexes MediaSources[0] unguarded; virtual stubs
                // can have none. Skip the item rather than fail the whole search.
                log.LogWarning(
                    ex,
                    "Skipping search result {Name}: DTO conversion failed",
                    meta.Name
                );
                continue;
            }

            var stremioUri = StremioUri.FromBaseItem(baseItem);
            if (stremioUri is null)
            {
                continue;
            }

            dto.Id = stremioUri.ToGuid();
            dtos.Add(dto);

            manager.SaveStremioMeta(dto.Id, meta);
        }

        return dtos;
    }
}
