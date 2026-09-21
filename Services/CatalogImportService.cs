using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private readonly KeyLock _catalogLocks = new();

    public Task ImportCatalogAsync(
        string catalogId,
        string type,
        CancellationToken ct,
        IProgress<double>? progress = null
    ) => _catalogLocks.RunSingleFlightAsync(
        CatalogLockKey(catalogId, type),
        lockCt => ImportCatalogCoreAsync(catalogId, type, lockCt, progress),
        ct
    );

    private async Task ImportCatalogCoreAsync(
        string catalogId,
        string type,
        CancellationToken ct,
        IProgress<double>? progress
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
            var receivedAuthoritativePage = false;
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

                // A returned page, including an empty one, is authoritative. Failures and
                // malformed responses throw, so they never trigger destructive reconciliation.
                receivedAuthoritativePage = true;

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
                                        ex,
                                        "{CatId}: insert metadata failed for {Id}",
                                        catalogId,
                                        meta.Id
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

            var canReconcile = CanReconcileCatalogSnapshot(receivedAuthoritativePage, failedItems);
            if (catalogCfg.CreateCollection && canReconcile)
            {
                await UpdateCollectionAsync(
                        catalogCfg,
                        importedIds.Values.Where(id => id != Guid.Empty).Take(100).ToList()
                    )
                    .ConfigureAwait(false);
            }


            if (canReconcile)
            {
                await ReconcileCatalogItemsAsync(
                        catalogCfg,
                        ReconcileFolders(catalogCfg, catalogFolder),
                        catalogTag,
                        importedIds.Values.Where(id => id != Guid.Empty).ToHashSet(),
                        ct
                    )
                    .ConfigureAwait(false);
            }
            else if (failedItems > 0)
            {
                logger.LogWarning(
                    "Catalog {Id} had {FailureCount} insertion failures; stale-item pruning was skipped to retain the last-known-good contents",
                    catalogCfg.Id,
                    failedItems
                );
            }
            else
            {
                logger.LogWarning(
                    "Catalog {Id} did not produce an authoritative upstream snapshot; stale-item pruning was skipped to retain the last-known-good contents",
                    catalogCfg.Id
                );
            }

            logger.LogInformation("{Id}: processed ({Count} items)", catalogCfg.Id, processedItems);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
                "Catalog sync failed for {Id}",
                catalogCfg.Id
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

    /// <summary>
    /// Drops this source's tag from every item it no longer lists. An item stays put while
    /// another source still lists it or a user has interacted with it (so Jellyfin's own
    /// Continue Watching / Next Up keep working on the stub); otherwise it is deleted.
    /// </summary>
    private async Task ReconcileCatalogItemsAsync(
        CatalogConfig catalog,
        IEnumerable<Folder> folders,
        string catalogTag,
        IReadOnlySet<Guid> retainedIds,
        CancellationToken cancellationToken
    )
    {
        var kind = BridgeLibraryService.IsSeries(catalog) ? BaseItemKind.Series : BaseItemKind.Movie;
        var staleItems = folders
            .DistinctBy(folder => folder.Id)
            .SelectMany(folder => libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    ParentId = folder.Id,
                    IncludeItemTypes = [kind],
                    Recursive = false,
                    IsDeadPerson = true,
                }
            ))
            .Where(item =>
                item.Tags?.Contains(catalogTag, StringComparer.OrdinalIgnoreCase) == true
                && !retainedIds.Contains(item.Id)
            )
            .ToList();
        foreach (var stale in staleItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = (stale.Tags ?? [])
                .Where(tag => !string.Equals(tag, catalogTag, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var stillListed = remaining.Any(tag =>
                tag.StartsWith(NebulaBridgeManager.CatalogTagPrefix, StringComparison.OrdinalIgnoreCase)
            );
            if (!stillListed && !HasUserState(stale))
            {
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
                continue;
            }

            if (!stillListed && !remaining.Contains(NebulaBridgeManager.PromotedTag, StringComparer.OrdinalIgnoreCase))
            {
                // Pinned: no feed lists it any more, but someone has watched or saved it.
                remaining.Add(NebulaBridgeManager.PromotedTag);
            }

            stale.Tags = [.. remaining];
            await stale
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
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
            return data.HasMeaningfulInteraction();
        }));
    }

    internal static string BuildCatalogTag(CatalogConfig catalog) =>
        $"{NebulaBridgeManager.CatalogTagPrefix}{catalog.Source}:{catalog.Type}:{catalog.Id}";

    internal static bool CanReconcileCatalogSnapshot(bool receivedAuthoritativePage, int failedItems) =>
        receivedAuthoritativePage && failedItems == 0;

    private static Guid CatalogLockKey(string catalogId, string type) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{catalogId}\n{type}")));

    public Task DisableCatalogAsync(
        CatalogConfig catalog,
        CancellationToken cancellationToken
    ) => _catalogLocks.RunSingleFlightAsync(
        CatalogLockKey(catalog.Id, catalog.Type),
        lockCt => DisableCatalogCoreAsync(catalog, lockCt),
        cancellationToken
    );

    private async Task DisableCatalogCoreAsync(
        CatalogConfig catalog,
        CancellationToken cancellationToken
    )
    {
        var folder = manager.TryGetFolderByPath(bridgeLibraries.GetCatalogDescriptor(catalog).Path);
        if (folder is null)
        {
            return;
        }

        await ReconcileCatalogItemsAsync(
                catalog,
                ReconcileFolders(catalog, folder),
                BuildCatalogTag(catalog),
                new HashSet<Guid>(),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A source that moved between its own library and the shared one may have left tagged
    /// items behind in the other place, so both are reconciled.
    /// </summary>
    private IEnumerable<Folder> ReconcileFolders(CatalogConfig catalog, Folder current)
    {
        yield return current;
        var aggregate = manager.TryGetFolderByPath(
            bridgeLibraries.GetAggregateDescriptor(BridgeLibraryService.IsSeries(catalog)).Path
        );
        if (aggregate is not null && aggregate.Id != current.Id)
        {
            yield return aggregate;
        }
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
        await bridgeLibraries.RefreshArtworkAsync(ct).ConfigureAwait(false);
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
        await bridgeLibraries.RefreshArtworkAsync(ct).ConfigureAwait(false);
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
