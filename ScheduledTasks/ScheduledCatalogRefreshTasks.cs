using MediaBrowser.Model.Tasks;
using NebulaBridge.Services;

namespace NebulaBridge.ScheduledTasks;

public sealed class DailyCatalogRefreshTask(CatalogImportService imports) : IScheduledTask
{
    public string Name => "Refresh daily Nebula Bridge catalogs";
    public string Key => "NebulaBridgeDailyCatalogRefresh";
    public string Description =>
        "Refreshes Trending and Box Office libraries daily at 2:00 AM server-local time.";
    public string Category => "Nebula Bridge";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
        },
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        imports.SyncCadenceAsync(
            CatalogRefreshCadence.Daily,
            cancellationToken,
            progress
        );
}

public sealed class WeeklyCatalogRefreshTask(CatalogImportService imports) : IScheduledTask
{
    public string Name => "Refresh weekly Nebula Bridge catalogs";
    public string Key => "NebulaBridgeWeeklyCatalogRefresh";
    public string Description =>
        "Refreshes Popular, Anticipated, and curated libraries Sundays at 3:00 AM server-local time.";
    public string Category => "Nebula Bridge";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = System.DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        },
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        imports.SyncCadenceAsync(
            CatalogRefreshCadence.Weekly,
            cancellationToken,
            progress
        );
}
