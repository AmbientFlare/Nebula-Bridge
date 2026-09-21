using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.Filters;

public class InsertActionFilter(
    NebulaBridgeManager manager,
    IUserManager userManager,
    ILibraryManager libraryManager,
    NebulaBridgeMetadataService metadata,
    HierarchyHydrationService hierarchy,
    BridgeLibraryService bridgeLibraries,
    UserAccessService userAccess,
    DiscoveryPrefetchService prefetch,
    ILogger<InsertActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    private readonly KeyLock _lock = new();
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (!ctx.TryGetUserId(out var userId) || userManager.GetUserById(userId) is not { } user)
        {
            await next();
            return;
        }

        // This is the normal Jellyfin compatibility path. Hydration runs before Jellyfin's own
        // action reads the repository, so Web and other unmodified clients receive ordinary
        // Season/Episode DTOs from their standard endpoints.
        if (await TryHydrateHierarchyRequestAsync(ctx, userId).ConfigureAwait(false))
        {
            await next();
            return;
        }

        if (!ctx.IsInsertableAction() || !ctx.TryGetRouteGuid(out var guid))
        {
            await next();
            return;
        }

        if (manager.GetStremioMeta(guid) is not { } stremioMeta)
        {
            log.LogDebug(
                "InsertActionFilter: no pending discovery metadata for {ItemId} on {Action}",
                guid,
                ctx.GetActionName()
            );
            await next();
            return;
        }

        // Materialize remote discoveries inside dedicated bridge-owned libraries so native
        // Jellyfin per-user folder access can hide the quarantine completely.
        var isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = await bridgeLibraries
            .EnsureLibraryAsync(
                bridgeLibraries.GetDiscoveryDescriptor(isSeries),
                ctx.HttpContext.RequestAborted
            )
            .ConfigureAwait(false);
        if (root is null)
        {
            log.LogWarning(
                "The managed {Type} discovery library is not ready",
                isSeries ? "series" : "movie"
            );
            await next();
            return;
        }
        await userAccess
            .ReconcileAllAsync(ctx.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (manager.IntoBaseItem(stremioMeta) is { } item)
        {
            var existing = manager.FindExistingItem(item, user);
            if (existing is not null)
            {
                log.LogInformation(
                    "Media already exists; redirecting to canonical id {Id}",
                    existing.Id
                );
                ctx.ReplaceGuid(existing.Id);
                await next();
                return;
            }
        }

        // Fetch full metadata.
        //
        // A raw indexer result is the exception: its id is derived from the
        // query and the release, so no metadata provider can resolve it and
        // asking one throws. What the search captured is all the metadata
        // there is, so it is used as-is.
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        var isRawSearchResult = stremioMeta.Id.StartsWith(
            NebulaBridgeManager.RawSearchIdPrefix,
            StringComparison.OrdinalIgnoreCase
        );
        var meta = isRawSearchResult
            ? stremioMeta
            : await metadata.GetMetaAsync(
                cfg,
                stremioMeta.ImdbId ?? stremioMeta.Id,
                stremioMeta.Type,
                ctx.HttpContext.RequestAborted
            );
        if (meta is null)
        {
            log.LogError(
                "aio meta not found for {Id} {Type}, maybe try aiometadata as meta addon.",
                stremioMeta.Id,
                stremioMeta.Type
            );
            await next();
            return;
        }

        // Insert the item
        var baseItem = await InsertMetaAsync(guid, root, meta, user);
        if (baseItem is not null)
        {
            if (!baseItem.HasDiscoveryTag())
            {
                baseItem.Tags = [.. (baseItem.Tags ?? []), NebulaBridgeManager.DiscoveryTag];
                await baseItem
                    .UpdateToRepositoryAsync(
                        ItemUpdateType.MetadataEdit,
                        ctx.HttpContext.RequestAborted
                    )
                    .ConfigureAwait(false);
            }
            ctx.ReplaceGuid(baseItem.Id);
            // The search-result metadata stays cached under the virtual id. Clients keep using
            // that id after the insert (a second PlaybackInfo, images, LiveStreams/Open), and a
            // request that arrives once the item exists is redirected to it through
            // FindExistingItem above; dropping the metadata here made such a request 404.
        }

        await next();
    }

    private async Task<bool> TryHydrateHierarchyRequestAsync(
        ActionExecutingContext ctx,
        Guid userId
    )
    {
        var action = ctx.GetActionName();
        var request = ctx.HttpContext.Request;
        var cancellationToken = request.HttpContext.RequestAborted;

        if (string.Equals(action, "GetSeasons", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.TryGetRouteGuid(out var seriesId))
            {
                await hierarchy
                    .HydrateSeriesAsync(seriesId, userId, cancellationToken)
                    .ConfigureAwait(false);
                prefetch.RequestNextUp(seriesId, userId);
                return true;
            }
            return false;
        }

        if (string.Equals(action, "GetEpisodes", StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetQueryGuid(request, "SeasonId", out var seasonId))
            {
                await hierarchy
                    .HydrateSeasonAsync(seasonId, userId, cancellationToken)
                    .ConfigureAwait(false);
                prefetch.RequestNextUp(seasonId, userId);
                return true;
            }
            if (ctx.TryGetRouteGuid(out var seriesId))
            {
                await hierarchy
                    .HydrateSeriesAsync(
                        seriesId,
                        userId,
                        cancellationToken,
                        includeEpisodes: true
                    )
                    .ConfigureAwait(false);
                prefetch.RequestNextUp(seriesId, userId);
                return true;
            }
            return false;
        }

        if (
            string.Equals(action, "GetItems", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase)
        )
        {
            if (!TryGetQueryGuid(request, "ParentId", out var parentId))
            {
                return false;
            }

            if (libraryManager.GetItemById(parentId) is Season)
            {
                await hierarchy
                    .HydrateSeasonAsync(parentId, userId, cancellationToken)
                    .ConfigureAwait(false);
                prefetch.RequestNextUp(parentId, userId);
                return true;
            }
            if (libraryManager.GetItemById(parentId) is Series)
            {
                await hierarchy
                    .HydrateSeriesAsync(parentId, userId, cancellationToken)
                    .ConfigureAwait(false);
                prefetch.RequestNextUp(parentId, userId);
                return true;
            }
        }

        return false;
    }

    private static bool TryGetQueryGuid(
        Microsoft.AspNetCore.Http.HttpRequest request,
        string key,
        out Guid value
    )
    {
        value = Guid.Empty;
        return request.Query.TryGetValue(key, out var raw)
            && Guid.TryParse(raw.ToString(), out value)
            && value != Guid.Empty;
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        User user
    )
    {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct =>
            {
                (baseItem, created) = await manager.InsertMeta(
                    root,
                    meta,
                    user,
                    false,
                    true,
                    meta.Type is StremioMediaType.Series,
                    ct,
                    includeSeriesEpisodes: false
                );
            }
        );

        if (baseItem is not null && created)
            log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
