using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

/// <summary>
/// Applies idempotent managed-library migrations after Jellyfin's virtual folders are ready.
/// </summary>
public sealed class ManagedLibraryStartupService(
    BridgeLibraryService libraries,
    UserAccessService userAccess,
    ILogger<ManagedLibraryStartupService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken).ConfigureAwait(false);
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                if (
                    await libraries
                        .ConsolidateLegacyDiscoveryLibrariesAsync(stoppingToken)
                        .ConfigureAwait(false)
                )
                {
                    await userAccess.ReconcileAllAsync(stoppingToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Managed-library startup migration attempt {Attempt} failed",
                    attempt
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }

        logger.LogWarning(
            "Managed-library startup migration was deferred because Jellyfin's library root did not become ready"
        );
    }
}
