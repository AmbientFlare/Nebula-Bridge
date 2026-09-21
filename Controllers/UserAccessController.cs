using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NebulaBridge.Services;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge/user-access")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class UserAccessController(UserAccessService accessService) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<NebulaBridgeUserAccess>> Get() =>
        Ok(accessService.GetRows());

    [HttpPut]
    public async Task<ActionResult> Save(
        [FromBody] IReadOnlyList<NebulaBridgeUserAccess> rows,
        CancellationToken cancellationToken
    )
    {
        await accessService.SaveRowsAsync(rows, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
