using System.Collections.Concurrent;
using NebulaBridge.Config;
using NebulaBridge.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace NebulaBridge;

public class NebulaBridgePlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<NebulaBridgePlugin> _log;
    private readonly NebulaBridgeManager _manager;
    private ConcurrentDictionary<Guid, PluginConfiguration> UserConfigs { get; } = new();
    private readonly NebulaBridgeStremioProviderFactory _stremioFactory;
    public PalcoCacheService PalcoCache { get; } // Migrated Palco Cache Service

    public NebulaBridgePlugin(
        IApplicationPaths applicationPaths,
        NebulaBridgeManager manager,
        IXmlSerializer xmlSerializer,
        ILogger<NebulaBridgePlugin> log,
        NebulaBridgeStremioProviderFactory stremioFactory,
        PalcoCacheService palcoCache
    )
        : base(applicationPaths, xmlSerializer)
    {
        MigrateLegacyConfiguration(applicationPaths, xmlSerializer, log);
        Instance = this;
        _log = log;
        _manager = manager;
        _stremioFactory = stremioFactory;
        PalcoCache = palcoCache;
    }

    private static void MigrateLegacyConfiguration(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<NebulaBridgePlugin> log
    )
    {
        var currentPath = Path.Combine(
            applicationPaths.PluginConfigurationsPath,
            "NebulaBridge.xml"
        );
        var legacyPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "Gelato.xml");
        if (File.Exists(currentPath) || !File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            var legacy = (PluginConfiguration)
                xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), legacyPath);
            xmlSerializer.SerializeToFile(legacy, currentPath);
            log.LogInformation(
                "Migrated legacy plugin configuration to the Nebula Bridge identity."
            );
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Could not migrate the legacy plugin configuration; defaults will be used."
            );
        }
    }

    public static NebulaBridgePlugin? Instance { get; private set; }

    // Event fired when the plugin configuration is updated via UpdateConfiguration
    public static new event Action<PluginConfiguration>? ConfigurationChanged;

    public override string Name => "Nebula Bridge";
    public override Guid Id => Guid.Parse("e9d7c793-aee0-49b6-82c1-8ad583453663");
    public override string Description =>
        "Native media discovery, Trakt catalogs, and on-demand Jellyfin playback.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            // Use a plugin-specific SPA key. A generic "config" key can leave Jellyfin
            // restoring a stale cached view whose controls no longer have handlers.
            Name = "nebulabridge-config",
            EnableInMainMenu = true,
            EmbeddedResourcePath = prefix + ".Config.config.html",
        };
        yield return new PluginPageInfo
        {
            Name = "nebulabridge-config-js",
            EnableInMainMenu = false,
            EmbeddedResourcePath = prefix + ".Config.config.js",
        };
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var cfg = (PluginConfiguration)configuration;
        // The dashboard receives a redacted configuration object. Preserve secrets that can
        // only be changed through the elevated, write-only provider-secret API.
        cfg.TorBoxApiToken = Configuration.TorBoxApiToken;
        cfg.TraktClientId = Configuration.TraktClientId;
        cfg.TraktClientSecret = Configuration.TraktClientSecret;
        cfg.TraktAccessToken = Configuration.TraktAccessToken;
        cfg.TraktRefreshToken = Configuration.TraktRefreshToken;
        cfg.TraktTokenCreatedAt = Configuration.TraktTokenCreatedAt;
        cfg.TraktTokenExpiresIn = Configuration.TraktTokenExpiresIn;
        cfg.TraktConnectedUser = Configuration.TraktConnectedUser;
        foreach (var userConfig in cfg.UserConfigs)
        {
            var persisted = Configuration.UserConfigs.FirstOrDefault(item =>
                item.UserId == userConfig.UserId
            );
            if (persisted is null)
            {
                continue;
            }

            userConfig.LibraryPolicyCaptured = persisted.LibraryPolicyCaptured;
            userConfig.PreviousEnableAllFolders = persisted.PreviousEnableAllFolders;
            userConfig.PreviousEnabledFolderIds = [.. persisted.PreviousEnabledFolderIds];
        }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISABLE_P2P")))
        {
            cfg.P2PEnabled = false;
        }
        base.UpdateConfiguration(cfg);

        _manager.ClearCache();
        _stremioFactory.ClearCache();
        UserConfigs.Clear();

        // Notify subscribers that configuration changed
        try
        {
            ConfigurationChanged?.Invoke(cfg);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error while invoking ConfigurationChanged event");
        }
    }

    public void UpdateProviderSecret(string provider, string? value, bool clear)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        var secret = clear ? string.Empty : value?.Trim() ?? string.Empty;
        switch (normalized)
        {
            case "torbox":
                Configuration.TorBoxApiToken = secret;
                break;
            case "trakt-client-id":
                Configuration.TraktClientId = secret;
                break;
            case "trakt-client-secret":
                Configuration.TraktClientSecret = secret;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), "Unknown provider secret.");
        }

        SaveConfiguration();
        InvalidateRuntimeConfiguration();
    }

    public void InvalidateRuntimeConfiguration()
    {
        _manager.ClearCache();
        _stremioFactory.ClearCache();
        UserConfigs.Clear();
    }

    public PluginConfiguration GetConfig(Guid userId)
    {
        try
        {
            return UserConfigs.GetOrAdd(
                userId,
                _ =>
                {
                    var cfg = Configuration.GetEffectiveConfig(userId);
                    var stremio = _stremioFactory.Create(cfg);
                    cfg.Stremio = stremio;
                    cfg.MovieFolder = _manager.TryGetMovieFolder(cfg);
                    cfg.SeriesFolder = _manager.TryGetSeriesFolder(cfg);
                    return cfg;
                }
            );
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error getting config");
            return new PluginConfiguration();
        }
    }
}
