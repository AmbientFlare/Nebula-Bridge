using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using NebulaBridge.Services;

namespace NebulaBridge.ScheduledTasks;

public sealed class PurgePlaybackCacheTask(AcquisitionCoordinator acquisitions, ILogger<PurgePlaybackCacheTask> log) : IScheduledTask
{
    public string Name => "Purge expired playback cache";
    public string Key => "PurgePlaybackCacheTask";
    public string Description => "Removes expired temporary progressive playback cache entries.";
    public string Category => "Nebula Bridge Maintenance";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!acquisitions.Enabled)
        {
            progress.Report(100);
            return;
        }
        var stale = (await acquisitions.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(job => job.Retention == AcquisitionRetention.Temporary && job.ExpiresUtc <= DateTimeOffset.UtcNow)
            .Where(job => job.State is not (AcquisitionJobState.Downloading or AcquisitionJobState.Completing or AcquisitionJobState.Importing))
            .ToList();
        for (var i = 0; i < stale.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await acquisitions.PurgeAsync(stale[i].Id, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Failed to purge playback cache job {JobId}", stale[i].Id); }
            progress.Report(100.0 * (i + 1) / Math.Max(stale.Count, 1));
        }
        progress.Report(100);
    }
}
