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
    /// <summary>Permanent destination for retained movies in an ordinary Jellyfin movie library.</summary>
    public string MovieImportPath { get; set; } = string.Empty;

    /// <summary>Permanent destination for retained episodes in an ordinary Jellyfin TV library.</summary>
    public string SeriesImportPath { get; set; } = string.Empty;
    public int StreamTTL { get; set; } = 3600;

    /// <summary>
    /// How long playback discovery waits for the indexer sweep before answering with the best
    /// release found so far. Indexers still running finish in the background and their answer is
    /// picked up by the next discovery for the same title.
    /// </summary>
    public int DiscoveryDeadlineSeconds { get; set; } = 25;

    /// <summary>
    /// Stremio stream addons (Torrentio, Comet, MediaFusion…) asked for a title's releases before
    /// the indexer sweep. They serve a pre-scraped database keyed by IMDb id and answer in well
    /// under a second, so when one of them knows the title playback does not wait on indexers.
    /// One addon base URL per entry; a configured-addon URL (with the addon's own options in the
    /// path) is fine as long as it does not carry a debrid key, since such streams are URLs and
    /// not hashes and are ignored.
    /// </summary>
    public List<string> StreamAddonUrls { get; set; } = [DefaultStreamAddonUrl];

    /// <summary>Public Torrentio; the addon everybody has, and the one this was measured against.</summary>
    public const string DefaultStreamAddonUrl = "https://torrentio.strem.fun";

    /// <summary>How long discovery waits for the stream addons before it falls back to the sweep alone.</summary>
    public int StreamAddonTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// How long the native stream proxy waits for a provider to answer an open (response headers)
    /// before it treats the provider as stalled, reports it unhealthy and fails over to an
    /// alternate route. Bounds the time Jellyfin's probe and every seek can hang on one provider.
    /// </summary>
    public int StreamOpenTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Warm source discovery ahead of the viewer: the next-up episode when a series or season is
    /// opened, and the episodes that follow one that starts playing. Runs one search at a time
    /// and yields to interactive discovery.
    /// </summary>
    public bool EnableDiscoveryPrefetch { get; set; } = true;

    /// <summary>How many episodes past the one playing are warmed.</summary>
    public int DiscoveryPrefetchDepth { get; set; } = 2;
    public int CatalogMaxItems { get; set; } = 100;
    public string Url { get; set; } = "";
    public bool EnableMixed { get; set; } = false;
    public bool ExtendLocalSeriesTrees { get; set; } = false;
    public bool FilterUnreleased { get; set; } = false;
    public int FilterUnreleasedBufferDays { get; set; } = 0;
    public bool DisableSourceCount { get; set; } = true;

    /// <summary>
    /// How long a raw indexer search result survives before it is swept up.
    /// These are meant to be looked at once, not kept.
    /// </summary>
    public int RawSearchLifetimeMinutes { get; set; } = 360;

    /// <summary>
    /// How long an untouched ordinary discovery item remains in its quarantine library.
    /// Meaningful user state promotes it instead of allowing the age sweep to delete it.
    /// </summary>
    public int DiscoveryLifetimeDays { get; set; } = 3;

    /// <summary>Temporary progressive playback bytes expire after useful access, independently of discovery metadata.</summary>
    public bool EnableLocalAcquisition { get; set; } = false;
    public int PlaybackCacheRetentionDays { get; set; } = 3;
    public int PlaybackCacheMinimumFreeSpaceMb { get; set; } = 2048;
    public bool EnableDedicatedLog { get; set; } = true;
    public NebulaLogVerbosity DedicatedLogVerbosity { get; set; } = NebulaLogVerbosity.Information;
    public string FFmpegAnalyzeDuration { get; set; } = "5M";
    public string FFmpegProbeSize { get; set; } = "40M";
    public bool CreateCollections { get; set; } = false;
    public int MaxCollectionItems { get; set; } = 100;
    public bool DisableSearch { get; set; } = false;
    public bool EnableJavaScriptInjection { get; set; } = false;
    public bool LazyImages { get; set; } = false;
    public bool EnableNativeScraper { get; set; } = false;
    public bool EnableNativeAggregation { get; set; } = false;
    /// <summary>
    /// Legacy single-provider switch. Retained so older saved configurations migrate into
    /// <see cref="DebridProviders"/>; runtime code reads the per-provider entry instead.
    /// </summary>
    public bool EnableTorBoxResolver { get; set; } = false;

    /// <summary>
    /// Per-provider debrid settings. Credentials are never stored here; they live in the
    /// write-only secret fields below or in environment variables.
    /// </summary>
    public List<DebridProviderConfig> DebridProviders { get; set; } = [];
    public string FlareSolverrUrl { get; set; } = string.Empty;

    [JsonIgnore]
    public string TorBoxApiToken { get; set; } = string.Empty;

    [JsonIgnore]
    public string RealDebridApiToken { get; set; } = string.Empty;
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
        clone.StreamAddonUrls = [.. StreamAddonUrls];
        clone.DebridProviders = DebridProviders.Select(p => p.Clone()).ToList();
        clone.Catalogs = Catalogs.Select(c => c.Clone()).ToList();
        clone.UserConfigs = UserConfigs.Select(u => u.Clone()).ToList();
        clone.Stremio = null;
        clone.MovieFolder = null;
        clone.SeriesFolder = null;
        return clone;
    }

    internal void NormalizeForRuntime()
    {
        StreamTTL = Math.Clamp(StreamTTL, 60, 86400);
        DiscoveryDeadlineSeconds = Math.Clamp(DiscoveryDeadlineSeconds, 5, 300);
        StreamAddonTimeoutSeconds = Math.Clamp(StreamAddonTimeoutSeconds, 1, 30);
        StreamAddonUrls = (StreamAddonUrls ?? [])
            .Select(url => url?.Trim() ?? string.Empty)
            .Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        StreamOpenTimeoutSeconds = Math.Clamp(StreamOpenTimeoutSeconds, 5, 120);
        DiscoveryPrefetchDepth = Math.Clamp(DiscoveryPrefetchDepth, 1, 3);
        CatalogMaxItems = Math.Clamp(CatalogMaxItems, 1, 5000);
        FilterUnreleasedBufferDays = Math.Clamp(FilterUnreleasedBufferDays, 0, 365);
        RawSearchLifetimeMinutes = Math.Clamp(RawSearchLifetimeMinutes, 5, 10080);
        DiscoveryLifetimeDays = Math.Clamp(DiscoveryLifetimeDays, 1, 365);
        PlaybackCacheRetentionDays = Math.Clamp(PlaybackCacheRetentionDays, 1, 365);
        PlaybackCacheMinimumFreeSpaceMb = Math.Clamp(PlaybackCacheMinimumFreeSpaceMb, 256, 1048576);
        if (!Enum.IsDefined(DedicatedLogVerbosity))
            DedicatedLogVerbosity = NebulaLogVerbosity.Information;
        NativeResolvedStreamLimit = Math.Clamp(NativeResolvedStreamLimit, 1, 20);
        EnabledNativeIndexerIds ??= [];
        DebridProviders ??= [];
        NormalizeDebridProviders();
        Catalogs ??= [];
        UserConfigs ??= [];
    }

    /// <summary>
    /// Collapses duplicate provider rows, clamps priorities, and migrates the legacy TorBox
    /// switch into a per-provider entry exactly once. The legacy flag is then kept in sync so
    /// older readers observe the same answer as the provider list.
    /// </summary>
    internal void NormalizeDebridProviders()
    {
        var normalized = new List<DebridProviderConfig>();
        foreach (var entry in DebridProviders)
        {
            var id = DebridProviderConfig.NormalizeId(entry.Id);
            if (id.Length == 0 || normalized.Any(item => item.Id == id))
                continue;
            normalized.Add(new DebridProviderConfig
            {
                Id = id,
                Enabled = entry.Enabled,
                Priority = Math.Clamp(entry.Priority, DebridProviderConfig.MinPriority, DebridProviderConfig.MaxPriority),
            });
        }

        if (normalized.All(item => item.Id != DebridProviderConfig.TorBoxId) && EnableTorBoxResolver)
        {
            normalized.Add(new DebridProviderConfig
            {
                Id = DebridProviderConfig.TorBoxId,
                Enabled = true,
                Priority = DebridProviderConfig.MinPriority,
            });
        }

        DebridProviders = normalized;
        EnableTorBoxResolver = normalized.Any(item => item.Id == DebridProviderConfig.TorBoxId && item.Enabled);
    }

    /// <summary>
    /// Returns the saved row for a provider, or the legacy TorBox switch mapped into a row when
    /// a configuration saved before the provider list existed has not been normalized yet.
    /// </summary>
    public DebridProviderConfig? GetDebridProvider(string providerId)
    {
        var id = DebridProviderConfig.NormalizeId(providerId);
        var entry = (DebridProviders ?? []).FirstOrDefault(item => DebridProviderConfig.NormalizeId(item.Id) == id);
        if (entry is null && id == DebridProviderConfig.TorBoxId && EnableTorBoxResolver)
        {
            return new DebridProviderConfig { Id = id, Enabled = true, Priority = DebridProviderConfig.MinPriority };
        }

        return entry;
    }
}

