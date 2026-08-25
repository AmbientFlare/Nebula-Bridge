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
    BridgeLibraryService bridgeLibraries,
    UserAccessService userAccess,
    ILogger<InsertActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    private static readonly TimeSpan PersistedSeriesFreshness = TimeSpan.FromDays(7);
    private readonly KeyLock _lock = new();
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (
            !ctx.IsInsertableAction()
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
        )
        {
            await next();
            return;
        }

        // Materialize a series tree as soon as the series/seasons view is opened. This applies
        // to Nebula Bridge stubs and, when configured, to local series extension.
        if (libraryManager.GetItemById(guid) is Series series)
        {
            await HandleSeriesAsync(userId, series, ctx.HttpContext.RequestAborted);
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

        // Fetch full metadata
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        var meta = await metadata.GetMetaAsync(
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
            manager.RemoveStremioMeta(guid);
        }

        await next();
    }

    private async Task HandleSeriesAsync(Guid userId, Series series, CancellationToken ct)
    {
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        var isNebulaBridge = series.IsNebulaBridge();

        if (isNebulaBridge || cfg.ExtendLocalSeriesTrees)
        {
            var hasEpisodes = libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        AncestorIds = [series.Id],
                        IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode],
                        Recursive = true,
                        Limit = 1,
                        IsDeadPerson = true,
                    }
                )
                .Count != 0;
            if (hasEpisodes)
            {
                if (!series.HasTreeSyncedTag())
                {
                    series.Tags = [.. (series.Tags ?? []), NebulaBridgeManager.TreeSyncedTag];
                    await series
                        .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                        .ConfigureAwait(false);
                }

                if (
                    series.DateLastRefreshed
                    < DateTime.UtcNow.Subtract(PersistedSeriesFreshness)
                )
                {
                    _ = _lock.RunSingleFlightAsync(
                        series.Id,
                        _ => RefreshPersistedSeriesTreeAsync(cfg, series)
                    );
                }
                return;
            }

            log.LogInformation(
                "InsertActionFilter: populating series tree on browse for {Name} ({Id})",
                series.Name,
                series.Id
            );

            var meta = await metadata.GetMetaAsync(cfg, series, ct).ConfigureAwait(false);
            if (meta is null)
                return;

            await manager
                .SyncSeriesTreesAsync(cfg, meta, ct, existingSeries: series)
                .ConfigureAwait(false);

            var populated = libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        AncestorIds = [series.Id],
                        IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode],
                        Recursive = true,
                        Limit = 1,
                        IsDeadPerson = true,
                    }
                )
                .Count != 0;
            if (populated && !series.HasTreeSyncedTag())
            {
                series.Tags = [.. (series.Tags ?? []), NebulaBridgeManager.TreeSyncedTag];
                await series
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                    .ConfigureAwait(false);
            }
            else if (!populated)
            {
                log.LogWarning(
                    "Browse-time metadata for {Name} contained no usable episodes; the series remains eligible for a later retry",
                    series.Name
                );
            }
        }
        else
        {
            // Setting disabled — clean any virtual items that may exist for this series
            manager.CleanVirtualTreeItem(series, ct);
        }
    }

    private async Task RefreshPersistedSeriesTreeAsync(
        PluginConfiguration cfg,
        Series series
    )
    {
        try
        {
            var meta = await metadata
                .GetMetaAsync(cfg, series, CancellationToken.None)
                .ConfigureAwait(false);
            if (meta is null)
            {
                return;
            }

            await manager
                .SyncSeriesTreesAsync(
                    cfg,
                    meta,
                    CancellationToken.None,
                    existingSeries: series
                )
                .ConfigureAwait(false);
            log.LogInformation(
                "Refreshed the persisted series tree for {SeriesName} in the background",
                series.Name
            );
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Background series-tree refresh failed for {SeriesName}; retained cached metadata",
                series.Name
            );
        }
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
                meta.Guid = guid;
                (baseItem, created) = await manager.InsertMeta(
                    root,
                    meta,
                    user,
                    false,
                    true,
                    meta.Type is StremioMediaType.Series,
                    ct
                );
            }
        );

        if (baseItem is not null && created)
            log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
