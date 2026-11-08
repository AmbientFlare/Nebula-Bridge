using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

/// <summary>
/// Applies idempotent managed-library migrations after Jellyfin's virtual folders are ready.
/// </summary>
public sealed class ManagedLibraryStartupService(
    BridgeLibraryService libraries,
    LibraryRepairService repairs,
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
                        .ConsolidateLegacyLibrariesAsync(stoppingToken)
                        .ConfigureAwait(false)
                )
                {
                    await userAccess.ReconcileAllAsync(stoppingToken).ConfigureAwait(false);
                    await RepairAsync(stoppingToken).ConfigureAwait(false);
                    await libraries.RefreshArtworkAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task RepairAsync(CancellationToken stoppingToken)
    {
        try
        {
            var summary = await repairs.RepairAsync(stoppingToken).ConfigureAwait(false);
            if (summary.Changed)
            {
                logger.LogInformation(
                    "Library repair migrated {Migrated} legacy item(s), retagged {Retagged}, removed {Removed} duplicate(s)",
                    summary.MigratedLegacyItems,
                    summary.RetaggedItems,
                    summary.RemovedDuplicates
                );
                await userAccess.ReconcileAllAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Library repair failed; it can be re-run from the scheduled tasks page");
        }
    }
}
