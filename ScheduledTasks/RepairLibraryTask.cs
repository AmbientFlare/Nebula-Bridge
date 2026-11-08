using MediaBrowser.Model.Tasks;
using NebulaBridge.Services;

namespace NebulaBridge.ScheduledTasks;

public sealed class RepairLibraryTask(LibraryRepairService repairs, UserAccessService userAccess) : IScheduledTask
{
    public string Name => "Repair Nebula Bridge library duplicates";
    public string Key => "NebulaBridgeRepairLibrary";

    public string Description =>
        "Collapses duplicate copies of the same Nebula Bridge title and migrates rows left over from older plugin versions. Also runs automatically at startup.";

    public string Category => "Nebula Bridge";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var summary = await repairs.RepairAsync(cancellationToken).ConfigureAwait(false);
        if (summary.Changed)
        {
            await userAccess.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
        }
        progress.Report(100);
    }
}
