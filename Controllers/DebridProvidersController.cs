using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Controllers;

/// <summary>
/// Admin view of the debrid providers: enabled state, priority, credential presence, live health
/// and capabilities. Credentials are never returned; they are written through
/// <see cref="ProviderSecretsController"/> and only reported as present/absent here.
/// </summary>
[ApiController]
[Route("nebulabridge/debrid-providers")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class DebridProvidersController(DebridProviderOrchestrator orchestrator) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<DebridProviderStatus>> List() =>
        Ok(orchestrator.DescribeProviders().Select(ToStatus).ToList());

    [HttpPost("{providerId}/test")]
    public async Task<ActionResult<DebridProviderTestResult>> Test(
        [FromRoute] string providerId,
        CancellationToken cancellationToken
    )
    {
        if (orchestrator.TryGetProvider(providerId) is not { } provider)
        {
            return NotFound();
        }

        var failure = await provider.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            orchestrator.Health.ReportSuccess(provider.Id);
        }
        else
        {
            orchestrator.Health.ReportFailure(provider.Id, failure);
        }

        var snapshot = orchestrator.Health.Get(provider.Id);
        return Ok(
            new DebridProviderTestResult(
                provider.Id,
                failure is null,
                failure?.Reason,
                failure?.Message,
                snapshot.Health.ToString()
            )
        );
    }

    private static DebridProviderStatus ToStatus(DebridProviderDescription description) =>
        new(
            description.Id,
            description.Name,
            description.Enabled,
            description.Priority,
            description.HasCredential,
            description.CredentialFromEnvironment,
            description.Active,
            description.Health.ToString(),
            description.HealthUntilUtc,
            description.LastFailureReason,
            Enum.GetValues<DebridProviderCapabilities>()
                .Where(flag => flag != DebridProviderCapabilities.None && description.Capabilities.HasFlag(flag))
                .Select(flag => flag.ToString())
                .ToList()
        );
}

public sealed record DebridProviderStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("hasCredential")] bool HasCredential,
    [property: JsonPropertyName("credentialFromEnvironment")] bool CredentialFromEnvironment,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("health")] string Health,
    [property: JsonPropertyName("healthUntilUtc")] DateTimeOffset? HealthUntilUtc,
    [property: JsonPropertyName("lastFailureReason")] string? LastFailureReason,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities
);

public sealed record DebridProviderTestResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("health")] string Health
);
