using NebulaBridge.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.ScheduledTasks;

public sealed class NebulaBridgeCatalogItemsSyncTask(
    ILogger<NebulaBridgeCatalogItemsSyncTask> log,
    CatalogImportService importService
) : IScheduledTask
{
    public string Name => "Import Catalogs";
    public string Key => "NebulaBridgeCatalogItemsSync";
    public string Description => "Imports items from enabled Stremio catalogs into Jellyfin.";
    public string Category => "Nebula Bridge";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        log.LogInformation("Starting Nebula Bridge catalog sync task...");
        await importService.SyncAllEnabledAsync(ct, progress).ConfigureAwait(false);
        log.LogInformation("Nebula Bridge catalog sync task finished.");
    }
}
