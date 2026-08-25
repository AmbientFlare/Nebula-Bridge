using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using NebulaBridge.Services;

namespace NebulaBridge.Providers;

public sealed class NebulaBridgeSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly ILogger<NebulaBridgeSeriesProvider> _log;
    private readonly ILibraryManager _libraryManager;
    private readonly NebulaBridgeManager _manager;
    private readonly IProviderManager _provider;
    private readonly NebulaBridgeMetadataService _metadata;
    private readonly ConcurrentDictionary<Guid, DateTime> _syncCache = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(2);

    public NebulaBridgeSeriesProvider(
        ILogger<NebulaBridgeSeriesProvider> logger,
        ILibraryManager libraryManager,
        IProviderManager provider,
        NebulaBridgeManager manager,
        NebulaBridgeMetadataService metadata
    )
    {
        _log = logger;
        _libraryManager = libraryManager;
        _manager = manager;
        _provider = provider;
        _metadata = metadata;

        _provider.RefreshStarted += OnProviderManagerRefreshStarted;
    }

    public string Name => "Nebula Bridge";

    public int Order => 0;

    private string ProviderName => Name;

    private void OnProviderManagerRefreshStarted(
        object? sender,
        GenericEventArgs<BaseItem> genericEventArgs
    )
        => _ = SafeRefreshAsync(genericEventArgs.Argument);

    private async Task SafeRefreshAsync(BaseItem item)
    {
        try
        {
            await RefreshSeriesAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Refresh hook failed for {Name}", item.Name);
        }
    }

    private async Task RefreshSeriesAsync(BaseItem item)
    {
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(Guid.Empty);
        if (!IsEnabledForLibrary(item))
        {
            _log.LogTrace(
                "{ProviderName} not enabled for {InputName}",
                ProviderName,
                item.Name
            );
            return;
        }

        if (item is not Series series)
        {
            _log.LogTrace("{Name} is not a Series", item.Name);
            return;
        }

        // Check cache
        var now = DateTime.UtcNow;
        if (!_syncCache.TryGetValue(series.Id, out var lastSync))
        {
            lastSync = item.DateLastSaved;
        }

        if (now - lastSync < CacheExpiry)
        {
            _log.LogDebug(
                "Skipping {Name} - synced {Seconds} seconds ago",
                series.Name,
                (now - lastSync).TotalSeconds
            );
            return;
        }

        // Update cache before syncing
        _syncCache[series.Id] = now;

        var isLocal = !series.IsNebulaBridge();

        if (isLocal && !cfg.ExtendLocalSeriesTrees)
            return;

        // Guard against race condition: RefreshStarted fires inside RefreshMetadata (NebulaBridgeManager.cs:692)
        // before AddChild/UpdateToRepositoryAsync persist the stub (lines 693-694). If Stremio metadata
        // is cached the handler can complete and create a duplicate before the original is saved.
        if (_libraryManager.GetItemById(series.Id) is null)
            return;

        var seriesFolder = cfg.SeriesFolder;
        if (!isLocal && seriesFolder is null)
        {
            _log.LogWarning("No series folder found");
            return;
        }

        var meta = await _metadata
            .GetMetaAsync(cfg, series, CancellationToken.None)
            .ConfigureAwait(false);
        if (meta is null)
        {
            _log.LogWarning("Skipping {Name} - no metadata found", series.Name);
            return;
        }

        await _manager.SyncSeriesTreesAsync(
            cfg,
            meta,
            CancellationToken.None,
            existingSeries: isLocal ? series : null
        ).ConfigureAwait(false);
        _log.LogInformation("synced series tree for {Name}", series.Name);
    }

    public async Task<MetadataResult<Series>> GetMetadata(
        SeriesInfo info,
        CancellationToken cancellationToken
    )
    {
        var result = new MetadataResult<Series> { HasMetadata = false, QueriedById = true };

        var id = ResolveId(info.ProviderIds);
        if (id is null)
        {
            _log.LogDebug("Nebula Bridge series provider: no usable ID for {Name}", info.Name);
            return result;
        }

        var configuration = NebulaBridgePlugin.Instance!.GetConfig(Guid.Empty);

        StremioMeta? meta;
        try
        {
            meta = await _metadata
                .GetMetaAsync(configuration, id, StremioMediaType.Series, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Nebula Bridge series provider: failed to fetch meta for {Id}", id);
            return result;
        }

        if (meta is null || !meta.IsValid())
            return result;

        if (_manager.IntoBaseItem(meta) is not Series series)
            return result;

        series.ProviderIds.Remove("Stremio");
        result.HasMetadata = true;
        result.Item = series;
        MapPeople(meta, result);
        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeriesInfo searchInfo,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(searchInfo.Name))
            return [];

        try
        {
            var results = await _metadata
                .SearchAsync(
                    NebulaBridgePlugin.Instance!.GetConfig(Guid.Empty),
                    searchInfo.Name,
                    StremioMediaType.Series,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return results.Select(ToSearchResult);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Nebula Bridge series provider: search failed for {Name}", searchInfo.Name);
            return [];
        }
    }

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    )
    {
        throw new NotImplementedException();
    }

    private static void MapPeople(StremioMeta meta, MetadataResult<Series> result)
    {
        foreach (var member in meta.App_Extras?.Cast ?? [])
        {
            if (string.IsNullOrWhiteSpace(member.Name))
                continue;
            result.AddPerson(
                new PersonInfo
                {
                    Name = member.Name,
                    Role = member.Character,
                    Type = PersonKind.Actor,
                    ImageUrl = member.Photo,
                }
            );
        }

        var directors = meta.App_Extras?.Directors;
        if (directors is { Count: > 0 })
        {
            foreach (var d in directors)
            {
                if (!string.IsNullOrWhiteSpace(d.Name))
                    result.AddPerson(
                        new PersonInfo
                        {
                            Name = d.Name,
                            Type = PersonKind.Director,
                            ImageUrl = d.Photo,
                        }
                    );
            }
        }
        else if (!string.IsNullOrWhiteSpace(meta.Director))
        {
            foreach (
                var name in meta.Director.Split(
                    ',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
                )
            )
                result.AddPerson(new PersonInfo { Name = name, Type = PersonKind.Director });
        }

        var writers = meta.App_Extras?.Writers;
        if (writers is { Count: > 0 })
        {
            foreach (var w in writers)
            {
                if (!string.IsNullOrWhiteSpace(w.Name))
                    result.AddPerson(
                        new PersonInfo
                        {
                            Name = w.Name,
                            Type = PersonKind.Writer,
                            ImageUrl = w.Photo,
                        }
                    );
            }
        }
        else if (!string.IsNullOrWhiteSpace(meta.Writer))
        {
            foreach (
                var name in meta.Writer.Split(
                    ',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
                )
            )
                result.AddPerson(new PersonInfo { Name = name, Type = PersonKind.Writer });
        }
    }

    private static RemoteSearchResult ToSearchResult(StremioMeta meta) =>
        new()
        {
            Name = meta.GetName(),
            ProductionYear = meta.GetYear(),
            ImageUrl = meta.Poster ?? meta.Thumbnail,
            ProviderIds = meta.GetProviderIds(),
        };

    private static string? ResolveId(Dictionary<string, string> providerIds)
    {
        if (
            providerIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdb)
            && !string.IsNullOrWhiteSpace(imdb)
        )
            return imdb;
        if (
            providerIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdb)
            && !string.IsNullOrWhiteSpace(tmdb)
        )
            return $"tmdb:{tmdb}";
        return null;
    }

    private bool IsEnabledForLibrary(BaseItem item)
    {
        var series = item switch
        {
            Episode episode => episode.Series,
            Season season => season.Series,
            _ => item as Series,
        };

        if (series == null)
        {
            _log.LogTrace(
                "Given input is not in {@ValidTypes}: {Type}",
                new[] { nameof(Series), nameof(Season), nameof(Episode) },
                item.GetType()
            );
            return false;
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(series);
        var typeOptions = libraryOptions.GetTypeOptions(series.GetType().Name);

        // Check if this metadata fetcher is enabled in the library options
        return typeOptions?.MetadataFetchers?.Contains(Name, StringComparer.OrdinalIgnoreCase)
            ?? false;
    }
}
