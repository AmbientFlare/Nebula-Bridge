using System.Diagnostics;
using System.Globalization;
using NebulaBridge.Config;
using NebulaBridge.Decorators;
using NebulaBridge.NativeSources;
using NebulaBridge.Services;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace NebulaBridge;

public sealed class NebulaBridgeManager(
    ILoggerFactory loggerFactory,
    IProviderManager provider,
    NebulaBridgeItemRepository repo,
    IFileSystem fileSystem,
    IMemoryCache memoryCache,
    IServerConfigurationManager serverConfig,
    ILibraryManager libraryManager,
    IDirectoryService directoryService,
    IApplicationPaths appPaths,
    NativeSourcePipeline nativeSourcePipeline,
    NativeStreamProxyRegistry nativeStreamProxyRegistry,
    NebulaBridgeMetadataService metadataService
)
{
    public const string StreamTag = "nebulabridge-stream";
    public const string LegacyStreamTag = "gelato-stream";
    public const string TreeSyncedTag = "nebulabridge-tree-synced";
    public const string LegacyTreeSyncedTag = "gelato-tree-synced";
    public const string DiscoveryTag = "nebulabridge-discovery";
    public const string CatalogTagPrefix = "nebulabridge-catalog:";
    public const string PromotedTag = "nebulabridge-promoted";

    private readonly ILogger<NebulaBridgeManager> _log = loggerFactory.CreateLogger<NebulaBridgeManager>();

    private int GetHttpPort()
    {
        var networkConfig = serverConfig.GetNetworkConfiguration();
        return networkConfig.InternalHttpPort;
    }

    public void SetStremioSubtitlesCache(Guid guid, List<StremioSubtitle> subs)
    {
        memoryCache.Set($"subs:{guid}", subs, TimeSpan.FromHours(1));
    }

    public List<StremioSubtitle>? GetStremioSubtitlesCache(Guid guid)
    {
        return memoryCache.Get<List<StremioSubtitle>>($"subs:{guid}");
    }

    public void SetStreamSync(string guid, TimeSpan? duration = null)
    {
        memoryCache.Set(
            $"streamsync:{guid}",
            guid,
            duration
                ?? TimeSpan.FromSeconds(NebulaBridgePlugin.Instance!.Configuration.StreamTTL)
        );
    }

    public bool HasStreamSync(string guid)
    {
        return memoryCache.TryGetValue($"streamsync:{guid}", out _);
    }

    public void SaveStremioMeta(Guid guid, StremioMeta meta)
    {
        // Search results are cheap summaries, but keeping their resolved hand-off for a week
        // makes a recently displayed result immediately insertable when the user returns.
        memoryCache.Set($"meta:{guid}", meta, TimeSpan.FromDays(7));
    }

    public StremioMeta? GetStremioMeta(Guid guid)
    {
        return memoryCache.TryGetValue($"meta:{guid}", out var value) ? value as StremioMeta : null;
    }

    public void RemoveStremioMeta(Guid guid)
    {
        memoryCache.Remove($"meta:{guid}");
    }

    public void ClearCache()
    {
        if (memoryCache is MemoryCache cache)
        {
            cache.Compact(1.0);
        }

        _log.LogDebug("Cache cleared");
    }

    private static void SeedFolder(string path)
    {
        Directory.CreateDirectory(path);
        var seed = Path.Combine(path, "stub.txt");
        if (!File.Exists(seed))
        {
            File.WriteAllText(
                seed,
                "This is a seed file created by Nebula Bridge so that library scans are triggered. Do not remove."
            );
        }
    }

    public Folder? TryGetMovieFolder(Guid userId)
    {
        return TryGetFolder(
            NebulaBridgePlugin.Instance!.Configuration.GetEffectiveConfig(userId).MoviePath
        );
    }

    public Folder? TryGetSeriesFolder(Guid userId)
    {
        return TryGetFolder(
            NebulaBridgePlugin.Instance!.Configuration.GetEffectiveConfig(userId).SeriesPath
        );
    }

    public Folder? TryGetMovieFolder(PluginConfiguration cfg)
    {
        return TryGetFolder(cfg.MoviePath);
    }

    public Folder? TryGetSeriesFolder(PluginConfiguration cfg)
    {
        return TryGetFolder(cfg.SeriesPath);
    }

    private Folder? TryGetFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            SeedFolder(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(
                "Nebula Bridge could not create the library seed file under {Path} ({FailureType}); continuing with the configured Jellyfin folder",
                path,
                ex.GetType().Name
            );
        }

        return repo.GetItemList(new InternalItemsQuery { IsDeadPerson = true, Path = path })
            .OfType<Folder>()
            .FirstOrDefault();
    }

    public Folder? TryGetFolderByPath(string path) => TryGetFolder(path);

    public Folder GetOrCreateDiscoveryFolder(Folder libraryRoot, CancellationToken ct)
    {
        var existing = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = libraryRoot.Id,
                    IncludeItemTypes = [BaseItemKind.Folder],
                    Recursive = false,
                    IsDeadPerson = true,
                }
            )
            .OfType<Folder>()
            .FirstOrDefault(folder =>
                string.Equals(folder.Name, "Discovery", StringComparison.OrdinalIgnoreCase)
                && folder.Tags?.Contains(DiscoveryTag, StringComparer.OrdinalIgnoreCase) == true
            );
        if (existing is not null)
        {
            return existing;
        }

        var path = $"{libraryRoot.Path}:nebulabridge-discovery";
        var folder = new Folder
        {
            Id = libraryManager.GetNewItemId(path, typeof(Folder)),
            Name = "Discovery",
            Path = path,
            ParentId = libraryRoot.Id,
            IsVirtualItem = false,
            Tags = [DiscoveryTag],
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow,
            DateLastSaved = DateTime.UtcNow,
            DateLastRefreshed = DateTime.UtcNow,
        };
        folder.PresentationUniqueKey = folder.CreatePresentationUniqueKey();
        libraryRoot.AddChild(folder);
        repo.SaveItems([folder], ct);
        _log.LogInformation(
            "Created discovery quarantine under {LibraryName} ({LibraryId})",
            libraryRoot.Name,
            libraryRoot.Id
        );
        return folder;
    }

    public async Task PromoteDiscoveryItemAsync(
        BaseItem item,
        Guid userId,
        CancellationToken ct,
        Folder? destination = null
    )
    {
        if (!item.HasDiscoveryTag())
        {
            return;
        }

        var target = destination
            ?? (item is Series ? TryGetSeriesFolder(userId) : TryGetMovieFolder(userId));
        if (target is null)
        {
            _log.LogWarning(
                "Discovery item {Name} could not be promoted because its destination library is unavailable",
                item.Name
            );
            return;
        }

        await PromoteToLibraryAsync(item, target, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Promoted discovery item {Name} ({ItemId}) into {LibraryName}",
            item.Name,
            item.Id,
            target.Name
        );
    }

    public async Task PromoteCatalogItemAsync(
        BaseItem item,
        Folder destination,
        CancellationToken ct
    )
    {
        if (item.Tags?.Any(tag =>
                tag.StartsWith(CatalogTagPrefix, StringComparison.OrdinalIgnoreCase)
            ) != true)
        {
            return;
        }

        await PromoteToLibraryAsync(item, destination, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Promoted catalog item {Name} ({ItemId}) into {LibraryName}",
            item.Name,
            item.Id,
            destination.Name
        );
    }

    private static async Task PromoteToLibraryAsync(
        BaseItem item,
        Folder target,
        CancellationToken ct
    )
    {
        item.Tags =
        [
            .. (item.Tags ?? [])
                .Where(tag => !string.Equals(tag, DiscoveryTag, StringComparison.OrdinalIgnoreCase))
                .Where(tag =>
                    !tag.StartsWith(CatalogTagPrefix, StringComparison.OrdinalIgnoreCase)
                ),
            PromotedTag,
        ];
        item.SetParent(target);
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
    }

    private BaseItem? Exist(StremioMeta meta, User? user = null)
    {
        var item = IntoBaseItem(meta);
        if (item?.ProviderIds is { Count: > 0 })
            return FindExistingItem(item, user);
        _log.LogWarning("Nebula Bridge: Missing provider ids, skipping");
        return null;
    }

    public BaseItem? FindExistingItem(BaseItem item, User? user = null)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [item.GetBaseItemKind()],
            HasAnyProviderId = item.ProviderIds,
            Recursive = true,
            ExcludeTags = [StreamTag, LegacyStreamTag],
            User = user,
            IsDeadPerson = true, // skip filter marker
        };

        return libraryManager
            .GetItemList(query)
            .FirstOrDefault(x =>
            {
                return x switch
                {
                    null => false,
                    Video v => !v.IsStream(),
                    _ => true,
                };
            });
    }

    /// <summary>
    /// Inserts metadata into the library. Skip if it already exists.
    /// </summary>
    public async Task<(BaseItem? Item, bool Created)> InsertMeta(
        Folder parent,
        StremioMeta meta,
        User? user,
        bool allowRemoteRefresh,
        bool refreshItem,
        bool queueRefreshItem,
        CancellationToken ct,
        string? identityScope = null
    )
    {
        var mediaType = meta.Type;
        BaseItem? existing;

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series))
        {
            _log.LogWarning("type {Type} is not valid, skipping", mediaType);
            return (null, false);
        }
        _log.LogDebug("inserting  {Name}", meta.Name);
        var baseItemKind = mediaType.ToBaseItem();
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(user?.Id ?? Guid.Empty);

        // load in full metadata if needed.
        if (
            allowRemoteRefresh
            && (
                meta.ImdbId is null
                || (
                    baseItemKind == BaseItemKind.Series
                    && (meta.Videos is null || meta.Videos.Count == 0)
                )
            )
        )
        {
            // do a precheck as loading metadata is expensive
            existing = FindExistingForInsert(meta, parent, user, identityScope);

            if (existing is not null)
            {
                _log.LogDebug(
                    "found existing {Kind}: {Id} for {Name}",
                    existing.GetBaseItemKind(),
                    existing.Id,
                    existing.Name
                );
                return (existing, false);
            }

            var lookupId = meta.ImdbId ?? meta.Id;
            var refreshedMeta = await metadataService
                .GetMetaAsync(cfg, lookupId, mediaType, ct)
                .ConfigureAwait(false);

            if (refreshedMeta is null)
            {
                _log.LogWarning(
                    "InsertMeta: no native or legacy metadata found for {Id} {Type}.",
                    lookupId,
                    mediaType
                );
                return (null, false);
            }

            meta = refreshedMeta;

            mediaType = meta.Type;
        }

        if (!meta.IsValid())
        {
            _log.LogWarning(
                "meta for {Id} is not valid {Name} , skipping",
                meta.Id,
                meta.GetName()
            );
            return (null, false);
        }

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series))
        {
            _log.LogWarning("type {Type} is not valid after refresh, skipping", mediaType);
            return (null, false);
        }

        existing = FindExistingForInsert(meta, parent, user, identityScope);

        if (existing is not null)
        {
            _log.LogDebug(
                "found existing {Kind}: {Id} for {Name}",
                existing.GetBaseItemKind(),
                existing.Id,
                existing.Name
            );
            return (existing, false);
        }

        await EnrichMetaAsync(meta, ct).ConfigureAwait(false);

        if (IntoBaseItem(meta) is not { } baseItem)
        {
            _log.LogWarning("failed to convert meta into base item for {Name}", meta.Name);
            return (null, false);
        }


        ApplyIdentityScope(baseItem, identityScope);

        if (mediaType == StremioMediaType.Movie)
        {
            baseItem = SaveItem(baseItem, parent);
            if (baseItem is null)
            {
                _log.LogWarning("InsertMeta: failed to create baseItem");
                return (null, false);
            }

            await baseItem
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }
        else
        {
            baseItem = await SyncSeriesTreesAsync(
                    cfg,
                    meta,
                    ct,
                    targetFolder: parent,
                    identityScope: identityScope
                )
                .ConfigureAwait(false);
        }

        if (baseItem is null)
        {
            _log.LogWarning("InsertMeta: failed to create {Type} for {Name}", mediaType, meta.Name);
            return (null, false);
        }

        if (refreshItem)
        {
            var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = false,
                ReplaceAllMetadata = false,
                ForceSave = true,
            };

            if (queueRefreshItem)
            {
                provider.QueueRefresh(baseItem.Id, options, RefreshPriority.High);
            }
            else
            {
                _ = RefreshFullItemSafelyAsync(baseItem, options);
            }
        }
        _log.LogDebug("inserted new {Kind}: {Name}", baseItem.GetBaseItemKind(), baseItem.Name);
        return (baseItem, true);
    }

    private BaseItem? FindExistingForInsert(
        StremioMeta meta,
        Folder parent,
        User? user,
        string? identityScope
    )
    {
        if (string.IsNullOrWhiteSpace(identityScope))
        {
            return Exist(meta, user);
        }

        var candidate = IntoBaseItem(meta);
        return candidate is null || candidate.ProviderIds.Count == 0
            ? null
            : GetByProviderIds(candidate.ProviderIds, candidate.GetBaseItemKind(), parent);
    }

    private void ApplyIdentityScope(BaseItem item, string? identityScope)
    {
        if (string.IsNullOrWhiteSpace(identityScope))
        {
            return;
        }

        var externalId = item.GetProviderId("Stremio") ?? item.Path;
        item.Path = $"nebulabridge://{identityScope}/{Uri.EscapeDataString(externalId)}";
        item.Id = libraryManager.GetNewItemId(item.Path, item.GetType());
        if (libraryManager.GetItemById(item.Id) is not null)
        {
            // A prior catalog copy may have been promoted into its durable bridge library.
            // Keep that item stable and allocate a new identity for the re-listed catalog copy.
            item.Id = Guid.NewGuid();
        }
        item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
    }

    private async Task RefreshFullItemSafelyAsync(
        BaseItem item,
        MetadataRefreshOptions options
    )
    {
        try
        {
            await provider
                .RefreshFullItem(item, options, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Background metadata refresh failed for {Name}", item.Name);
        }
    }

    private IEnumerable<BaseItem> FindByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind,
        Folder parent
    )
    {
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            ParentId = parent.Id,
            HasAnyProviderId = providerIds
                .Where(kvp =>
                    kvp.Key is nameof(MetadataProvider.Tmdb) or nameof(MetadataProvider.Tvdb)
                    || kvp.Key == nameof(MetadataProvider.TvRage)
                    || kvp.Key == "Stremio"
                    || kvp.Key == nameof(MetadataProvider.Imdb)
                )
                .ToDictionary(),
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            // skip filter marker
            IsDeadPerson = true,
        };

        foreach (var item in libraryManager.GetItemList(q))
        {
            yield return item;
        }
    }

    private BaseItem? GetByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind,
        Folder parent
    )
    {
        return FindByProviderIds(providerIds, kind, parent).FirstOrDefault();
    }

    /// <summary>
    /// Load streams and inserts them into the database keeping original
    /// sorting. We make sure to keep a one stable version based on primaryversionid
    /// </summary>
    /// <returns></returns>
    public async Task<int> SyncStreams(BaseItem item, Guid userId, CancellationToken ct)
    {
        _log.LogDebug($"SyncStreams for {item.Id}");
        var stopwatch = Stopwatch.StartNew();
        if (item is not Video video)
        {
            _log.LogWarning(
                "SyncStreams: item is not a Video type, itemType={ItemType}",
                item.GetType().Name
            );
            return 0;
        }

        if (video.IsStream())
        {
            _log.LogWarning("SyncStreams: item is a stream, skipping");
            return 0;
        }

        var isEpisode = video is Episode;
        var parent = video.GetParent() as Folder;
        parent ??= isEpisode ? null : TryGetMovieFolder(userId);
        if (parent is null)
        {
            _log.LogWarning("SyncStreams: no parent, skipping");
            return 0;
        }

        var uri = StremioUri.FromBaseItem(video);
        if (uri is null)
        {
            _log.LogError($"Unable to build Stremio URI for {video.Name}");
            return 0;
        }

        var streamProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (providerId, value) in video.ProviderIds)
        {
            streamProviderIds[providerId] = value;
        }
        streamProviderIds["Stremio"] = uri.ExternalId;

        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        var stremio = cfg.Stremio;
        var httpPort = GetHttpPort();
        var streams = new List<StremioStream>();
        if (stremio is not null)
        {
            try
            {
                streams = await stremio.GetStreamsAsync(uri).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    "Legacy Stremio source lookup failed ({FailureType}); continuing with native sources",
                    ex.GetType().Name
                );
            }
        }

        if (cfg.EnableNativeScraper && cfg.EnableNativeAggregation)
        {
            var nativeQuery = BuildNativeMediaQuery(video);
            var nativeStreams = await nativeSourcePipeline
                .ResolveAsync(nativeQuery, ct)
                .ConfigureAwait(false);
            streams.AddRange(
                nativeStreams.Select(source =>
                    new StremioStream
                    {
                        Url = nativeStreamProxyRegistry
                            .Register(source, httpPort)
                            .AbsoluteUri,
                        Name = source.Name,
                        Title = source.Name,
                        Description = $"Native source: {source.SourceId}",
                        BehaviorHints = new StremioBehaviorHints
                        {
                            VideoSize = source.SizeBytes,
                            // TorBox CDN paths are intentionally opaque and often have no
                            // extension. Preserve the selected torrent filename so Jellyfin can
                            // determine the container before the first playback probe.
                            Filename = !string.IsNullOrWhiteSpace(source.Filename)
                                ? source.Filename
                                : Path.GetFileName(source.DirectUrl?.AbsolutePath ?? string.Empty),
                        },
                    }
                )
            );
        }
        // Filter valid streams
        var acceptable = streams
            .Select(s =>
            {
                if (!s.IsValid())
                {
                    _log.LogWarning("Invalid stream, skipping {StreamName}", s.Name);
                    return null;
                }

                if (!cfg.P2PEnabled && s.IsTorrent())
                {
                    _log.LogDebug($"P2P stream, skipping {s.Name}");
                    return null;
                }

                return s;
            })
            .OfType<StremioStream>()
            .ToList();

        // Get existing streams
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie],
            HasAnyProviderId = streamProviderIds,
            Recursive = true,
            IsDeadPerson = true,
            //  IsVirtualItem = true,
        };

        var existingStreamItems = repo.GetItemList(query)
            .OfType<Video>()
            .Where(v => v.IsStream())
            .ToList();

        // Match stream rows by persisted NebulaBridge guid, not by volatile playback URL/path.
        var existingByGuid = new Dictionary<Guid, Video>();
        foreach (var existingItem in existingStreamItems)
        {
            var existingGuid = existingItem.NebulaBridgeData<Guid?>("guid");
            if (existingGuid is null || existingGuid == Guid.Empty)
            {
                // Strict guid matching: ignore rows without a persisted guid.
                continue;
            }

            if (!existingByGuid.TryAdd(existingGuid.Value, existingItem))
            {
                // Guard against bad historical data; don't fail sync on collisions.
                _log.LogWarning(
                    "Duplicate stream guid found during sync: {Guid}. Keeping first item id={FirstId}, ignoring item id={SecondId}",
                    existingGuid.Value,
                    existingByGuid[existingGuid.Value].Id,
                    existingItem.Id
                );
            }
        }

        var upsertedStreams = new List<Video>();

        for (var i = 0; i < acceptable.Count; i++)
        {
            var s = acceptable[i];
            var index = i + 1;
            var path = s.IsFile()
                ? s.Url
                : $"http://127.0.0.1:{httpPort}/nebulabridge/stream?ih={s.InfoHash}"
                    + (s.FileIdx is not null ? $"&idx={s.FileIdx}" : "")
                    + (
                        s.Sources is { Count: > 0 }
                            ? $"&trackers={Uri.EscapeDataString(string.Join(',', s.Sources))}"
                            : ""
                    );

            var streamGuid = s.GetGuid(uri.ExternalId);
            var isNewStreamItem = !existingByGuid.TryGetValue(streamGuid, out var existingStream);
            Video streamItem;

            if (isNewStreamItem)
            {
                streamItem =
                    isEpisode && video is Episode e
                        ? new Episode
                        {
                            //Id = libraryManager.GetNewItemId(path, typeof(Episode)),
                            SeriesId = e.SeriesId,
                            SeriesName = e.SeriesName,
                            SeasonId = e.SeasonId,
                            SeasonName = e.SeasonName,
                            IndexNumber = e.IndexNumber,
                            ParentIndexNumber = e.ParentIndexNumber,
                            PremiereDate = e.PremiereDate,
                        }
                        : new Movie
                        {
                            //Id = libraryManager.GetNewItemId(path, typeof(Movie))
                        };
                streamItem.Path = path;
                streamItem.Id = libraryManager.GetNewItemId(
                    $"{uri.ExternalId}\n{streamItem.Path}",
                    streamItem.GetType()
                );
            }
            else
            {
                streamItem = existingStream!;
            }

            streamItem.Name = video.Name;
            streamItem.Tags = [StreamTag];

            var locked = streamItem.LockedFields?.ToList() ?? [];
            if (!locked.Contains(MetadataField.Tags))
                locked.Add(MetadataField.Tags);
            streamItem.LockedFields = locked.ToArray();

            streamItem.ProviderIds = streamProviderIds;
            streamItem.RunTimeTicks = video.RunTimeTicks;
            streamItem.Size = s.BehaviorHints?.VideoSize;
            // Preserve Jellyfin-managed alternate-version links. The media-source decorator
            // exposes every cached row through the standard MediaSources array, and clearing
            // these links here made source-picker clients lose the relationship on refresh.
            streamItem.SetPrimaryVersionId(null);
            streamItem.PremiereDate = video.PremiereDate;
            streamItem.Path = path;
            // Alternate provider rows are an internal playback implementation detail. Keeping
            // them virtual prevents library-sync plugins (including Trakt) from treating every
            // source as another movie/episode while Nebula Bridge still resolves them explicitly.
            streamItem.IsVirtualItem = true;
            streamItem.SetParent(parent);

            var users = streamItem.NebulaBridgeData<List<Guid>>("userIds") ?? [];
            if (!users.Contains(userId))
            {
                users.Add(userId);
                streamItem.SetNebulaBridgeData("userIds", users);
            }

            streamItem.SetNebulaBridgeData("name", s.Name);
            streamItem.SetNebulaBridgeData("description", s.Description);
            if (!string.IsNullOrEmpty(s.BehaviorHints?.BingeGroup))
            {
                streamItem.SetNebulaBridgeData("bingeGroup", s.BehaviorHints.BingeGroup);
            }
            if (!string.IsNullOrEmpty(s.BehaviorHints?.Filename))
            {
                streamItem.SetNebulaBridgeData("filename", s.BehaviorHints.Filename);

                // A known container is required by Jellyfin's static stream endpoint. Remote
                // providers such as TorBox may return an opaque signed URL, so derive it from
                // the provider's real filename instead of relying on the URL path.
                var extension = Path.GetExtension(s.BehaviorHints.Filename).TrimStart('.');
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    streamItem.Container = extension.ToLowerInvariant();
                }
            }
            streamItem.SetNebulaBridgeData("index", index);
            streamItem.SetNebulaBridgeData("guid", streamGuid);
            // Keep map current so stale detection below uses the final upserted set.
            existingByGuid[streamGuid] = streamItem;

            upsertedStreams.Add(streamItem);
        }

        //upsertedStreams = SaveItems(upsertedStreams, (Folder)primary.GetParent()).Cast<Video>().ToList();
        repo.SaveItems(upsertedStreams, ct);

        var newIds = new HashSet<Guid>(upsertedStreams.Select(x => x.Id));
        var stale = existingByGuid
            .Values.Where(m =>
                !newIds.Contains(m.Id)
                && (m.NebulaBridgeData<List<Guid>>("userIds")?.Contains(userId) ?? false)
            )
            .ToList();

        foreach (var _item in stale)
        {
            var users = _item.NebulaBridgeData<List<Guid>>("userIds") ?? [];
            users.Remove(userId);
            _item.SetNebulaBridgeData("userIds", users);
        }

        var toDelete = stale
            .Where(item => item.NebulaBridgeData<List<Guid>>("userIds") is { Count: 0 })
            .ToList();
        var toSave = stale.Except(toDelete).ToList();

        foreach (var staleItem in toDelete)
        {
            try
            {
                libraryManager.DeleteItem(
                    staleItem,
                    new DeleteOptions { DeleteFileLocation = false },
                    true
                );
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Could not remove stale Nebula Bridge stream item {ItemId}",
                    staleItem.Id
                );
            }
        }

        repo.SaveItems(toSave, ct);
        stopwatch.Stop();

        _log.LogInformation(
            $"SyncStreams finished Nebula BridgeId={uri.ExternalId} userId={userId} duration={Math.Round(stopwatch.Elapsed.TotalSeconds, 1)}s streams={acceptable.Count}"
        );

        return acceptable.Count;
    }

    internal static NativeMediaQuery BuildNativeMediaQuery(Video video)
    {
        if (video is Episode episode)
        {
            var series = episode.Series;
            var title = series?.Name;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = episode.SeriesName;
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                title = video.Name;
            }

            var imdbId = series?.GetProviderId(MetadataProvider.Imdb);
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                imdbId = video.GetProviderId(MetadataProvider.Imdb);
            }
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                imdbId = ExtractImdbId(video.GetProviderId("Stremio"));
            }

            return new NativeMediaQuery(
                title,
                series?.ProductionYear ?? video.ProductionYear,
                episode.ParentIndexNumber,
                episode.IndexNumber,
                imdbId,
                series?.GetProviderId(MetadataProvider.Tmdb)
                    ?? video.GetProviderId(MetadataProvider.Tmdb),
                series?.GetProviderId(MetadataProvider.Tvdb)
                    ?? video.GetProviderId(MetadataProvider.Tvdb)
            );
        }

        return new NativeMediaQuery(
            video.Name,
            video.ProductionYear,
            video.ParentIndexNumber,
            video.IndexNumber,
            video.GetProviderId(MetadataProvider.Imdb),
            video.GetProviderId(MetadataProvider.Tmdb),
            video.GetProviderId(MetadataProvider.Tvdb)
        );
    }

    private static string? ExtractImdbId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        var candidate = separator < 0 ? value : value[..separator];
        return candidate.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            && candidate[2..].All(char.IsDigit)
            ? candidate
            : null;
    }

    /// <summary>
    /// We only check permissions cause jellyfin excludes remote items by default
    /// </summary>
    /// <param name="item"></param>
    /// <param name="user"></param>
    /// <returns></returns>
    public bool CanDelete(BaseItem item, User user)
    {
        var allCollectionFolders = libraryManager
            .GetUserRootFolder()
            .Children.OfType<Folder>()
            .ToList();

        return item.IsAuthorizedToDelete(user, allCollectionFolders);
    }

    public bool IsStremio(BaseItem item)
    {
        return item.IsNebulaBridge();
    }

    public async Task<BaseItem?> SyncSeriesTreesAsync(
        PluginConfiguration cfg,
        StremioMeta seriesMeta,
        CancellationToken ct,
        Series? existingSeries = null,
        Folder? targetFolder = null,
        string? identityScope = null
    )
    {
        var seriesRootFolder = targetFolder ?? cfg.SeriesFolder;

        Series series;

        if (existingSeries is not null)
        {
            // Local (non-nebulabridge) series — use as-is, no creation needed
            series = existingSeries;
        }
        else
        {
            // NebulaBridge series — create or find under the virtual folder
            if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
            {
                _log.LogWarning("seriesRootFolder null or empty for {SeriesId}", seriesMeta.Id);
                return null;
            }

            if (IntoBaseItem(seriesMeta) is not Series tmpSeries)
                return null;

            ApplyIdentityScope(tmpSeries, identityScope);

            if (tmpSeries.ProviderIds.Count == 0)
            {
                _log.LogWarning(
                    "No providers found for {SeriesId} {SeriesName}, skipping creation",
                    seriesMeta.Id,
                    seriesMeta.Name
                );
                return null;
            }

            if (
                GetByProviderIds(
                    tmpSeries.ProviderIds,
                    tmpSeries.GetBaseItemKind(),
                    seriesRootFolder
                )
                is not Series found
            )
            {
                tmpSeries.Id = tmpSeries.Id == Guid.Empty ? Guid.NewGuid() : tmpSeries.Id;

                var options = new MetadataRefreshOptions(directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = false,
                    ReplaceAllMetadata = true,
                    ForceSave = true,
                };

                tmpSeries.ParentId = seriesRootFolder.Id;
                await tmpSeries.RefreshMetadata(options, ct).ConfigureAwait(false);
                seriesRootFolder.AddChild(tmpSeries);
                await tmpSeries.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct);
                series = tmpSeries;
            }
            else
            {
                series = found;
            }
        }

        var stopwatch = Stopwatch.StartNew();

        // Group episodes by season
        var seasonGroups = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            .Where(e => e.Season.HasValue && (e.Episode.HasValue || e.Number.HasValue))
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Episode ?? e.Number)
            .GroupBy(e => e.Season!.Value)
            .ToList();

        if (seasonGroups.Count == 0)
        {
            _log.LogWarning("No valid episodes found for {SeriesId}", seriesMeta.Id);
            return null;
        }

        var existingSeasonsDict = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = [BaseItemKind.Season],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Season>()
            .Where(s => s.IndexNumber.HasValue)
            .GroupBy(s => s.IndexNumber!.Value)
            .Select(g =>
            {
                if (g.Count() > 1)
                {
                    _log.LogWarning(
                        "Duplicate seasons found for series {SeriesName} ({SeriesId})! Season {SeasonNum} exists {Count} times. IDs: {Ids}",
                        series.Name,
                        series.Id,
                        g.Key,
                        g.Count(),
                        string.Join(", ", g.Select(s => s.Id))
                    );
                }
                return g;
            })
            .ToDictionary(g => g.Key, g => g.First());

        // Fetch all existing episodes for this series in one query, grouped by season
        var existingEpisodesBySeason = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    AncestorIds = [series.Id],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Episode>()
            .Where(x => !x.IsStream() && x.IndexNumber.HasValue && x.ParentIndexNumber.HasValue)
            .GroupBy(e => e.ParentIndexNumber!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => e.IndexNumber!.Value).ToHashSet());

        var seasonsInserted = 0;
        var episodesInserted = 0;

        var newSeasons = new List<Season>();
        var allNewEpisodes = new List<Episode>();

        var seriesStremioId = series.GetProviderId("Stremio");
        var seriesPresentationKey = series.GetPresentationUniqueKey();

        foreach (var seasonGroup in seasonGroups)
        {
            ct.ThrowIfCancellationRequested();

            var seasonIndex = seasonGroup.Key;
            var seasonPath = $"{series.Path}:{seasonIndex}";

            if (!existingSeasonsDict.TryGetValue(seasonIndex, out var season))
            {
                _log.LogTrace(
                    "Creating series {SeriesName} season {SeasonIndex:D2}",
                    series.Name,
                    seasonIndex
                );
                var epMeta = seasonGroup.First();
                epMeta.Type = StremioMediaType.Episode;
                if (IntoBaseItem(epMeta) is not Episode episode)
                {
                    _log.LogWarning(
                        "Could not load base item as episode for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                season = new Season
                {
                    Id = libraryManager.GetNewItemId(seasonPath, typeof(Season)),
                    Name = $"Season {seasonIndex:D2}",
                    IndexNumber = seasonIndex,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = seasonPath,
                    DateLastRefreshed = DateTime.UtcNow,
                    SeriesPresentationUniqueKey = seriesPresentationKey,
                    DateModified = DateTime.UtcNow,
                    DateLastSaved = DateTime.UtcNow,
                    PremiereDate = episode.PremiereDate,
                    EndDate =
                        episode.PremiereDate ?? new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ParentId = series.Id,
                };

                var primary = seriesMeta.App_Extras?.SeasonPosters?.ElementAtOrDefault(seasonIndex);
                if (!string.IsNullOrWhiteSpace(primary))
                {
                    ProviderManagerDecorator.SetRemoteImage(
                        appPaths,
                        season,
                        ImageType.Primary,
                        null,
                        primary
                    );
                }

                season.SetProviderId("Stremio", $"{seriesStremioId}:{seasonIndex}");
                season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
                newSeasons.Add(season);
                seasonsInserted++;
            }

            // Look up existing episodes for this season from the pre-fetched dict
            var existingEpisodeNumbers = existingEpisodesBySeason.TryGetValue(seasonIndex, out var epNums)
                ? epNums
                : [];
            foreach (var epMeta in seasonGroup)
            {
                ct.ThrowIfCancellationRequested();

                var index = epMeta.Episode ?? epMeta.Number;

                // This should never happen due to earlier filtering, but kept for safety
                if (!index.HasValue)
                {
                    _log.LogWarning(
                        "Episode number missing for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                if (existingEpisodeNumbers.Contains(index.Value))
                {
                    _log.LogTrace(
                        "Episode {EpisodeName} already exists, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                _log.LogTrace(
                    "Processing episode {EpisodeName} with index {Index} for {SeriesName} season {SeasonIndex}",
                    epMeta.GetName(),
                    index,
                    series.Name,
                    season.IndexNumber
                );

                epMeta.Type = StremioMediaType.Episode;
                if (IntoBaseItem(epMeta) is not Episode episode)
                {
                    _log.LogWarning(
                        "Could not load base item as episode for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                episode.IndexNumber = index;
                episode.ParentIndexNumber = season.IndexNumber;
                episode.SeasonId = season.Id;
                episode.SeriesId = series.Id;
                episode.SeriesName = series.Name;
                episode.SeasonName = season.Name;
                episode.ParentId = season.Id;
                episode.SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey;
                episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();

                allNewEpisodes.Add(episode);
                episodesInserted++;
                _log.LogTrace("Created episode {EpisodeName}", epMeta.GetName());
            }
        }

        if (newSeasons.Count > 0)
            repo.SaveItems(newSeasons, ct);

        if (allNewEpisodes.Count > 0)
            repo.SaveItems(allNewEpisodes, ct);

        series.DateLastRefreshed = DateTime.UtcNow;
        series.DateModified = DateTime.UtcNow;
        repo.SaveItems([series], ct);

        stopwatch.Stop();

        _log.LogDebug(
            "Sync completed for {SeriesName}: {SeasonsInserted} season(s) and {EpisodesInserted} episode(s) in {Dur}",
            series.Name,
            seasonsInserted,
            episodesInserted,
            stopwatch.Elapsed.TotalSeconds
        );

        return series;
    }

    /// <summary>
    /// Pass 1: fixes EndDate on all nebulabridge media items (movies get TMDB digital release date,
    /// series/seasons/episodes get PremiereDate as EndDate).
    /// </summary>
    public async Task SyncReleaseDates(
        Guid userId,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null
    )
    {
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        var sentinel = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;
        const int chunkSize = 400;
        const int maxDegreeOfParallelism = 4;

        var needsEndDate = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes =
                    [
                        BaseItemKind.Movie,
                        BaseItemKind.Series,
                        BaseItemKind.Season,
                        BaseItemKind.Episode,
                    ]
                }
            )
            .Where(m => m.EndDate is null || m.EndDate >= sentinel || m.EndDate > now)
            .ToList();

        var total = needsEndDate.Count;
        var processed = 0;
        var totalSaved = 0;

        for (var chunkStart = 0; chunkStart < total; chunkStart += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = needsEndDate.Skip(chunkStart).Take(chunkSize).ToList();
            var chunkResults = new System.Collections.Concurrent.ConcurrentBag<BaseItem>();

            await Parallel
                .ForEachAsync(
                    chunk,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxDegreeOfParallelism,
                        CancellationToken = cancellationToken,
                    },
                    async (item, ct) =>
                    {
                        try
                        {
                            switch (item)
                            {
                                case Movie movie:
                                    {
                                        var meta = await metadataService
                                            .GetMetaAsync(cfg, movie, ct)
                                            .ConfigureAwait(false);
                                        if (meta is null)
                                            break;
                                        await EnrichMetaAsync(meta, ct).ConfigureAwait(false);
                                        var digital = meta.GetDigitalReleaseDate();
                                        var oneYearAgo = DateTime.UtcNow.AddYears(-1);
                                        movie.EndDate =
                                            digital
                                            ?? (
                                                movie.PremiereDate.HasValue
                                                && movie.PremiereDate.Value < oneYearAgo
                                                    ? movie.PremiereDate.Value
                                                    : sentinel
                                            );
                                        chunkResults.Add(movie);
                                        _log.LogDebug(
                                            "SyncReleaseDates: movie {Name} EndDate → {Date}",
                                            movie.Name,
                                            movie.EndDate?.ToString(
                                                "yyyy-MM-dd",
                                                CultureInfo.InvariantCulture
                                            )
                                        );
                                        break;
                                    }

                                case BaseItem other when other is Series or Season or Episode:
                                    other.EndDate = other.PremiereDate ?? sentinel;
                                    chunkResults.Add(other);
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(
                                ex,
                                "SyncReleaseDates: failed for {Name} ({Id})",
                                item.Name,
                                item.Id
                            );
                        }
                        finally
                        {
                            var current = Interlocked.Increment(ref processed);
                            if (total > 0)
                                progress?.Report(100.0 * current / total);
                        }
                    }
                )
                .ConfigureAwait(false);

            if (!chunkResults.IsEmpty)
            {
                var toSave = chunkResults.ToList();
                repo.SaveItems(toSave, cancellationToken);
                totalSaved += toSave.Count;
            }
        }

        _log.LogInformation(
            "SyncReleaseDates completed. EndDate fixed for {Count} item(s).",
            totalSaved
        );
    }

    /// <summary>
    /// Syncs series trees: fetches new episodes for all continuing series (nebulabridge + local),
    /// and extends local series trees for the first time if ExtendLocalSeriesTrees is enabled.
    /// </summary>
    public async Task SyncSeriesTrees(
        Guid userId,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null
    )
    {
        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        var nebulabridgeProviders = new Dictionary<string, string>
        {
            { "Stremio", string.Empty },
            { "stremio", string.Empty },
        };

        var continuingNebulaBridgeSeries = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series],
                    SeriesStatuses = [SeriesStatus.Continuing],
                    HasAnyProviderId = nebulabridgeProviders,
                }
            )
            .OfType<Series>()
            .ToList();

        var continuingLocalSeries = cfg.ExtendLocalSeriesTrees
            ? libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Series],
                        SeriesStatuses = [SeriesStatus.Continuing],
                    }
                )
                .OfType<Series>()
                .Where(s =>
                    !s.IsNebulaBridge()
                    && (
                        !string.IsNullOrWhiteSpace(s.GetProviderId("Imdb"))
                        || !string.IsNullOrWhiteSpace(s.GetProviderId("Tmdb"))
                    )
                )
                .ToList()
            : [];

        var continuingSeries = continuingNebulaBridgeSeries.Concat(continuingLocalSeries).ToList();

        var total = continuingSeries.Count;
        var i = 0;

        await Parallel.ForEachAsync(
            continuingSeries,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken,
            },
            async (series, ct) =>
            {
                try
                {
                    var meta = await metadataService
                        .GetMetaAsync(cfg, series, ct)
                        .ConfigureAwait(false);
                    if (meta is not null)
                    {
                        var isLocal = !series.IsNebulaBridge();
                        await SyncSeriesTreesAsync(
                                cfg,
                                meta,
                                ct,
                                existingSeries: isLocal ? series : null
                            )
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(
                        ex,
                        "SyncSeriesTrees: tree sync failed for {Name} ({Id})",
                        series.Name,
                        series.Id
                    );
                }
                finally
                {
                    if (total > 0)
                        progress?.Report(100.0 * Interlocked.Increment(ref i) / total);
                }
            }
        );

        _log.LogInformation(
            "SyncSeriesTrees: continuing series synced: {SeriesCount}.",
            continuingSeries.Count
        );

        if (cfg.ExtendLocalSeriesTrees)
        {
            await SyncLocalSeriesTreesAsync(cfg, cancellationToken, progress, i, total)
                .ConfigureAwait(false);
        }
        else
        {
            CleanVirtualTreeItems(cancellationToken);
        }
    }

    private async Task SyncLocalSeriesTreesAsync(
        PluginConfiguration cfg,
        CancellationToken ct,
        IProgress<double>? progress,
        int progressOffset,
        int progressTotal
    )
    {
        var localSeries = libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series] })
            .OfType<Series>()
            .Where(s =>
                !s.IsNebulaBridge()
                && s.Status != SeriesStatus.Continuing // continuing handled in pass 2
                && (
                    !string.IsNullOrWhiteSpace(s.GetProviderId("Imdb"))
                    || !string.IsNullOrWhiteSpace(s.GetProviderId("Tmdb"))
                )
                && !s.HasTreeSyncedTag()
            )
            .ToList();

        _log.LogInformation(
            "SyncSeriesTrees: {Count} local (non-nebulabridge, non-continuing) series to extend for the first time.",
            localSeries.Count
        );

        var total = progressTotal + localSeries.Count;
        var i = progressOffset;

        foreach (var series in localSeries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var meta = await metadataService.GetMetaAsync(cfg, series, ct).ConfigureAwait(false);
                if (meta is not null)
                {
                    await SyncSeriesTreesAsync(cfg, meta, ct, existingSeries: series)
                        .ConfigureAwait(false);

                    // Mark as synced so we skip on future runs
                    series.Tags = [.. (series.Tags ?? []), TreeSyncedTag];
                    repo.SaveItems([series], ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "SyncSeriesTrees: virtual tree sync failed for {Name} ({Id})",
                    series.Name,
                    series.Id
                );
            }
            finally
            {
                if (total > 0)
                    progress?.Report(100.0 * ++i / total);
            }
        }
    }

    public void CleanVirtualTreeItem(Series series, CancellationToken ct)
    {
        var allEpisodes = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    AncestorIds = [series.Id],
                }
            )
            .OfType<Episode>()
            .ToList();

        var virtualEpisodes = allEpisodes.Where(ep => ep.IsNebulaBridge()).ToList();

        if (virtualEpisodes.Count == 0)
            return;

        var virtualEpIds = virtualEpisodes.Select(e => e.Id).ToHashSet();
        var seasonsWithRemainingEpisodes = allEpisodes
            .Where(ep => !virtualEpIds.Contains(ep.Id))
            .Select(ep => ep.SeasonId)
            .ToHashSet();

        _log.LogDebug(
            "CleanVirtualTreeItem: removing {EpCount} virtual episodes from {SeriesName}.",
            virtualEpisodes.Count,
            series.Name
        );

        foreach (var ep in virtualEpisodes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                libraryManager.DeleteItem(ep, new DeleteOptions { DeleteFileLocation = false });
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "CleanVirtualTreeItem: failed to delete episode {Name} ({Id})",
                    ep.Name,
                    ep.Id
                );
            }
        }

        var allSeasons = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Season],
                    ParentId = series.Id,
                }
            )
            .OfType<Season>()
            .ToList();

        foreach (var season in allSeasons)
        {
            ct.ThrowIfCancellationRequested();
            if (seasonsWithRemainingEpisodes.Contains(season.Id))
                continue;

            try
            {
                libraryManager.DeleteItem(season, new DeleteOptions { DeleteFileLocation = false });
                _log.LogDebug(
                    "CleanVirtualTreeItem: deleted empty season {Name} ({Id})",
                    season.Name,
                    season.Id
                );
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "CleanVirtualTreeItem: failed to delete season {Name} ({Id})",
                    season.Name,
                    season.Id
                );
            }
        }

        series.Tags = series
            .Tags?.Where(t => !t.Equals(TreeSyncedTag, StringComparison.OrdinalIgnoreCase))
            .Where(t => !t.Equals(LegacyTreeSyncedTag, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        repo.SaveItems([series], ct);
    }

    private void CleanVirtualTreeItems(CancellationToken ct)
    {
        var localSeries = libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series] })
            .OfType<Series>()
            .Where(s => string.IsNullOrEmpty(s.GetProviderId("Stremio")))
            .ToList();

        foreach (var series in localSeries)
        {
            ct.ThrowIfCancellationRequested();
            CleanVirtualTreeItem(series, ct);
        }
    }

    private BaseItem? SaveItem(BaseItem item, Folder parent)
    {
        return SaveItems([item], parent).FirstOrDefault();
    }

    private List<BaseItem> SaveItems(IEnumerable<BaseItem> items, Folder parent)
    {
        var baseItems = items.ToList();
        foreach (var item in baseItems)
        {
            var now = DateTime.UtcNow;
            item.DateModified = now;
            item.DateLastRefreshed = now;
            item.DateLastSaved = now;

            if (item.Id == Guid.Empty)
            {
                item.Id = libraryManager.GetNewItemId(item.Path, item.GetType());
            }
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

            parent.AddChild(item);
        }

        repo.SaveItems(baseItems, CancellationToken.None);
        return baseItems;
    }

    public BaseItem? IntoBaseItem(StremioMeta meta)
    {
        BaseItem item;

        var id = meta.Id;

        switch (meta.Type)
        {
            case StremioMediaType.Series:
                item = new Series { };
                break;

            case StremioMediaType.Movie:
                item = new Movie { };
                break;

            case StremioMediaType.Episode:
                item = new Episode { };
                break;
            default:
                _log.LogWarning("unsupported type {type}", meta.Type);
                return null;
        }

        item.Name = meta.GetName();

        item.PremiereDate = meta.GetPremiereDate();

        // Always set EndDate so it's never NULL — NULL breaks MaxEndDate filtering (SQL NULL semantics).
        // Movies: use digital release date (TMDB type-4); sentinel 9999 means no digital date yet.
        // Exception: if PremiereDate is older than 1 year, use it as EndDate so old media without a
        // digital release date is never hidden by the unreleased filter.
        // Series/Season/Episode: use premiere/firstAired date; sentinel 9999 means not yet known.
        {
            var sentinel = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var oneYearAgo = DateTime.UtcNow.AddYears(-1);
            item.EndDate =
                meta.Type == StremioMediaType.Movie
                    ? meta.GetDigitalReleaseDate()
                        ?? (
                            item.PremiereDate.HasValue && item.PremiereDate.Value < oneYearAgo
                                ? item.PremiereDate.Value
                                : sentinel
                        )
                    : meta.GetPremiereDate() ?? sentinel;
        }

        item.ProductionYear = meta.GetYear();
        item.Path = $"nebulabridge://stub/{id}";

        // Provider IDs — skip for episodes since the parent series IMDB id is used there
        if (meta.Type is not StremioMediaType.Episode && !string.IsNullOrWhiteSpace(id))
        {
            var providerMappings = new (string Prefix, string Provider, bool StripPrefix)[]
            {
                ("tmdb:", nameof(MetadataProvider.Tmdb), true),
                ("tt", nameof(MetadataProvider.Imdb), false),
                ("anidb:", "AniDB", true),
                ("kitsu:", "Kitsu", true),
                ("mal:", "Mal", true),
                ("anilist:", "Anilist", true),
                ("tvdb:", nameof(MetadataProvider.Tvdb), true),
                ("tvmaze:", nameof(MetadataProvider.TvMaze), true),
            };

            foreach (var (prefix, prov, stripPrefix) in providerMappings)
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var providerId = stripPrefix ? id[prefix.Length..] : id;
                item.SetProviderId(prov, providerId);
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(meta.ImdbId))
            item.SetProviderId(MetadataProvider.Imdb, meta.ImdbId);
        if (!string.IsNullOrWhiteSpace(meta.TmdbId))
            item.SetProviderId(MetadataProvider.Tmdb, meta.TmdbId);
        if (!string.IsNullOrWhiteSpace(meta.TvdbId))
            item.SetProviderId(MetadataProvider.Tvdb, meta.TvdbId);
        if (!string.IsNullOrWhiteSpace(meta.TraktId))
            item.SetProviderId("Trakt", meta.TraktId);

        var stremioUri = new StremioUri(meta.Type, meta.ImdbId ?? id);
        item.SetProviderId("Stremio", stremioUri.ExternalId);

        item.Overview = meta.Description ?? meta.Overview;

        if (meta.ImdbRating.HasValue)
            item.CommunityRating = meta.ImdbRating;

        if (!string.IsNullOrWhiteSpace(meta.Country))
            item.ProductionLocations =
            [
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(meta.Country.ToLowerInvariant()),
            ];

        if (!string.IsNullOrWhiteSpace(meta.App_Extras?.Certification))
            item.OfficialRating = meta.App_Extras.Certification;

        if (meta.Type is StremioMediaType.Movie or StremioMediaType.Series)
        {
            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);

            var genres = (meta.Genres ?? meta.Genre) ?? [];
            item.Genres = genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        }

        if (item is Episode ep)
        {
            ep.IndexNumber = meta.Episode ?? meta.Number;
            ep.ParentIndexNumber = meta.Season;
            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                ep.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);
            if (!string.IsNullOrWhiteSpace(meta.Thumbnail))
                ep.SetProviderId("StremioThumb", meta.Thumbnail);
            var tvdbId = meta.TvdbEpisodeId();
            tvdbId ??= meta.TvdbId;
            if (tvdbId is not null)
                ep.SetProviderId(MetadataProvider.Tvdb, tvdbId);
        }

        if (item is Series series)
        {
            series.Status = meta.GetStatus() switch
            {
                StremioStatus.Continuing => SeriesStatus.Continuing,
                StremioStatus.Ended => SeriesStatus.Ended,
                StremioStatus.Upcoming => SeriesStatus.Unreleased,
                _ => null,
            };
        }

        item.IsVirtualItem = false;
        item.DateModified = DateTime.UtcNow;
        item.DateLastSaved = DateTime.UtcNow;
        item.DateCreated = DateTime.UtcNow;
        item.Id = libraryManager.GetNewItemId(item.Path, item.GetType());
        item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

        var primaryImage = meta.Poster ?? meta.Thumbnail;
        if (!string.IsNullOrWhiteSpace(primaryImage))
            ProviderManagerDecorator.SetRemoteImage(
                appPaths,
                item,
                ImageType.Primary,
                null,
                primaryImage
            );

        return item;
    }

    /// <summary>
    /// Enriches <paramref name="meta"/> with digital release dates from TMDB when
    /// the meta is a movie and <c>App_Extras.ReleaseDates</c> is not yet populated.
    /// </summary>
    public async Task EnrichMetaAsync(StremioMeta meta, CancellationToken ct)
    {
        if (meta.Type != StremioMediaType.Movie)
            return;

        if (meta.App_Extras?.ReleaseDates is not null)
            return;

        var configuration = NebulaBridgePlugin.Instance!.GetConfig(Guid.Empty);
        await metadataService
            .EnrichDigitalReleaseDateAsync(configuration, meta, ct)
            .ConfigureAwait(false);
    }
}
