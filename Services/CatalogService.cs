using NebulaBridge.Config;

namespace NebulaBridge.Services;

public class CatalogService(
    NebulaBridgeStremioProviderFactory stremioFactory,
    NativeTraktClient traktClient
)
{
    public async Task<List<CatalogConfig>> GetCatalogsAsync(Guid userId)
    {
        var config = NebulaBridgePlugin.Instance!.Configuration;
        var provider = stremioFactory.Create(userId);
        List<CatalogConfig> catalogs = [];

        var manifest = provider is null ? null : await provider.GetManifestAsync();
        if (manifest?.Catalogs is null)
        {
            catalogs.AddRange(
                config.Catalogs.Where(c =>
                    !string.Equals(c.Source, "trakt", StringComparison.OrdinalIgnoreCase)
                )
            );
        }

        foreach (var mCatalog in manifest?.Catalogs ?? [])
        {
            if (!mCatalog.IsImportable())
                continue;

            var existing = config.Catalogs.FirstOrDefault(c =>
                c.Id == mCatalog.Id
                && c.Type == mCatalog.Type
                && !string.Equals(c.Source, "trakt", StringComparison.OrdinalIgnoreCase)
            );
            if (existing == null)
            {
                existing = new CatalogConfig
                {
                    Source = "stremio",
                    Id = mCatalog.Id,
                    Type = mCatalog.Type,
                    Name = mCatalog.Name,
                    Enabled = false,
                    MaxItems = 0, // max items to be imported from this catalog
                    CreateCollection = false,
                    Url = "",
                };
            }
            else
            {
                // Update basic info from manifest just in case
                existing.Name = mCatalog.Name;
            }
            catalogs.Add(existing);
        }

        foreach (var definition in traktClient.GetCatalogDefinitions())
        {
            var existing = config.Catalogs.FirstOrDefault(c =>
                c.Id == definition.Id
                && c.Type == definition.Type
                && string.Equals(c.Source, "trakt", StringComparison.OrdinalIgnoreCase)
            );
            if (existing is null)
            {
                existing = new CatalogConfig
                {
                    Source = "trakt",
                    Id = definition.Id,
                    Type = definition.Type,
                    Name = definition.Name,
                    Enabled = false,
                    MaxItems = 0,
                    CreateCollection = false,
                    Url = definition.Path,
                };
            }
            else
            {
                existing.Name = definition.Name;
                existing.Url = definition.Path;
            }

            catalogs.Add(existing);
        }

        if (config.EnableTraktCatalogs)
        {
            var nextEpisodes = config.Catalogs.FirstOrDefault(c =>
                string.Equals(c.Source, "trakt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Id, TraktNextEpisodesService.CatalogId, StringComparison.Ordinal)
            );
            nextEpisodes ??= new CatalogConfig
            {
                Source = "trakt",
                Id = TraktNextEpisodesService.CatalogId,
                Type = "series",
                Name = "Trakt Next Episodes",
                Enabled = true,
                MaxItems = 0,
                CreateCollection = false,
                Url = "sync/watched/shows",
            };
            catalogs.Add(nextEpisodes);
        }

        if (catalogs.Count == 0)
        {
            return config.Catalogs;
        }

        config.Catalogs = catalogs;

        // Save if we added new ones (optional, but good for persistence)
        NebulaBridgePlugin.Instance.SaveConfiguration();

        return config.Catalogs;
    }

    public void UpdateCatalogConfig(CatalogConfig updatedConfig)
    {
        var config = NebulaBridgePlugin.Instance!.Configuration;
        var existing = config.Catalogs.FirstOrDefault(c =>
            c.Id == updatedConfig.Id
            && c.Type == updatedConfig.Type
            && string.Equals(c.Source, updatedConfig.Source, StringComparison.OrdinalIgnoreCase)
        );

        if (existing != null)
        {
            existing.Enabled = updatedConfig.Enabled;
            existing.ShowOnHome = updatedConfig.ShowOnHome;
            existing.MaxItems = updatedConfig.MaxItems;
            existing.CreateCollection = updatedConfig.CreateCollection;
        }
        else
        {
            config.Catalogs.Add(updatedConfig);
        }

        NebulaBridgePlugin.Instance.SaveConfiguration();
    }

    public CatalogConfig? GetCatalogConfig(string id, string type)
    {
        return NebulaBridgePlugin.Instance!.Configuration.Catalogs.FirstOrDefault(c =>
            c.Id == id && c.Type == type
        );
    }
}
