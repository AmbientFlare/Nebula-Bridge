using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.ScheduledTasks;

/// <summary>
/// Sweeps up items produced by a raw indexer search.
///
/// Those results are meant to be looked at once and discarded, so they are
/// deleted a few hours after they were created rather than accumulating in a
/// library. Only items whose Stremio id carries the raw-search prefix are
/// touched; nothing that came from a metadata provider is affected.
/// </summary>
public sealed class PurgeRawSearchItemsTask(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    ILogger<PurgeRawSearchItemsTask> log
) : IScheduledTask
{
    public string Name => "Purge raw search results";
    public string Key => "PurgeRawSearchItemsTask";
    public string Description =>
        "Removes throwaway items created by raw indexer searches once they expire.";
    public string Category => "Nebula Bridge Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                // The sweep walks every bridge item, so it runs well inside the
                // shortest sensible lifetime rather than as often as possible.
                IntervalTicks = TimeSpan.FromMinutes(30).Ticks,
            },
        ];
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var lifetime = TimeSpan.FromMinutes(
            Math.Clamp(
                NebulaBridgePlugin.Instance?.Configuration.RawSearchLifetimeMinutes ?? 360,
                5,
                10080
            )
        );
        var cutoff = DateTime.UtcNow - lifetime;

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { { "Stremio", string.Empty } },
            IsDeadPerson = true,
        };

        var expired = libraryManager
            .GetItemList(query)
            .Where(item => item.IsDisposableRawSearchResult() && item.DateCreated < cutoff)
            .Where(item => !HasMeaningfulUserState(item))
            .ToArray();

        if (expired.Length == 0)
        {
            progress?.Report(100.0);
            return Task.CompletedTask;
        }

        log.LogInformation(
            "Purging {Count} raw search result(s) older than {Minutes} minute(s)",
            expired.Length,
            lifetime.TotalMinutes
        );

        var done = 0;
        foreach (var item in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                libraryManager.DeleteItem(
                    item,
                    new DeleteOptions { DeleteFileLocation = false },
                    true
                );
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to delete raw search result {ItemId}", item.Id);
            }

            done++;
            progress?.Report(Math.Min(100.0, ((double)done / expired.Length) * 100.0));
        }

        progress?.Report(100.0);
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
