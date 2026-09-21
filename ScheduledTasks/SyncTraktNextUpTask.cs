using MediaBrowser.Model.Tasks;
using NebulaBridge.Services;

namespace NebulaBridge.ScheduledTasks;

public sealed class SyncTraktNextUpTask(TraktNextEpisodesService service) : IScheduledTask
{
    public string Name => "Refresh Trakt Next Episodes";
    public string Key => "NebulaBridgeSyncTraktNextUp";
    public string Description =>
        "Rebuilds the managed Trakt Next Episodes library at 1:00 AM and 1:00 PM server-local time.";
    public string Category => "Nebula Bridge";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(1).Ticks,
        },
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(13).Ticks,
        },
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        service.SyncAsync(progress, cancellationToken);
}
