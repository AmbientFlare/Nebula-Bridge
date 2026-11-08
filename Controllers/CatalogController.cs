using NebulaBridge.Config;
using NebulaBridge.ScheduledTasks;
using NebulaBridge.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge/catalogs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CatalogController(
    ILogger<CatalogController> logger,
    CatalogService catalogService,
    CatalogImportService importService,
    TraktNextEpisodesService nextEpisodesService,
    BridgeLibraryService bridgeLibraries,
    UserAccessService userAccess,
    ITaskManager taskManager,
    BackgroundWork backgroundWork
) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CatalogConfig>>> GetCatalogs()
    {
        // Use Global user for now, or HttpContext.User if we want per-user catalogs later
        // But CatalogService currently uses Guid.Empty for global config if passed
        // We'll stick to global administration for now as per plan
        return await catalogService.GetCatalogsAsync(Guid.Empty);
    }

    [HttpPost("{id}/{type}/config")]
    public async Task<ActionResult> UpdateConfig(
        [FromRoute] string id,
        [FromRoute] string type,
        [FromBody] CatalogConfig config,
        CancellationToken cancellationToken
    )
    {
        if (config.Id != id || config.Type != type)
        {
            return BadRequest("ID/Type mismatch");
        }

        var previous = catalogService.GetCatalogConfig(id, type);
        var hadOwnLibrary = previous is { Enabled: true, SeparateLibrary: true };
        catalogService.UpdateCatalogConfig(config);
        if (!config.Enabled)
        {
            await importService
                .DisableCatalogAsync(config, cancellationToken)
                .ConfigureAwait(false);
        }

        if (hadOwnLibrary && !(config.Enabled && config.SeparateLibrary))
        {
            // The source's own library is no longer wanted; fold it back into the shared one.
            await bridgeLibraries.ConsolidateLegacyLibrariesAsync(cancellationToken).ConfigureAwait(false);
            await userAccess.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
        }
        return Ok();
    }

    [HttpPost("{id}/{type}/import")]
    public Task<ActionResult> TriggerImport([FromRoute] string id, [FromRoute] string type)
    {
        logger.LogInformation("Manual import triggered for {Id} {Type}", id, type);

        // An import can outlive a browser request, so it runs as owned background work and the
        // request answers Accepted as soon as it is queued.
        backgroundWork.Run(
            $"manual-import:{id}",
            ct => id == TraktNextEpisodesService.CatalogId
                ? nextEpisodesService.SyncAsync(new Progress<double>(), ct)
                : importService.ImportCatalogAsync(id, type, ct));

        return Task.FromResult<ActionResult>(Accepted());
    }

    [HttpPost("import-all")]
    public ActionResult ImportAll()
    {
        logger.LogInformation("Manual import triggered for all enabled catalogs");

        backgroundWork.Run(
            "manual-import-all",
            _ =>
            {
                taskManager.CancelIfRunningAndQueue<NebulaBridgeCatalogItemsSyncTask>();
                return Task.CompletedTask;
            });

        return Accepted();
    }
}
