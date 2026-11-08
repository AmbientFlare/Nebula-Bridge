using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.ScheduledTasks;

/// <summary>
/// Removes ordinary, untouched search discoveries after their quarantine lifetime. Raw results
/// have their own shorter purge task because their configured lifetime is in minutes.
/// </summary>
public sealed class PurgeDiscoveryItemsTask(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    ILogger<PurgeDiscoveryItemsTask> log
) : IScheduledTask
{
    public string Name => "Purge stale discovery items";

    public string Key => "PurgeDiscoveryItemsTask";

    public string Description =>
        "Removes untouched ordinary Nebula Bridge search discoveries after their configured lifetime.";

    public string Category => "Nebula Bridge Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks,
        },
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var lifetime = TimeSpan.FromDays(
            Math.Clamp(NebulaBridgePlugin.Instance?.Configuration.DiscoveryLifetimeDays ?? 3, 1, 365)
        );
        var cutoff = DateTime.UtcNow - lifetime;
        var stale = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Recursive = true,
                    HasAnyProviderId = new Dictionary<string, string> { { "Stremio", string.Empty } },
                    IsDeadPerson = true,
                }
            )
            .Where(item => !item.IsRawSearchResult())
            .Where(item => item.HasDiscoveryTag() && item.DateCreated < cutoff)
            .Where(item => !HasMeaningfulUserState(item))
            .ToArray();

        for (var index = 0; index < stale.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = stale[index];
            try
            {
                libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to purge stale discovery item {ItemId}", item.Id);
            }

            progress?.Report(100.0 * (index + 1) / Math.Max(stale.Length, 1));
        }

        progress?.Report(100);
        return Task.CompletedTask;
    }

    private bool HasMeaningfulUserState(BaseItem item)
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
}
