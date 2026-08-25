using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.NativeSources;

public sealed class IndexerCatalogRefreshService(
    IndexerUpdateCoordinator coordinator,
    IIndexerCatalogSettings settings,
    ILogger<IndexerCatalogRefreshService> logger
) : BackgroundService
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation("Automatic indexer catalog updates are disabled");
            return;
        }

        await TryUpdateAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await TryUpdateAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await coordinator.UpdateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Automatic indexer catalog update failed");
        }
    }
}
