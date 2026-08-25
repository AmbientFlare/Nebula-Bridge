using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.Controllers;

[ApiController]
[Route("nebulabridge/catalogs/home")]
[Authorize]
public sealed class CatalogHomeController(BridgeLibraryService libraries) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<CatalogHomeSection>> Get()
    {
        HttpContext.TryGetUserId(out var userId);
        var cfg = NebulaBridgePlugin.Instance!.Configuration;
        if (cfg.UserConfigs.FirstOrDefault(item => item.UserId == userId)?.NoNebulaBridge == true)
        {
            return Ok(Array.Empty<CatalogHomeSection>());
        }

        var rows = cfg.Catalogs
            .Where(catalog => catalog.Enabled && catalog.ShowOnHome)
            .GroupBy(BridgeLibraryService.GetCatalogLibraryKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var descriptor = group.Key == "next-episodes"
                    ? libraries.GetNextEpisodesDescriptor()
                    : libraries.GetCatalogDescriptor(group.First());
                return new CatalogHomeSection(
                    group.Key,
                    descriptor.Name,
                    libraries.GetVirtualFolderId(descriptor),
                    group.Select(catalog => catalog.Type).Distinct().ToArray()
                );
            })
            .OrderBy(section => section.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(rows);
    }
}

public sealed record CatalogHomeSection(
    string Key,
    string Name,
    Guid? LibraryId,
    IReadOnlyList<string> Types
);
