using System.Text.Json.Serialization;
using System.Xml.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Config;

public class PluginConfiguration : BasePluginConfiguration
{
    public string MoviePath { get; set; } = Path.Combine(Path.GetTempPath(), "nebulabridge", "movies");
    public string SeriesPath { get; set; } = Path.Combine(Path.GetTempPath(), "nebulabridge", "series");
    public int StreamTTL { get; set; } = 3600;
    public int CatalogMaxItems { get; set; } = 100;
    public string Url { get; set; } = "";
    public bool EnableMixed { get; set; } = false;
    public bool ExtendLocalSeriesTrees { get; set; } = false;
    public bool FilterUnreleased { get; set; } = false;
    public int FilterUnreleasedBufferDays { get; set; } = 0;
    public bool DisableSourceCount { get; set; } = true;
    public bool P2PEnabled { get; set; } = false;
    public int P2PDLSpeed { get; set; } = 0;
    public int P2PULSpeed { get; set; } = 0;
    public string FFmpegAnalyzeDuration { get; set; } = "5M";
    public string FFmpegProbeSize { get; set; } = "40M";
    public bool CreateCollections { get; set; } = false;
    public int MaxCollectionItems { get; set; } = 100;
    public bool DisableSearch { get; set; } = false;
    public bool EnableJavaScriptInjection { get; set; } = false;
    public bool LazyImages { get; set; } = false;
    public bool EnableNativeScraper { get; set; } = false;
    public bool EnableNativeAggregation { get; set; } = false;
    public bool EnableTorBoxResolver { get; set; } = false;

    [JsonIgnore]
    public string TorBoxApiToken { get; set; } = string.Empty;
    public int NativeResolvedStreamLimit { get; set; } = 10;
    public List<string> EnabledNativeIndexerIds { get; set; } = [];
    public bool EnableRemoteIndexerCatalog { get; set; } = true;
    public string IndexerCatalogManifestUrl { get; set; } =
        NativeSources.IndexerCatalogDefaults.ManifestUrl;
    public string IndexerCatalogPublicKey { get; set; } =
        NativeSources.IndexerCatalogDefaults.PublicKeyBase64;
    public bool EnableTraktCatalogs { get; set; } = false;

    [JsonIgnore]
    public string TraktClientId { get; set; } = string.Empty;

    [JsonIgnore]
    public string TraktClientSecret { get; set; } = string.Empty;
    public string TraktRedirectUri { get; set; } = string.Empty;

    [JsonIgnore]
    public string TraktAccessToken { get; set; } = string.Empty;

    [JsonIgnore]
    public string TraktRefreshToken { get; set; } = string.Empty;
    public long TraktTokenCreatedAt { get; set; }
    public int TraktTokenExpiresIn { get; set; }
    public string TraktConnectedUser { get; set; } = string.Empty;
    public List<CatalogConfig> Catalogs { get; set; } = [];
    public List<UserConfig> UserConfigs { get; set; } = [];

    public string GetBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(Url))
            throw new InvalidOperationException("Nebula Bridge Url not configured.");

        var u = Url.Trim().TrimEnd('/');

        if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            u = u[..^"/manifest.json".Length];

        return u;
    }

    [JsonIgnore]
    [XmlIgnore]
    public NebulaBridgeStremioProvider? Stremio;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? MovieFolder;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? SeriesFolder;

    public PluginConfiguration GetEffectiveConfig(Guid userId)
    {
        var userConfig = UserConfigs.FirstOrDefault(u => u.UserId == userId);
        return userConfig is null ? CloneForRuntime() : userConfig.ApplyOverrides(this);
    }

    internal PluginConfiguration CloneForRuntime()
    {
        var clone = (PluginConfiguration)MemberwiseClone();
        clone.EnabledNativeIndexerIds = [.. EnabledNativeIndexerIds];
        clone.Catalogs = Catalogs.Select(c => c.Clone()).ToList();
        clone.UserConfigs = UserConfigs.Select(u => u.Clone()).ToList();
        clone.Stremio = null;
        clone.MovieFolder = null;
        clone.SeriesFolder = null;
        return clone;
    }
}

public class UserConfig
{
    public Guid UserId { get; set; }

    // Retained for backwards-compatible configuration migration. New installations use the
    // server-wide paths and the permission grid rather than per-user endpoints and paths.
    public string Url { get; set; } = "";
    public string MoviePath { get; set; } = "";
    public string SeriesPath { get; set; } = "";
    public bool DisableSearch { get; set; } = false;
    public bool NoNebulaBridge { get; set; } = false;
    public string Notes { get; set; } = "";

    [JsonIgnore]
    public bool LibraryPolicyCaptured { get; set; }

    [JsonIgnore]
    public bool PreviousEnableAllFolders { get; set; }

    [JsonIgnore]
    public List<Guid> PreviousEnabledFolderIds { get; set; } = [];

    /// <summary>
    /// Apply user overrides to base configuration - replaces all overridable fields
    /// </summary>
    public PluginConfiguration ApplyOverrides(PluginConfiguration baseConfig)
    {
        var effective = baseConfig.CloneForRuntime();
        if (!string.IsNullOrWhiteSpace(Url))
            effective.Url = Url;
        if (!string.IsNullOrWhiteSpace(MoviePath))
            effective.MoviePath = MoviePath;
        if (!string.IsNullOrWhiteSpace(SeriesPath))
            effective.SeriesPath = SeriesPath;
        effective.DisableSearch = DisableSearch || NoNebulaBridge;
        return effective;
    }

    internal UserConfig Clone()
    {
        var clone = (UserConfig)MemberwiseClone();
        clone.PreviousEnabledFolderIds = [.. PreviousEnabledFolderIds];
        return clone;
    }
}

public class NebulaBridgeStremioProviderFactory(IHttpClientFactory http, ILoggerFactory log)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        NebulaBridgeStremioProvider
    > _cache = new(StringComparer.OrdinalIgnoreCase);

    public NebulaBridgeStremioProvider? Create(Guid userId)
    {
        var cfg = NebulaBridgePlugin.Instance!.Configuration.GetEffectiveConfig(userId);
        return Create(cfg);
    }

    public NebulaBridgeStremioProvider? Create(PluginConfiguration cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Url))
        {
            return null;
        }

        var baseUrl = cfg.GetBaseUrl();
        return _cache.GetOrAdd(
            baseUrl,
            url => new NebulaBridgeStremioProvider(url, http, log.CreateLogger<NebulaBridgeStremioProvider>())
        );
    }

    public void ClearCache() => _cache.Clear();
}

public class CatalogConfig
{
    public string Source { get; set; } = "stremio";
    public string Id { get; set; } = "";
    public string Type { get; set; } = "movie";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public bool ShowOnHome { get; set; } = true;

    /// <summary>0 means "use global CatalogMaxItems".</summary>
    public int MaxItems { get; set; } = 0;
    public bool CreateCollection { get; set; } = false;
    public string Url { get; set; } = "";

    internal CatalogConfig Clone() => (CatalogConfig)MemberwiseClone();
}
