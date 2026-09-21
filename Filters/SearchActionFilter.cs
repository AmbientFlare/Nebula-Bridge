using NebulaBridge.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using NebulaBridge.NativeSources;
using NebulaBridge.Services;

namespace NebulaBridge.Filters;

public class SearchActionFilter(
    IDtoService dtoService,
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

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        var metas = await SearchMetasAsync(
            searchTerm,
            requestedTypes,
            cfg,
            ctx.HttpContext.RequestAborted
        );

        log.LogInformation(
            "Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} results={Results}",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            metas.Count
        );

        var dtos = ConvertMetasToDtos(metas);
        var paged = dtos.Skip(start).Take(limit).ToArray();

        ctx.Result = new OkObjectResult(
            new QueryResult<BaseItemDto> { Items = paged, TotalRecordCount = dtos.Count }
        );
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

    private List<BaseItemDto> ConvertMetasToDtos(List<StremioMeta> metas)
    {
        // theres a reason i initally disabled all fields but forgot....
        // infuse breaks if we do a small subset. Not sure which field it needs. Prolly mediasources
        var options = new DtoOptions { EnableImages = true, EnableUserData = false };

        var dtos = new List<BaseItemDto>(metas.Count);

        foreach (var meta in metas)
        {
            var baseItem = manager.IntoBaseItem(meta);
            if (baseItem is null)
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
