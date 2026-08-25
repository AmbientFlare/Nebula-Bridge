using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NebulaBridge.Providers;
using NebulaBridge.Services;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Decorators;

public sealed class MediaSourceManagerDecorator(
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    NebulaBridgeItemRepository repo,
    //Lazy<ISubtitleManager> subtitleManager,
    Lazy<NebulaBridgeManager> manager,
    Lazy<SubtitleProvider> subtitleProvider,
    IMediaSegmentManager mediaSegmentManager,
    IMemoryCache memoryCache
) : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ILogger<MediaSourceManagerDecorator> _log =
        log ?? throw new ArgumentNullException(nameof(log));
    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));
    private readonly KeyLock _lock = new();
    private readonly IMediaSegmentManager _mediaSegmentManager =
        mediaSegmentManager ?? throw new ArgumentNullException(nameof(mediaSegmentManager));
    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    private readonly Lazy<NebulaBridgeManager> _manager = manager;
    private readonly Lazy<SubtitleProvider> _subtitleProvider = subtitleProvider;
    private readonly IMemoryCache _memoryCache =
        memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user = null
    )
    {
        var manager = _manager.Value;
        _log.LogDebug("GetStaticMediaSources {Id}", item.Id);
        var ctx = _http.HttpContext;
        Guid userId;
        if (user != null)
        {
            userId = user.Id;
        }
        else
        {
            userId = Guid.Empty;
            ctx?.TryGetUserId(out userId);
        }

        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        if (
            (!cfg.EnableMixed && !IsNebulaBridgePlaybackItem(item))
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        }

        var uri = StremioUri.FromBaseItem(item);
        var actionName =
            ctx?.Items.TryGetValue("actionName", out var ao) == true ? ao as string : null;

        var allowSync = ctx?.IsInsertableAction() == true && userId != Guid.Empty;
        var video = item as Video;
        var cacheKey = Guid.TryParse(video?.PrimaryVersionId, out var id)
            ? id.ToString()
            : item.Id.ToString();

        if (userId != Guid.Empty)
        {
            cacheKey = $"{userId.ToString()}:{cacheKey}";
        }

        if (!allowSync)
        {
            _log.LogDebug(
                "GetStaticMediaSources not a sync-eligible call. action={Action} uri={Uri}",
                actionName,
                uri?.ToString()
            );
        }
        else if (uri is not null && !manager.HasStreamSync(cacheKey))
        {
            // Bug in web UI that calls the detail page twice. So that's why there's a lock.
            _lock
                .RunSingleFlightAsync(
                    item.Id,
                    async ct =>
                    {
                        _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);

                        // Prewarm subtitle cache in the background if NebulaBridge Subtitles
                        // is enabled for this library.
                        var libraryOptions = _libraryManager.GetLibraryOptions(item);
                        var subtitlePrewarmEnabled =
                            libraryOptions.SubtitleDownloadLanguages?.Length > 0
                            && !libraryOptions.DisabledSubtitleFetchers.Contains(
                                "Nebula Bridge Subtitles",
                                StringComparer.OrdinalIgnoreCase
                            );

                        if (subtitlePrewarmEnabled)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _subtitleProvider
                                        .Value.GetSubtitlesAsync(
                                            uri.ExternalId,
                                            uri.MediaType,
                                            CancellationToken.None
                                        )
                                        .ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _log.LogWarning(ex, "Subtitle prewarm failed for {Uri}", uri);
                                }
                            });
                        }

                        try
                        {
                            var count = await manager
                                .SyncStreams(item, userId, ct)
                                .ConfigureAwait(false);
                            manager.SetStreamSync(
                                cacheKey,
                                count > 0 ? null : TimeSpan.FromMinutes(2)
                            );
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Failed to sync streams");
                        }
                    }
                )
                .GetAwaiter()
                .GetResult();

            // refresh item
            libraryManager.GetItemById(item.Id);
        }

        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user).ToList();

        // we dont use jellyfins alternate versions crap. So we have to load it ourselves

        InternalItemsQuery query;
        var associationId = item.GetProviderId("Stremio");

        if (item.GetBaseItemKind() == BaseItemKind.Episode)
        {
            var episode = (Episode)item;
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                ParentId = episode.SeasonId,
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                IndexNumber = episode.IndexNumber,
            };
        }
        else
        {
            var associationUri = StremioUri.FromBaseItem(item);
            if (associationUri is null)
            {
                _log.LogDebug("No Stremio URI found for movie {ItemId}", item.Id);
                return sources;
            }

            associationId = associationUri.ExternalId;
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                HasAnyProviderId = new Dictionary<string, string>
                {
                    { "Stremio", associationUri.ExternalId },
                },
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
            };
        }

        var nebulabridgeSources = repo.GetItemList(query)
            .OfType<Video>()
            .Where(x =>
                x.IsNebulaBridge()
                && x.HasStreamTag()
                && (
                    userId == Guid.Empty
                    || (x.NebulaBridgeData<List<Guid>>("userIds")?.Contains(userId) ?? false)
                )
            )
            .OrderBy(x => x.NebulaBridgeData<int?>("index") ?? int.MaxValue)
            .Select(s =>
            {
                var k = GetVersionInfo(s, MediaSourceType.Grouping, user);

                if (user is not null)
                {
                    _inner.SetDefaultAudioAndSubtitleStreamIndices(item, k, user);
                }

                return k;
            })
            .ToList();

        _log.LogDebug(
            "Found {s} streams. UserId={Action} Nebula Bridge ID={Uri}",
            nebulabridgeSources.Count,
            userId,
            associationId
        );

        sources.AddRange(nebulabridgeSources);

        // The primary nebulabridge:// item is metadata-only. Never expose it as a fallback
        // media source: ffprobe cannot open that protocol, and doing so turns an ordinary
        // "no cached source available" result into a broken transcoding session.
        sources = sources.Where(source => !IsInternalStubPath(source.Path)).ToList();

        if (sources.Count == 0)
        {
            // An item that exists must never report zero media sources. Stock
            // DtoService indexes MediaSources[0] unguarded, so an empty list
            // throws ArgumentOutOfRangeException, which the API layer reports
            // as 400 — killing the whole listing, not just this item.
            //
            // Hand back a source Jellyfin knows is not open yet instead. It is
            // never probed here (RequiresOpening + SupportsProbing=false), so
            // ffprobe never sees the nebulabridge:// path; resolution happens
            // in OpenLiveStream when a client actually asks to play.
            _log.LogInformation(
                "No cached Nebula Bridge source for {ItemId}; returning deferred placeholder",
                item.Id
            );
            return [CreateDeferredSource(item, uri)];
        }

        if (sources.Count > 0)
            sources[0].Type = MediaSourceType.Default;

        return sources;
    }

    internal static bool IsInternalStubPath(string? path) =>
        path?.StartsWith("nebulabridge", StringComparison.OrdinalIgnoreCase) == true
        || path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) == true
        || path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) == true;

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
    {
        _inner.AddParts(providers);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        return _inner.GetMediaStreams(itemId);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
    {
        return _inner.GetMediaStreams(query).ToList();
    }

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) =>
        _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) =>
        _inner.GetMediaAttachments(query);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken ct
    )
    {
        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        var manager = _manager.Value;
        var ctx = _http.HttpContext;

        var sources = GetStaticMediaSources(item, enablePathSubstitution, user);

        Guid? mediaSourceId =
            ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true
            && idObj is string idStr
            && Guid.TryParse(idStr, out var fromCtx)
                ? fromCtx
                : (
                    item.IsPrimaryVersion()
                    && sources.Count > 0
                    && Guid.TryParse(sources[0].Id, out var fromSource)
                        ? fromSource
                        : null
                );

        _log.LogDebug(
            "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId}",
            item.Id,
            mediaSourceId
        );

        var selected = SelectByIdOrFirst(sources, mediaSourceId);
        if (selected is null)
            return sources;

        var owner = ResolveOwnerFor(selected, item);
        if (!IsNebulaBridgePlaybackItem(owner))
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        if (owner.IsPrimaryVersion() && owner.Id != item.Id)
        {
            sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(sources, mediaSourceId);
            if (selected is null)
                return sources;
        }

        if (NeedsProbe(selected))
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(owner);

            var segmentTask = _mediaSegmentManager.RunSegmentPluginProviders(
                owner,
                libraryOptions,
                false,
                ct
            );
            var metadataTask = ProbeMediaSourceAsync(owner, selected, ct);
            //  var subtitleTask = DownloadSubtitles((Video)owner, ct);

            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);
        }

        if (item.RunTimeTicks is null && selected.RunTimeTicks is not null)
        {
            item.RunTimeTicks = selected.RunTimeTicks;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }

        // Stub path after probing is done so the real URL is never sent to clients.
        // Force File protocol so clients proxy through Jellyfin instead of direct-playing.
        if (ctx?.GetActionName() == "GetPostedPlaybackInfo")
        {
            selected.Path = "/stub";
            selected.IsRemote = false;
            selected.Protocol = MediaProtocol.File;
        }

        return [selected];

        static MediaSourceInfo? SelectByIdOrFirst(IReadOnlyList<MediaSourceInfo> list, Guid? id)
        {
            if (!id.HasValue)
                return list.FirstOrDefault();

            var target = id.Value;

            return list.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.Id) && Guid.TryParse(s.Id, out var g) && g == target
                ) ?? list.FirstOrDefault();
        }

        static bool NeedsProbe(MediaSourceInfo s) =>
            (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true)
            || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;

        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            Guid.TryParse(s.ETag, out var g) ? libraryManager.GetItemById(g) ?? fallback : fallback;
    }

    private static bool IsNebulaBridgePlaybackItem(BaseItem item) =>
        item.HasStreamTag()
        || (item.Path?.StartsWith("nebulabridge://", StringComparison.OrdinalIgnoreCase) ?? false)
        || (item.Path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) ?? false);

    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken
    )
    {
        if (
            item.GetBaseItemKind() is BaseItemKind.Movie or BaseItemKind.Episode
            && IsNebulaBridgePlaybackItem(item)
        )
        {
            var sources = GetStaticMediaSources(item, enablePathSubstitution);
            var selected = sources.FirstOrDefault(source =>
                string.Equals(source.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase)
            );

            if (selected is not null && IsAssociatedSourceInfo(selected, item))
            {
                return Task.FromResult(selected);
            }

            if (
                Guid.TryParse(mediaSourceId, out var sourceId)
                && libraryManager.GetItemById(sourceId) is Video sourceItem
                && IsAssociatedStreamItemForPlayback(sourceItem, item)
            )
            {
                return Task.FromResult(
                    GetVersionInfo(sourceItem, MediaSourceType.Default)
                );
            }
        }

        return _inner.GetMediaSource(
            item,
            mediaSourceId,
            liveStreamId,
            enablePathSubstitution,
            cancellationToken
        );

        bool IsAssociatedSourceInfo(MediaSourceInfo source, BaseItem owner) =>
            Guid.TryParse(source.ETag, out var sourceId)
            && libraryManager.GetItemById(sourceId) is Video sourceItem
            && IsAssociatedStreamItemForPlayback(sourceItem, owner);

    }

    internal static bool IsAssociatedStreamItemForPlayback(Video source, BaseItem owner)
    {
        if (!source.HasStreamTag())
        {
            return false;
        }

        var sourceId = source.GetProviderId("Stremio");
        var ownerId = owner.GetProviderId("Stremio");
        return !string.IsNullOrWhiteSpace(sourceId)
            && string.Equals(sourceId, ownerId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<LiveStreamResponse> OpenLiveStream(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    )
    {
        var resolved = await TryOpenDeferredSourceAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return resolved ?? await _inner.OpenLiveStream(request, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a source this decorator handed out as a deferred placeholder.
    /// Returns null for any token it did not issue, so other providers are
    /// untouched.
    /// </summary>
    private async Task<LiveStreamResponse?> TryOpenDeferredSourceAsync(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    )
    {
        var token = request.OpenToken;
        if (token is null || !token.StartsWith(DeferredTokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryParseDeferredToken(token, out var itemId))
        {
            _log.LogWarning("Malformed deferred open token: {Token}", token);
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Item {itemId} no longer exists"
                )
            );
        }

        // The resolution that used to run during listing, now on the one action
        // where a viewer is already waiting.
        _log.LogInformation("Resolving deferred Nebula Bridge source for {ItemId}", itemId);
        await _manager
            .Value.SyncStreams(item, request.UserId, cancellationToken)
            .ConfigureAwait(false);

        // user is left null so the request's own HTTP context supplies the id,
        // exactly as it does for an ordinary listing.
        var resolved = GetStaticMediaSources(item, false, null)
            .FirstOrDefault(source => source.RequiresOpening != true);

        if (resolved is null)
        {
            _log.LogWarning("No Nebula Bridge source could be resolved for {ItemId}", itemId);
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No playable source could be resolved for {itemId}"
                )
            );
        }

        resolved.LiveStreamId = token;
        return new LiveStreamResponse(resolved);
    }

    public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStream(id, cancellationToken);

    public Task<
        Tuple<MediaSourceInfo, IDirectStreamProvider>
    > GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) =>
        _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken
    ) => await _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    public async Task<MediaSourceInfo> GetLiveStreamMediaInfo(
        string id,
        CancellationToken cancellationToken
    ) => await _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) =>
        _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(
        BaseItem item,
        MediaSourceInfo source,
        User user
    ) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken
    ) =>
        _inner.AddMediaInfoWithProbe(
            mediaSource,
            isAudio,
            cacheKey,
            addProbeDelay,
            isLiveStream,
            cancellationToken
        );

    /// <summary>
    /// Prefix identifying an open token this decorator issued.
    /// </summary>
    internal const string DeferredTokenPrefix = "nebulabridge|";

    /// <summary>
    /// A media source that stands in for an item whose real stream has not been
    /// resolved yet. Carries everything OpenLiveStream needs to resolve it later.
    /// </summary>
    /// <summary>
    /// Reads back the item id from a token issued by CreateDeferredSource.
    /// Returns false for a token this decorator did not issue.
    /// </summary>
    internal static bool TryParseDeferredToken(string? token, out Guid itemId)
    {
        itemId = Guid.Empty;

        if (token is null || !token.StartsWith(DeferredTokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = token.Split('|');
        return parts.Length >= 2 && Guid.TryParseExact(parts[1], "N", out itemId);
    }

    internal static MediaSourceInfo CreateDeferredSource(BaseItem item, StremioUri? uri)
    {
        var id = item.Id.ToString("N", CultureInfo.InvariantCulture);

        return new MediaSourceInfo
        {
            Id = id,
            ETag = id,
            Name = item.Name,
            Protocol = MediaProtocol.Http,

            // Never the nebulabridge:// path: ffprobe cannot open that protocol.
            Path = null,
            IsRemote = true,

            // These two keep the source away from ffprobe until it is opened.
            RequiresOpening = true,
            SupportsProbing = false,
            RequiresClosing = false,

            OpenToken = string.Create(
                CultureInfo.InvariantCulture,
                $"{DeferredTokenPrefix}{id}|{uri?.ExternalId}"
            ),

            Type = MediaSourceType.Default,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            RunTimeTicks = item.RunTimeTicks,
            MediaStreams = [],
            MediaAttachments = [],
        };
    }

    private MediaSourceInfo GetVersionInfo(
        BaseItem item,
        MediaSourceType type,
        User? user = null
    )
    {
        ArgumentNullException.ThrowIfNull(item);

        var streamName = item.NebulaBridgeData<string>("name");
        var streamDesc = item.NebulaBridgeData<string>("description");
        var bingeGroup = item.NebulaBridgeData<string>("bingeGroup");
        var richName = !string.IsNullOrEmpty(streamDesc)
            ? $"{streamName}\n{streamDesc}"
            : streamName;

        var info = new MediaSourceInfo
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            ETag = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Protocol = MediaProtocol.Http,
            MediaStreams = GetMediaStreamsWithExternalSubs(item),
            MediaAttachments = _inner.GetMediaAttachments(item.Id),
            Name = richName,
            Path = item.Path,
            RunTimeTicks = item.RunTimeTicks,
            Container = item.Container,
            Size = item.Size,
            Type = type,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            // just always say yes
            HasSegments = true,
            //HasSegments = MediaSegmentManager.HasSegments(item.Id)
        };

        // Remote media is probed directly at playback time and cached in memory. This avoids
        // temporarily changing the Jellyfin item to a local .strm file, which can cause Jellyfin
        // to record the tiny shortcut file's size while failing to retain the remote codecs.
        if (
            _memoryCache.TryGetValue<MediaSourceInfo>(ProbeCacheKey(item.Id), out var probed)
            && probed is not null
        )
        {
            var externalStreams = info.MediaStreams
                .Where(stream => stream.IsExternal)
                .ToList();
            info.Bitrate = probed.Bitrate;
            info.Container = probed.Container ?? info.Container;
            info.Formats = probed.Formats;
            info.MediaStreams = probed.MediaStreams
                .Concat(externalStreams)
                .GroupBy(stream => (stream.Index, stream.Type, stream.Path))
                .Select(group => group.First())
                .ToList();
            info.RunTimeTicks = probed.RunTimeTicks ?? info.RunTimeTicks;
            info.Size = probed.Size ?? info.Size;
            info.Timestamp = probed.Timestamp;
            info.Video3DFormat = probed.Video3DFormat;
            info.VideoType = probed.VideoType;
        }


        if (user is not null)
        {
            info.SupportsTranscoding = user.HasPermission(
                PermissionKind.EnableVideoPlaybackTranscoding
            );
            info.SupportsDirectStream = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
        }
        if (string.IsNullOrEmpty(info.Path))
        {
            info.Type = MediaSourceType.Placeholder;
        }

        if (item is Video video)
        {
            info.IsoType = video.IsoType;
            info.VideoType = video.VideoType;
            info.Video3DFormat = video.Video3DFormat;
            info.Timestamp = video.Timestamp;
            info.IsRemote = true;

            if (video.IsShortcut)
            {
                info.IsRemote = true;
                info.Path = video.ShortcutPath;
            }
        }

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    private static readonly HashSet<string> _subtitleExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "vtt",
        "srt",
        "ass",
        "ssa",
        "sub",
        "idx",
        "smi",
    };

    // Jellyfin's MediaInfoResolver.GetExternalStreamsAsync bails immediately when !video.IsFileProtocol
    // (stream items have http:// paths). This means external subtitle files saved to the internal
    // metadata folder are never discovered during library refresh and never written to the DB.
    // We work around this by scanning the metadata folder ourselves at playback time and merging
    // any matching subtitle files into the DB streams on the fly.
    private IReadOnlyList<MediaStream> GetMediaStreamsWithExternalSubs(BaseItem item)
    {
        var streams = _inner.GetMediaStreams(item.Id).ToList();

        var nebulabridgeFilename = item.NebulaBridgeData<string>("filename");
        if (string.IsNullOrEmpty(nebulabridgeFilename))
            return streams;

        var metaPath = item.GetInternalMetadataPath();
        if (!Directory.Exists(metaPath))
            return streams;

        var baseName = Path.GetFileNameWithoutExtension(nebulabridgeFilename);
        var existingPaths = new HashSet<string>(
            streams.Where(s => s.Path != null).Select(s => s.Path!),
            StringComparer.OrdinalIgnoreCase
        );

        var nextIndex = streams.Count > 0 ? streams.Max(s => s.Index) + 1 : 0;

        foreach (var file in Directory.EnumerateFiles(metaPath))
        {
            var fname = Path.GetFileName(file);

            // Must start with baseName + "."
            if (!fname.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            var ext = Path.GetExtension(fname).TrimStart('.');
            if (!_subtitleExtensions.Contains(ext))
                continue;

            if (existingPaths.Contains(file))
                continue;

            // Parse language from suffix: {baseName}.{lang}.{ext} or {baseName}.{lang}.{N}.{ext}
            var suffix = fname.Substring(baseName.Length + 1); // everything after "baseName."
            var parts = Path.GetFileNameWithoutExtension(suffix).Split('.');
            var langCode = parts.Length > 0 ? parts[0] : "und";

            streams.Add(
                new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    IsExternal = true,
                    IsExternalUrl = false,
                    SupportsExternalStream = true,
                    Path = file,
                    Language = langCode,
                    Codec = ext.ToLowerInvariant(),
                    Index = nextIndex++,
                    IsDefault = false,
                    IsForced = false,
                    IsHearingImpaired = false,
                }
            );

            existingPaths.Add(file);
        }

        return streams;
    }

    private async Task ProbeMediaSourceAsync(
        BaseItem owner,
        MediaSourceInfo mediaSource,
        CancellationToken ct
    )
    {
        try
        {
            _log.LogInformation("Probing remote stream for {Id}", owner.Id);
            await _inner
                .AddMediaInfoWithProbe(
                    mediaSource,
                    false,
                    // Jellyfin 10.11's disk-probe cache attempts to open the cache file before
                    // creating its parent directory. Keep the authoritative six-hour cache in
                    // IMemoryCache below and disable that fragile optional disk cache here.
                    string.Empty,
                    false,
                    false,
                    ct
                )
                .ConfigureAwait(false);

            _memoryCache.Set(
                ProbeCacheKey(owner.Id),
                mediaSource,
                TimeSpan.FromHours(6)
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stream probe failed for {Id}", owner.Id);
        }
    }

    private static string ProbeCacheKey(Guid itemId) => $"nebulabridge:probe:{itemId:N}";
}
