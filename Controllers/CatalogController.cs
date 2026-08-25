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
[Route("gelato/catalogs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CatalogController(
    ILogger<CatalogController> logger,
    CatalogService catalogService,
    CatalogImportService importService,
    TraktNextEpisodesService nextEpisodesService,
    ITaskManager taskManager
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

        catalogService.UpdateCatalogConfig(config);
        if (!config.Enabled)
        {
            await importService
                .DisableCatalogAsync(config, cancellationToken)
                .ConfigureAwait(false);
        }
        return Ok();
    }

    [HttpPost("{id}/{type}/import")]
    public Task<ActionResult> TriggerImport([FromRoute] string id, [FromRoute] string type)
    {
        logger.LogInformation("Manual import triggered for {Id} {Type}", id, type);

        // Run in background? Or await?
        // User probably wants to know it started.
        // Awaiting might timeout if it takes long.
        // But existing implementations awaited.
        // Let's fire and forget but log, or return accepted.
        // "Straight approach" -> maybe just await it so user sees errors?
        // But browser timeout is 2 mins usually. Import can take longer.
        // Better to run in background.

        _ = Task.Run(async () =>
        {
            try
            {
                if (id == TraktNextEpisodesService.CatalogId)
                {
                    await nextEpisodesService.SyncAsync(
                        new Progress<double>(),
                        CancellationToken.None
                    );
                }
                else
                {
                    await importService.ImportCatalogAsync(id, type, CancellationToken.None);
                }
                //await libraryManager
                //    .ValidateMediaLibrary(new Progress<double>(), CancellationToken.None)
                //    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in manual import for {Id}", id);
            }
        });

        return Task.FromResult<ActionResult>(Accepted());
    }

    [HttpPost("import-all")]
    public ActionResult ImportAll()
    {
        logger.LogInformation("Manual import triggered for all enabled catalogs");

        _ = Task.Run(() =>
        {
            try
            {
                taskManager.CancelIfRunningAndQueue<NebulaBridgeCatalogItemsSyncTask>();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in manual import for all enabled catalogs");
            }

            return Task.CompletedTask;
        });

        return Accepted();
    }
}