/// <summary>
/// One debrid provider's non-secret settings. The Id is the provider's stable identifier
/// (for example "torbox"); it is never a display name and never changes.
/// </summary>
public class DebridProviderConfig
{
    public const string TorBoxId = "torbox";
    public const int MinPriority = 1;
    public const int MaxPriority = 100;

    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = false;

    /// <summary>Lower numbers are preferred when more than one provider can serve a file.</summary>
    public int Priority { get; set; } = MaxPriority;

    internal DebridProviderConfig Clone() => (DebridProviderConfig)MemberwiseClone();

    public static string NormalizeId(string? id) => (id ?? string.Empty).Trim().ToLowerInvariant();
}

public enum NebulaLogVerbosity
{
    Information,
    Debug,
    Trace,
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
        // The server-wide switch is authoritative. A user may further disable search,
        // but must never be able to re-enable it for themselves.
        effective.DisableSearch = baseConfig.DisableSearch || DisableSearch || NoNebulaBridge;
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

    /// <summary>
    /// When false (the default) this source feeds the shared "Nebula Bridge — Movies" or
    /// "Nebula Bridge — Shows" library and is merged with every other source of its type.
    /// When true it gets a library of its own, Umbrella-style, for personal lists such as
    /// a watchlist that should stay separate.
    /// </summary>
    public bool SeparateLibrary { get; set; } = false;

    /// <summary>0 means "use global CatalogMaxItems".</summary>
    public int MaxItems { get; set; } = 0;
    public bool CreateCollection { get; set; } = false;
    public string Url { get; set; } = "";

    internal CatalogConfig Clone() => (CatalogConfig)MemberwiseClone();
}
