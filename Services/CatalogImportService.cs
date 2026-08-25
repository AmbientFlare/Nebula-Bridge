using System.Collections.Concurrent;
using System.Diagnostics;
using NebulaBridge.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

// For BoxSet

namespace NebulaBridge.Services;

public class CatalogImportService(
    ILogger<CatalogImportService> logger,
    NebulaBridgeManager manager,
    CatalogService catalogService,
    NativeTraktClient traktClient,
    ICollectionManager collectionManager,
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    BridgeLibraryService bridgeLibraries,
    UserAccessService userAccess
)
{
    public async Task ImportCatalogAsync(
        string catalogId,
        string type,
        CancellationToken ct,
        IProgress<double>? progress = null
    )
    {
        var catalogCfg = catalogService.GetCatalogConfig(catalogId, type);
        if (catalogCfg == null)
        {
            logger.LogWarning("Catalog config not found for {Id} {Type}", catalogId, type);
            return;
        }

        if (!catalogCfg.Enabled)
        {
            logger.LogInformation("Catalog {Id} {Type} is disabled, skipping.", catalogId, type);
            return;
        }
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(Guid.Empty);
        var stremio = cfg.Stremio;
        var isTrakt = string.Equals(catalogCfg.Source, "trakt", StringComparison.OrdinalIgnoreCase);

        if (!isTrakt && stremio is null)
        {
            logger.LogWarning("No legacy Stremio endpoint is configured; skipping catalog import.");
            return;
        }

        var descriptor = bridgeLibraries.GetCatalogDescriptor(catalogCfg);
        var catalogFolder = await bridgeLibraries
            .EnsureLibraryAsync(descriptor, ct)
            .ConfigureAwait(false);
        if (catalogFolder is null)
        {
            logger.LogWarning(
                "Catalog library {LibraryName} was created but is not ready; retrying on the next scheduled refresh",
                descriptor.Name
            );
            return;
        }

        await userAccess.ReconcileAllAsync(ct).ConfigureAwait(false);
        var catalogTag = BuildCatalogTag(catalogCfg);

        var maxItems = catalogCfg.MaxItems > 0 ? catalogCfg.MaxItems : cfg.CatalogMaxItems;
        if (maxItems <= 0)
        {
            logger.LogWarning("Catalog {Id} has an invalid item limit; skipping.", catalogId);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting import for catalog {Name} ({Id}) - Limit: {Limit}",
            catalogCfg.Name,
            catalogId,
            maxItems
        );

        try
        {
            var skip = 0;
            var processedItems = 0;
            var failedItems = 0;
            // keyed on stremio meta.Id to deduplicate within the import run
            var importedIds = new ConcurrentDictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            while (processedItems < maxItems)
            {
                ct.ThrowIfCancellationRequested();

                var page = isTrakt
                    ? await traktClient
                        .GetCatalogMetasAsync(catalogId, skip, ct)
                        .ConfigureAwait(false)
                    : await stremio!
                        .GetCatalogMetasAsync(catalogId, type, search: null, skip: skip)
                        .ConfigureAwait(false);

                if (page.Count == 0)
                {
                    break;
                }

                var remaining = maxItems - processedItems;
                var batch = page.Take(remaining).ToList();

                await Parallel
                    .ForEachAsync(
                        batch,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = 4,
                            CancellationToken = ct,
                        },
                        async (meta, innerCt) =>
                        {
                            if (!importedIds.TryAdd(meta.Id, Guid.Empty))
                            {
                                Interlocked.Increment(ref processedItems);
                                return;
                            }

                            var mediaType = meta.Type;
                            var baseItemKind = mediaType.ToBaseItem();
                            var root = baseItemKind is BaseItemKind.Series or BaseItemKind.Movie
                                ? catalogFolder
                                : null;

                            if (root is not null)
                            {
                                try
                                {
                                    if (isTrakt && baseItemKind == BaseItemKind.Series)
                                    {
                                        await traktClient
                                            .EnrichSeriesEpisodesAsync(meta, innerCt)
                                            .ConfigureAwait(false);
                                    }

                                    var (item, _) = await manager
                                        .InsertMeta(
                                            root,
                                            meta,
                                            null,
                                            !isTrakt,
                                            true,
                                            baseItemKind == BaseItemKind.Series,
                                            innerCt,
                                            descriptor.Key
                                        )
                                        .ConfigureAwait(false);

                                    if (item != null)
                                    {
                                        if (item.Tags?.Contains(catalogTag, StringComparer.OrdinalIgnoreCase) != true)
                                        {
                                            item.Tags = [.. (item.Tags ?? []), catalogTag];
                                            await item
                                                .UpdateToRepositoryAsync(
                                                    ItemUpdateType.MetadataEdit,
                                                    innerCt
                                                )
                                                .ConfigureAwait(false);
                                        }
                                        importedIds[meta.Id] = item.Id;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref failedItems);
                                    logger.LogError(
                                        "{CatId}: insert meta failed for {Id}. Exception: {Message}\n{StackTrace}",
                                        catalogId,
                                        meta.Id,
                                        ex.Message,
                                        ex.StackTrace
                                    );
                                }
                            }

                            var done = Interlocked.Increment(ref processedItems);
                            progress?.Report(done * 100.0 / maxItems);
                        }
                    )
                    .ConfigureAwait(false);

                skip += page.Count;
            }

            if (catalogCfg.CreateCollection)
            {
                await UpdateCollectionAsync(
                        catalogCfg,
                        importedIds.Values.Where(id => id != Guid.Empty).Take(100).ToList()
                    )
                    .ConfigureAwait(false);
            }


            if (failedItems == 0)
            {
                await ReconcileCatalogItemsAsync(
                        catalogCfg,
                        catalogFolder,
                        catalogTag,
                        importedIds.Values.Where(id => id != Guid.Empty).ToHashSet(),
                        ct
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning(
                    "Catalog {Id} had {FailureCount} insertion failures; stale-item pruning was skipped to retain the last-known-good contents",
                    catalogCfg.Id,
                    failedItems
                );
            }

            logger.LogInformation("{Id}: processed ({Count} items)", catalogCfg.Id, processedItems);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                ex,
                "Catalog {Id} aborted due to non-user cancellation, continuing with next catalog",
                catalogId
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Catalog sync failed for {Id}: {Message}",
                catalogCfg.Id,
                ex.Message
            );
        }

        stopwatch.Stop();
        progress?.Report(100);
        logger.LogInformation(
            "Catalog {catalog} sync completed in {Minutes}m {Seconds}s ({TotalSeconds:F2}s total)",
            catalogCfg.Name,
            (int)stopwatch.Elapsed.TotalMinutes,
            stopwatch.Elapsed.Seconds,
            stopwatch.Elapsed.TotalSeconds
        );
    }

    private async Task ReconcileCatalogItemsAsync(
        CatalogConfig catalog,
        Folder catalogFolder,
        string catalogTag,
        IReadOnlySet<Guid> retainedIds,
        CancellationToken cancellationToken
    )
    {
        var staleItems = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = catalogFolder.Id,
                    IncludeItemTypes =
                    [
                        catalog.Type.Equals("series", StringComparison.OrdinalIgnoreCase)
                            ? BaseItemKind.Series
                            : BaseItemKind.Movie,
                    ],
                    Recursive = false,
                    IsDeadPerson = true,
                }
            )
            .Where(item =>
                item.Tags?.Contains(catalogTag, StringComparer.OrdinalIgnoreCase) == true
                && !retainedIds.Contains(item.Id)
            )
            .ToList();
        foreach (var stale in staleItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasUserState(stale))
            {
                var destination = await bridgeLibraries
                    .EnsureLibraryAsync(
                        bridgeLibraries.GetPromotionDescriptor(stale is MediaBrowser.Controller.Entities.TV.Series),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (destination is not null)
                {
                    await manager
                        .PromoteCatalogItemAsync(stale, destination, cancellationToken)
                        .ConfigureAwait(false);
                    await userAccess.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            libraryManager.DeleteItem(
                stale,
                new DeleteOptions { DeleteFileLocation = false }
            );
            logger.LogInformation(
                "Pruned stale catalog item {Name} ({ItemId}) from {CatalogName}",
                stale.Name,
                stale.Id,
                catalog.Name
            );
        }
    }

    private bool HasUserState(BaseItem item)
    {
        var candidates = new List<BaseItem> { item };
        if (item is MediaBrowser.Controller.Entities.TV.Series)
        {
            candidates.AddRange(
                libraryManager.GetItemList(
                    new InternalItemsQuery
                    {
                        AncestorIds = [item.Id],
                        Recursive = true,
                        IsDeadPerson = true,
                    }
                )
            );
        }

        return userManager.GetUsers().Any(user => candidates.Any(candidate =>
        {
            var data = userDataManager.GetUserData(user, candidate);
            return data is not null
                && (data.Played || data.IsFavorite || data.PlaybackPositionTicks > 0);
        }));
    }

    internal static string BuildCatalogTag(CatalogConfig catalog) =>
        $"{NebulaBridgeManager.CatalogTagPrefix}{catalog.Source}:{catalog.Type}:{catalog.Id}";

    public async Task DisableCatalogAsync(
        CatalogConfig catalog,
        CancellationToken cancellationToken
    )
    {
        var descriptor = catalog.Id == TraktNextEpisodesService.CatalogId
            ? bridgeLibraries.GetNextEpisodesDescriptor()
            : bridgeLibraries.GetCatalogDescriptor(catalog);
        var folder = manager.TryGetFolderByPath(descriptor.Path);
        if (folder is null)
        {
            return;
        }

        await ReconcileCatalogItemsAsync(
                catalog,
                folder,
                BuildCatalogTag(catalog),
                new HashSet<Guid>(),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<BoxSet?> GetOrCreateBoxSetAsync(CatalogConfig config)
    {
        var id = $"{config.Type}.{config.Id}";
        var collection = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet],
                    CollapseBoxSetItems = false,
                    Recursive = true,
                    HasAnyProviderId = new Dictionary<string, string> { { "Stremio", id } },
                }
            )
            .OfType<BoxSet>()
            .FirstOrDefault();

        if (collection is null)
        {
            collection = await collectionManager
                .CreateCollectionAsync(
                    new CollectionCreationOptions
                    {
                        Name = config.Name,
                        IsLocked = true,
                        ProviderIds = new Dictionary<string, string> { { "Stremio", id } },
                    }
                )
                .ConfigureAwait(false);

            collection.DisplayOrder = "Default";
            await collection
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }
        return collection;
    }

    private async Task UpdateCollectionAsync(CatalogConfig config, List<Guid> ids)
    {
        logger.LogInformation(
            "Updating collection {Name} with {Count} items",
            config.Name,
            ids.Count
        );
        try
        {
            var collection = await GetOrCreateBoxSetAsync(config).ConfigureAwait(false);
            if (collection != null)
            {
                var currentChildren = libraryManager
                    .GetItemList(new InternalItemsQuery { Parent = collection, Recursive = false })
                    .Select(i => i.Id)
                    .ToList();

                if (currentChildren.Count != 0)
                {
                    await collectionManager
                        .RemoveFromCollectionAsync(collection.Id, currentChildren)
                        .ConfigureAwait(false);
                }

                await collectionManager
                    .AddToCollectionAsync(collection.Id, ids)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating collection for {Name}", config.Name);
        }
    }

    public async Task SyncAllEnabledAsync(CancellationToken ct, IProgress<double>? progress = null)
    {
        var catalogs = await catalogService.GetCatalogsAsync(Guid.Empty);
        var enabled = catalogs
            .Where(c => c.Enabled && c.Id != TraktNextEpisodesService.CatalogId)
            .ToList();

        await SyncCatalogsAsync(enabled, ct, progress).ConfigureAwait(false);
    }

    public async Task SyncCadenceAsync(
        CatalogRefreshCadence cadence,
        CancellationToken ct,
        IProgress<double>? progress = null
    )
    {
        var catalogs = await catalogService.GetCatalogsAsync(Guid.Empty);
        var enabled = catalogs
            .Where(c =>
                c.Enabled
                && c.Id != TraktNextEpisodesService.CatalogId
                && BridgeLibraryService.GetCadence(c) == cadence
            )
            .ToList();
        await SyncCatalogsAsync(enabled, ct, progress).ConfigureAwait(false);
    }

    private async Task SyncCatalogsAsync(
        IReadOnlyList<CatalogConfig> enabled,
        CancellationToken ct,
        IProgress<double>? progress
    )
    {

        if (enabled.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        var defaultLimit = NebulaBridgePlugin.Instance!.Configuration.CatalogMaxItems;
        var total = enabled.Sum(c => c.MaxItems > 0 ? c.MaxItems : defaultLimit);
        var offset = 0;

        foreach (var cat in enabled)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogInformation("Processing enabled catalog: {Name}", cat.Name);

            var catMax = cat.MaxItems > 0 ? cat.MaxItems : defaultLimit;
            var localOffset = offset;
            var catProgress = progress is null
                ? null
                : (IProgress<double>)
                    new Progress<double>(p =>
                        progress.Report((localOffset + p / 100.0 * catMax) / total * 100.0)
                    );

            await ImportCatalogAsync(cat.Id, cat.Type, ct, catProgress).ConfigureAwait(false);

            offset += catMax;
        }

        // collections appear empty after inporting this fixes that.. sometimes...
        libraryManager.QueueLibraryScan();

        progress?.Report(100);
    }
}
