using NebulaBridge.NativeSources;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge/native-indexers")]
[Route("gelato/native-indexers")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class NativeIndexersController(
    IndexerDefinitionLoader definitionLoader,
    IndexerUpdateCoordinator updateCoordinator,
    NativeSourcePipeline sourcePipeline
) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<NativeDefinitionSummary>> GetDefinitions()
    {
        return Ok(definitionLoader.GetAllSummaries());
    }

    [HttpGet("status")]
    public ActionResult<IndexerRefreshResponse> GetIndexerStatus() =>
        Ok(definitionLoader.GetStatus());

    [HttpPost("refresh")]
    public async Task<ActionResult<IndexerCatalogUpdateResult>> RefreshIndexers(CancellationToken cancellationToken) =>
        Ok(await updateCoordinator.UpdateAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("{id}/enabled")]
    public ActionResult SetEnabled(string id, [FromBody] NativeIndexerEnabledRequest request)
    {
        if (!definitionLoader.SetEnabled(id, request.Enabled))
        {
            return BadRequest("The indexer is missing, invalid, or unsupported.");
        }
        return NoContent();
    }

    [HttpPost("validate")]
    public ActionResult<NativeDefinitionValidationResponse> ValidateDefinition(
        [FromBody] NativeDefinitionValidationRequest request
    )
    {
        var (definition, errors) = definitionLoader.Parse(request.Yaml);
        var summary = definition is null
            ? null
            : new NativeDefinitionSummary(
                definition.Id,
                definition.Name,
                definition.Description
            );
        return Ok(
            new NativeDefinitionValidationResponse(
                definition is not null,
                errors,
                summary
            )
        );
    }

    [HttpPost("search")]
    public async Task<ActionResult<NativeSearchResult>> Search(
        [FromBody] NativeSearchRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!IsEnabled())
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Query.Title))
        {
            return BadRequest("A title is required.");
        }

        try
        {
            return Ok(
                await sourcePipeline
                    .SearchAsync(request.Query, request.DefinitionId, cancellationToken)
                    .ConfigureAwait(false)
            );
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private static bool IsEnabled() =>
        NebulaBridgePlugin.Instance?.Configuration.EnableNativeScraper == true;
}
