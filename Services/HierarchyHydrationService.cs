using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

public enum HierarchyHydrationState
{
    NotHydrated,
    Hydrating,
    Hydrated,
    Stale,
    Failed,
}

public sealed record HierarchyHydrationResult(
    Guid ItemId,
    HierarchyHydrationState State,
    int SeasonCount,
    int EpisodeCount,
    string? Error = null
);

/// <summary>
/// Coordinates progressive creation of ordinary Jellyfin Season and Episode records.
/// Operations are single-flight per hierarchy level, and all work for a parent series is
/// serialized so series and season requests cannot race while updating the same tree.
/// </summary>
public sealed class HierarchyHydrationService(
    NebulaBridgeManager manager,
    NebulaBridgeMetadataService metadata,
    ILibraryManager libraryManager,
    IUserManager userManager,
    NebulaBridgeCache cache,
    ILogger<HierarchyHydrationService> logger
)
{
    private static readonly TimeSpan Freshness = TimeSpan.FromDays(7);
    private readonly KeyLock _seriesHydrationLock = new();
    private readonly ConcurrentDictionary<
        HydrationKey,
        Lazy<Task<HierarchyHydrationResult>>
    > _running = new();
    public HierarchyHydrationResult GetState(
        Guid itemId,
        Guid userId,
        bool episodes = false
    ) =>
        cache.Get<HierarchyHydrationResult>(
            StateCacheKey(new HydrationKey(itemId, episodes, userId))
        ) is { } state
            ? state
            : new HierarchyHydrationResult(
                itemId,
                HierarchyHydrationState.NotHydrated,
                0,
                0
            );

    public Task<HierarchyHydrationResult> HydrateSeriesAsync(
        Guid seriesId,
        Guid userId,
        CancellationToken cancellationToken,
        bool includeEpisodes = false
    ) =>
        RunSingleFlightAsync(
            new HydrationKey(seriesId, includeEpisodes, userId),
            ct => HydrateSeriesCoreAsync(seriesId, userId, includeEpisodes, ct),
            cancellationToken
        );

    public Task<HierarchyHydrationResult> HydrateSeasonAsync(
        Guid seasonId,
        Guid userId,
        CancellationToken cancellationToken
    ) =>
        RunSingleFlightAsync(
            new HydrationKey(seasonId, true, userId),
            ct => HydrateSeasonCoreAsync(seasonId, userId, ct),
            cancellationToken
        );

    private async Task<HierarchyHydrationResult> RunSingleFlightAsync(
        HydrationKey key,
        Func<CancellationToken, Task<HierarchyHydrationResult>> operation,
        CancellationToken callerCancellationToken
    )
    {
        var lazy = _running.GetOrAdd(
            key,
            _ =>
                new Lazy<Task<HierarchyHydrationResult>>(
                    () =>
                    {
                        SetState(
                            key,
                            new HierarchyHydrationResult(
                                key.ItemId,
                                HierarchyHydrationState.Hydrating,
                                0,
                                0
                            )
                        );
                        return RunAndPublishAsync(key, operation);
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
        );

        return await lazy.Value.WaitAsync(callerCancellationToken).ConfigureAwait(false);
    }

    private async Task<HierarchyHydrationResult> RunAndPublishAsync(
        HydrationKey key,
        Func<CancellationToken, Task<HierarchyHydrationResult>> operation
    )
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await operation(timeout.Token).ConfigureAwait(false);
            SetState(key, result);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hierarchy hydration failed for {ItemId}", key.ItemId);
            var failed = new HierarchyHydrationResult(
                key.ItemId,
                HierarchyHydrationState.Failed,
                0,
                0,
                ex.GetType().Name
            );
            SetState(key, failed);
            return failed;
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    private async Task<HierarchyHydrationResult> HydrateSeriesCoreAsync(
        Guid seriesId,
        Guid userId,
        bool includeEpisodes,
        CancellationToken cancellationToken
    )
    {
        var user = userManager.GetUserById(userId);
        if (user is null || libraryManager.GetItemById<Series>(seriesId, user) is not { } series)
        {
            return Failed(seriesId, "series_not_found");
        }

        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        if (!series.IsNebulaBridge() && !cfg.ExtendLocalSeriesTrees)
        {
            return Failed(seriesId, "not_nebula_content");
        }

        var alreadyHydrated = includeEpisodes
            ? series.HasTreeSyncedTag()
            : series.HasSeasonsHydratedTag();
        if (alreadyHydrated && IsFresh(series.DateLastRefreshed))
        {
            var current = Count(series.Id);
            // As with seasons, an empty tree that carries the tag is not hydrated.
            var reallyHydrated = includeEpisodes
                ? current.Episodes > 0
                : current.Seasons > 0;
            if (reallyHydrated)
            {
                logger.LogDebug(
                    "Series {SeriesId} was already hydrated (episodes={IncludeEpisodes})",
                    series.Id,
                    includeEpisodes
                );
                return new HierarchyHydrationResult(
                    series.Id,
                    HierarchyHydrationState.Hydrated,
                    current.Seasons,
                    current.Episodes
                );
            }

            logger.LogInformation(
                "Series {SeriesId} is tagged hydrated but the tree is empty (episodes={IncludeEpisodes}); re-hydrating",
                series.Id,
                includeEpisodes
            );
        }

        logger.LogInformation(
            "Series hydration started for {SeriesName} ({SeriesId}); episodes={IncludeEpisodes}",
            series.Name,
            series.Id,
            includeEpisodes
        );
        var meta = await metadata.GetMetaAsync(cfg, series, cancellationToken)
            .ConfigureAwait(false);
        if (meta is null)
        {
            return Failed(series.Id, "metadata_not_found");
        }

        BaseItem? hydrated = null;
        await _seriesHydrationLock
            .RunQueuedAsync(
                series.Id,
                async lockCancellationToken =>
                {
                    // A season request may have populated the tree while this request
                    // was resolving metadata. Re-check inside the parent-series lock.
                    var isHydrated = includeEpisodes
                        ? series.HasTreeSyncedTag()
                        : series.HasSeasonsHydratedTag();
                    var counts = Count(series.Id);
                    if (
                        isHydrated
                        && IsFresh(series.DateLastRefreshed)
                        && (includeEpisodes ? counts.Episodes > 0 : counts.Seasons > 0)
                    )
                    {
                        return;
                    }

                    hydrated = await manager
                        .SyncSeriesTreesAsync(
                            cfg,
                            meta,
                            lockCancellationToken,
                            existingSeries: series,
                            includeEpisodes: includeEpisodes
                        )
                        .ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (hydrated is null && !(includeEpisodes ? series.HasTreeSyncedTag() : series.HasSeasonsHydratedTag()))
        {
            return Failed(series.Id, "hierarchy_not_created");
        }

        var counts = Count(series.Id);
        logger.LogInformation(
            "Series hydration completed for {SeriesId}: {SeasonCount} season(s), {EpisodeCount} episode(s)",
            series.Id,
            counts.Seasons,
            counts.Episodes
        );
        return new HierarchyHydrationResult(
            series.Id,
            HierarchyHydrationState.Hydrated,
            counts.Seasons,
            counts.Episodes
        );
    }

    private async Task<HierarchyHydrationResult> HydrateSeasonCoreAsync(
        Guid seasonId,
        Guid userId,
        CancellationToken cancellationToken
    )
    {
        var user = userManager.GetUserById(userId);
        if (
            user is null
            || libraryManager.GetItemById<Season>(seasonId, user) is not { } season
            || libraryManager.GetItemById<Series>(season.SeriesId, user) is not { } series
            || !season.IndexNumber.HasValue
        )
        {
            return Failed(seasonId, "season_not_found");
        }

        var cfg = NebulaBridgePlugin.Instance!.GetConfig(userId);
        if (!series.IsNebulaBridge() && !cfg.ExtendLocalSeriesTrees)
        {
            return Failed(seasonId, "not_nebula_content");
        }

        // The tag alone is not proof. A season can carry it while holding no episodes --
        // an interrupted sync, or a second library copy of the same show that claimed the
        // episode records. Trusting the tag there pins the empty list for the whole
        // freshness window, so require the episodes to actually be present.
        if (season.HasEpisodesHydratedTag() && IsFresh(season.DateLastRefreshed))
        {
            var current = Count(series.Id, season.Id);
            if (current.Episodes > 0)
            {
                logger.LogDebug("Season {SeasonId} was already hydrated", season.Id);
                return new HierarchyHydrationResult(
                    season.Id,
                    HierarchyHydrationState.Hydrated,
                    current.Seasons,
                    current.Episodes
                );
            }

            logger.LogInformation(
                "Season {SeasonId} is tagged hydrated but holds no episodes; re-hydrating",
                season.Id
            );
        }

        logger.LogInformation(
            "Season hydration started for {SeriesName} season {SeasonNumber} ({SeasonId})",
            series.Name,
            season.IndexNumber,
            season.Id
        );
        var meta = await metadata.GetMetaAsync(cfg, series, cancellationToken)
            .ConfigureAwait(false);
        if (meta is null)
        {
            return Failed(season.Id, "metadata_not_found");
        }

        BaseItem? hydrated = null;
        await _seriesHydrationLock
            .RunQueuedAsync(
                series.Id,
                async lockCancellationToken =>
                {
                    if (
                        season.HasEpisodesHydratedTag()
                        && IsFresh(season.DateLastRefreshed)
                        && Count(series.Id, season.Id).Episodes > 0
                    )
                    {
                        return;
                    }

                    hydrated = await manager
                        .SyncSeriesTreesAsync(
                            cfg,
                            meta,
                            lockCancellationToken,
                            existingSeries: series,
                            includeEpisodes: true,
                            onlySeasonIndex: season.IndexNumber.Value
                        )
                        .ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (hydrated is null && Count(series.Id, season.Id).Episodes == 0)
        {
            return Failed(season.Id, "hierarchy_not_created");
        }

        var counts = Count(series.Id, season.Id);
        logger.LogInformation(
            "Season hydration completed for {SeasonId}: {EpisodeCount} episode(s)",
            season.Id,
            counts.Episodes
        );
        return new HierarchyHydrationResult(
            season.Id,
            HierarchyHydrationState.Hydrated,
            counts.Seasons,
            counts.Episodes
        );
    }

    private (int Seasons, int Episodes) Count(Guid seriesId, Guid? seasonId = null)
    {
        var seasons = libraryManager.GetItemList(
            new InternalItemsQuery
            {
                ParentId = seriesId,
                IncludeItemTypes = [BaseItemKind.Season],
                Recursive = false,
                IsDeadPerson = true,
            }
        ).Count;
        var episodeQuery = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = !seasonId.HasValue,
            IsDeadPerson = true,
        };
        if (seasonId.HasValue)
        {
            episodeQuery.ParentId = seasonId.Value;
        }
        else
        {
            episodeQuery.AncestorIds = [seriesId];
        }
        var episodes = libraryManager.GetItemList(episodeQuery).Count;
        return (seasons, episodes);
    }

    private static bool IsFresh(DateTime refreshed) =>
        refreshed >= DateTime.UtcNow.Subtract(Freshness);

    private static HierarchyHydrationResult Failed(Guid itemId, string error) =>
        new(itemId, HierarchyHydrationState.Failed, 0, 0, error);

    private void SetState(HydrationKey key, HierarchyHydrationResult state) =>
        cache.Set(StateCacheKey(key), state, TimeSpan.FromDays(1));

    private static string StateCacheKey(HydrationKey key) =>
        $"hierarchy:{key.UserId:N}:{key.ItemId:N}:{key.Episodes}";

    private readonly record struct HydrationKey(Guid ItemId, bool Episodes, Guid UserId);
}
