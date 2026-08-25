using NebulaBridge.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge/trakt")]
[Route("gelato/trakt")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class TraktController(NativeTraktClient traktClient) : ControllerBase
{
    [HttpPost("device/start")]
    public async Task<ActionResult<TraktDeviceAuthorizationStatus>> StartDeviceAuthorization(
        CancellationToken cancellationToken
    )
    {
        try
        {
            return Ok(
                await traktClient
                    .StartDeviceAuthorizationAsync(cancellationToken)
                    .ConfigureAwait(false)
            );
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("device/status")]
    public async Task<ActionResult<TraktDeviceAuthorizationStatus>> GetDeviceStatus(
        CancellationToken cancellationToken
    ) =>
        Ok(
            await traktClient
                .GetDeviceAuthorizationStatusAsync(cancellationToken)
                .ConfigureAwait(false)
        );

    [HttpPost("disconnect")]
    public ActionResult Disconnect()
    {
        traktClient.Disconnect();
        return NoContent();
    }
}
