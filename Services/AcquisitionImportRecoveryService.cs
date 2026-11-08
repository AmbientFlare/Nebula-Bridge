using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

internal enum AcquisitionRecoveryAction
{
    None,
    Complete,
    Import,
}

/// <summary>Resumes imports whose durable intent was written before a process crash.</summary>
public sealed class AcquisitionImportRecoveryService(
    AcquisitionJobStore store,
    AcquisitionImportService importer,
    AcquisitionCoordinator coordinator,
    ILogger<AcquisitionImportRecoveryService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (NebulaBridgePlugin.Instance?.Configuration.EnableLocalAcquisition != true)
            return;
        try
        {
            foreach (var job in await store.ReadAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var action = GetRecoveryAction(job);
                    if (action == AcquisitionRecoveryAction.Import)
                        await importer.ImportAsync(job.Id, stoppingToken).ConfigureAwait(false);
                    else if (action == AcquisitionRecoveryAction.Complete)
                        _ = coordinator.StartCompletion(job.Id); // owned by the coordinator, not this startup pass
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                { logger.LogWarning(ex, "Nebula acquisition recovery failed for {JobId}", job.Id); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Stops the completions the coordinator owns before the host finishes shutting down.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await coordinator.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static AcquisitionRecoveryAction GetRecoveryAction(AcquisitionJob job) => job.State switch
    {
        AcquisitionJobState.ReadyToImport or AcquisitionJobState.Importing => AcquisitionRecoveryAction.Import,
        AcquisitionJobState.Queued or AcquisitionJobState.Completing
            when job.Retention != AcquisitionRetention.Temporary => AcquisitionRecoveryAction.Complete,
        AcquisitionJobState.Failed
            when job.Retention != AcquisitionRetention.Temporary => AcquisitionRecoveryAction.Complete,
        _ => AcquisitionRecoveryAction.None,
    };
}
