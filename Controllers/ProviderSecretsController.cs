using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge/provider-secrets")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class ProviderSecretsController : ControllerBase
{
    private static readonly string[] Providers =
    [
        "torbox",
        "trakt-client-id",
        "trakt-client-secret",
    ];

    [HttpGet]
    public ActionResult<IReadOnlyList<ProviderSecretStatus>> GetStatuses()
    {
        var cfg = NebulaBridgePlugin.Instance!.Configuration;
        return Ok(
            Providers.Select(provider => new ProviderSecretStatus(provider, HasKey(cfg, provider)))
        );
    }

    [HttpPut("{provider}")]
    public ActionResult Save([FromRoute] string provider, [FromBody] ProviderSecretRequest request)
    {
        if (!Providers.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Value))
        {
            return BadRequest("A non-empty replacement value is required.");
        }

        NebulaBridgePlugin.Instance!.UpdateProviderSecret(provider, request.Value, clear: false);
        return NoContent();
    }

    [HttpDelete("{provider}")]
    public ActionResult Clear([FromRoute] string provider)
    {
        if (!Providers.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        NebulaBridgePlugin.Instance!.UpdateProviderSecret(provider, null, clear: true);
        return NoContent();
    }

    private static bool HasKey(Config.PluginConfiguration cfg, string provider) => provider switch
    {
        "torbox" =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEBULA_BRIDGE_TORBOX_API_TOKEN"))
            || !string.IsNullOrWhiteSpace(cfg.TorBoxApiToken),
        "trakt-client-id" =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEBULA_BRIDGE_TRAKT_CLIENT_ID"))
            || !string.IsNullOrWhiteSpace(cfg.TraktClientId),
        "trakt-client-secret" =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEBULA_BRIDGE_TRAKT_CLIENT_SECRET"))
            || !string.IsNullOrWhiteSpace(cfg.TraktClientSecret),
        _ => false,
    };
}

public sealed record ProviderSecretStatus(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("hasKey")] bool HasKey
);

public sealed record ProviderSecretRequest(string Value);
