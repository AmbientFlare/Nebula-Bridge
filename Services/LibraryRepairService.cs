using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

/// <summary>
/// Idempotent repairs for bridge-owned library rows: collapses duplicate copies of one title
/// that share a folder and identity path, and migrates rows left behind by the project the
/// plugin was forked from onto the current <c>nebulabridge://</c> identity.
/// </summary>
public sealed class LibraryRepairService(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    NebulaBridgeManager manager,
    ILogger<LibraryRepairService> logger
)
{
    public const string IdentityPrefix = "nebulabridge://";

    /// <summary>Identity scheme and tags written by the upstream fork before the rename.</summary>
    internal const string LegacyIdentityPrefix = "gelato://";
    internal const string LegacyStreamTag = "gelato-stream";
    internal const string LegacyTreeSyncedTag = "gelato-tree-synced";

    public async Task<RepairSummary> RepairAsync(CancellationToken cancellationToken)
    {
        var migrated = await MigrateLegacyItemsAsync(cancellationToken).ConfigureAwait(false);
        var retagged = await RetagLegacyItemsAsync(cancellationToken).ConfigureAwait(false);
        var removed = RemoveDuplicateCopies(cancellationToken);
        return new RepairSummary(migrated, retagged, removed);
    }

    /// <summary>
    /// Every bridge-created title lives at a deterministic path inside its folder, so two rows
    /// with the same parent and path are the same title listed twice. The copy that carries
    /// user state (or, failing that, the newest) survives; the others hand over their state
    /// and are deleted.
    /// </summary>
    public int RemoveDuplicateCopies(CancellationToken cancellationToken)
    {
        var groups = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .Where(item =>
                item.Path?.StartsWith(IdentityPrefix, StringComparison.OrdinalIgnoreCase) == true
            )
            .GroupBy(item => (item.ParentId, Path: item.Path!))
            .Where(group => group.Count() > 1)
            .ToList();

        var removed = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ranked = group
                .Select(item => (Item: item, HasState: HasUserState(item)))
                .OrderByDescending(entry => entry.HasState)
                .ThenByDescending(entry => entry.Item.HasTreeSyncedTag())
                .ThenByDescending(entry => entry.Item.DateCreated)
                .Select(entry => entry.Item)
                .ToList();
            var keeper = ranked[0];
            foreach (var duplicate in ranked.Skip(1))
            {
                manager.MergeUserData(duplicate, keeper, cancellationToken);
                libraryManager.DeleteItem(
                    duplicate,
                    new DeleteOptions { DeleteFileLocation = false }
                );
                removed++;
            }

            logger.LogInformation(
                "Collapsed {Count} duplicate copies of {Name} at {Path} into {KeeperId}",
                ranked.Count - 1,
                keeper.Name,
                keeper.Path,
                keeper.Id
            );
        }

        return removed;
    }

    /// <summary>
    /// A legacy row whose <c>nebulabridge://</c> twin exists is folded into that twin; one
    /// without a twin is renamed in place (keeping its id and user data) and re-homed under
    /// the configured bridge folder.
    /// </summary>
    public async Task<int> MigrateLegacyItemsAsync(CancellationToken cancellationToken)
    {
        var legacy = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .Where(item =>
                item.Path?.StartsWith(LegacyIdentityPrefix, StringComparison.OrdinalIgnoreCase)
                == true
            )
            .ToList();
        if (legacy.Count == 0)
        {
            return 0;
        }

        var cfg = NebulaBridgePlugin.Instance!.GetConfig(Guid.Empty);
        var migrated = 0;
        foreach (var item in legacy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = string.Concat(IdentityPrefix, item.Path!.AsSpan(LegacyIdentityPrefix.Length));
            var twin = libraryManager.FindByPath(targetPath, false);
            if (twin is not null && twin.Id != item.Id && twin.GetBaseItemKind() == item.GetBaseItemKind())
            {
                manager.MergeUserData(item, twin, cancellationToken);
                libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                logger.LogInformation(
                    "Folded legacy item {Name} ({ItemId}) into {TwinId}",
                    item.Name,
                    item.Id,
                    twin.Id
                );
                migrated++;
                continue;
            }

            var home = item is Series
                ? manager.TryGetSeriesFolder(cfg)
                : manager.TryGetMovieFolder(cfg);
            if (home is not null && home.Id != item.ParentId)
            {
                item.SetParent(home);
            }
            await RenameAsync(item, targetPath, cancellationToken).ConfigureAwait(false);

            if (item is Folder folder)
            {
                foreach (var child in folder.GetRecursiveChildren(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (child.Path?.StartsWith(LegacyIdentityPrefix, StringComparison.OrdinalIgnoreCase) != true)
                    {
                        continue;
                    }

                    await RenameAsync(
                            child,
                            string.Concat(IdentityPrefix, child.Path.AsSpan(LegacyIdentityPrefix.Length)),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            }

            logger.LogInformation(
                "Renamed legacy item {Name} ({ItemId}) to {Path}",
                item.Name,
                item.Id,
                targetPath
            );
            migrated++;
        }

        return migrated;
    }

    public async Task<int> RetagLegacyItemsAsync(CancellationToken cancellationToken)
    {
        var retagged = 0;
        foreach (var (legacyTag, currentTag) in new[]
        {
            (LegacyStreamTag, NebulaBridgeManager.StreamTag),
            (LegacyTreeSyncedTag, NebulaBridgeManager.TreeSyncedTag),
        })
        {
            var items = libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    Tags = [legacyTag],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            );
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tags = (item.Tags ?? [])
                    .Where(tag => !tag.Equals(legacyTag, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (!tags.Contains(currentTag, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(currentTag);
                }

                item.Tags = [.. tags];
                await item
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
                retagged++;
            }
        }

        return retagged;
    }

    private static async Task RenameAsync(BaseItem item, string path, CancellationToken cancellationToken)
    {
        item.Path = path;
        item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
        await item
            .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool HasUserState(BaseItem item)
    {
        var candidates = new List<BaseItem> { item };
        if (item is Series)
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

        return userManager
            .GetUsers()
            .Any(user =>
                candidates.Any(candidate =>
                    userDataManager.GetUserData(user, candidate).HasMeaningfulInteraction()
                )
            );
    }
}

public readonly record struct RepairSummary(int MigratedLegacyItems, int RetaggedItems, int RemovedDuplicates)
{
    public bool Changed => MigratedLegacyItems > 0 || RetaggedItems > 0 || RemovedDuplicates > 0;
}
