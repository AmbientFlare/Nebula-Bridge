using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

public sealed class TraktNextEpisodesService(
    NativeTraktClient trakt,
    ITraktSettingsProvider settingsProvider,
    NebulaBridgeMetadataService metadata,
    NebulaBridgeManager manager,
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    BridgeLibraryService bridgeLibraries,
    UserAccessService userAccess,
    ILogger<TraktNextEpisodesService> logger
)
{
    public const string CatalogId = "trakt-next-episodes";

    public async Task SyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var settings = settingsProvider.GetSettings();
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            logger.LogDebug("Trakt Next Episodes refresh skipped because no account is connected.");
            progress.Report(100);
            return;
        }

        var catalog = NebulaBridgePlugin.Instance!.Configuration.Catalogs.FirstOrDefault(item =>
            string.Equals(item.Id, CatalogId, StringComparison.Ordinal)
        );
        if (catalog?.Enabled != true)
        {
            logger.LogDebug("Trakt Next Episodes refresh skipped because its catalog is disabled.");
            progress.Report(100);
            return;
        }

        var descriptor = bridgeLibraries.GetNextEpisodesDescriptor();
        var folder = await bridgeLibraries
            .EnsureLibraryAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        if (folder is null)
        {
            logger.LogWarning("Trakt Next Episodes library is not ready; refresh deferred.");
            progress.Report(100);
            return;
        }

        await userAccess.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
        var watched = await trakt.GetWatchedEpisodesAsync(cancellationToken)
            .ConfigureAwait(false);
        var maxItems = catalog.MaxItems > 0
            ? catalog.MaxItems
            : NebulaBridgePlugin.Instance.Configuration.CatalogMaxItems;
        if (maxItems <= 0)
        {
            logger.LogWarning("Trakt Next Episodes has an invalid item limit; refresh skipped.");
            progress.Report(100);
            return;
        }
        var groups = watched
            .GroupBy(BuildShowKey)
            .Where(group => group.Key is not null)
            .Take(Math.Max(0, maxItems))
            .ToList();
        var retained = new HashSet<Guid>();
        var cfg = NebulaBridgePlugin.Instance.GetConfig(Guid.Empty);
        var tag = CatalogImportService.BuildCatalogTag(catalog);
        var linkedUser = ResolveLinkedUser(settings);
        for (var index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[index];
            var sample = group.First();
            var lookup = sample.ShowImdbId
                ?? (sample.ShowTmdbId is null ? null : $"tmdb:{sample.ShowTmdbId}")
                ?? (sample.ShowTvdbId is null ? null : $"tvdb:{sample.ShowTvdbId}");
            if (lookup is null)
            {
                continue;
            }

            var seriesMeta = await metadata
                .GetMetaAsync(cfg, lookup, StremioMediaType.Series, cancellationToken)
                .ConfigureAwait(false);
            if (seriesMeta?.Videos is not { Count: > 0 } allEpisodes)
            {
                continue;
            }

            var watchedEpisodes = group.Select(item => (item.Season, item.Episode)).ToHashSet();
            var next = SelectNextEpisode(allEpisodes, watchedEpisodes, DateTime.UtcNow);
            if (next is null)
            {
                continue;
            }

            // Persist the full known tree. The watched-state import below lets Jellyfin choose
            // the same next episode without reducing a series page to one artificial season.
            seriesMeta.Videos = allEpisodes;
            var (item, created) = await manager
                .InsertMeta(
                    folder,
                    seriesMeta,
                    null,
                    allowRemoteRefresh: false,
                    refreshItem: true,
                    queueRefreshItem: true,
                    cancellationToken,
                    descriptor.Key
                )
                .ConfigureAwait(false);
            if (item is not Series series)
            {
                continue;
            }

            if (!created && HasUserState(series))
            {
                var promotion = await bridgeLibraries
                    .EnsureLibraryAsync(
                        bridgeLibraries.GetPromotionDescriptor(series: true),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (promotion is not null)
                {
                    await manager
                        .PromoteCatalogItemAsync(series, promotion, cancellationToken)
                        .ConfigureAwait(false);
                    seriesMeta.Videos = allEpisodes;
                    await manager
                        .SyncSeriesTreesAsync(
                            cfg,
                            seriesMeta,
                            cancellationToken,
                            existingSeries: series,
                            targetFolder: promotion
                        )
                        .ConfigureAwait(false);
                    await userAccess.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    retained.Add(series.Id);
                    continue;
                }

                (item, _) = await manager
                    .InsertMeta(
                        folder,
                        seriesMeta,
                        null,
                        allowRemoteRefresh: false,
                        refreshItem: true,
                        queueRefreshItem: true,
                        cancellationToken,
                        descriptor.Key
                    )
                    .ConfigureAwait(false);
                series = item as Series ?? series;
            }

            await manager
                .SyncSeriesTreesAsync(
                    cfg,
                    seriesMeta,
                    cancellationToken,
                    existingSeries: series,
                    targetFolder: folder,
                    identityScope: descriptor.Key
                )
                .ConfigureAwait(false);
            if (linkedUser is not null)
            {
                ImportWatchedState(linkedUser, series, group, cancellationToken);
            }
            if (series.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) != true)
            {
                series.Tags = [.. (series.Tags ?? []), tag];
                await series
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
            }
            retained.Add(series.Id);
            progress.Report((index + 1) * 100.0 / Math.Max(1, groups.Count));
        }

        var stale = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = folder.Id,
                    IncludeItemTypes = [BaseItemKind.Series],
                    Recursive = false,
                    IsDeadPerson = true,
                }
            )
            .Where(item =>
                item.Tags?.Contains(tag, StringComparer.OrdinalIgnoreCase) == true
                && !retained.Contains(item.Id)
            )
            .ToList();
        foreach (var item in stale)
        {
            if (item is Series series && HasUserState(series))
            {
                var promotion = await bridgeLibraries
                    .EnsureLibraryAsync(
                        bridgeLibraries.GetPromotionDescriptor(series: true),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (promotion is not null)
                {
                    await manager
                        .PromoteCatalogItemAsync(series, promotion, cancellationToken)
                        .ConfigureAwait(false);
                    var fullMeta = await metadata
                        .GetMetaAsync(cfg, series, cancellationToken)
                        .ConfigureAwait(false);
                    if (fullMeta is not null)
                    {
                        await manager
                            .SyncSeriesTreesAsync(
                                cfg,
                                fullMeta,
                                cancellationToken,
                                existingSeries: series,
                                targetFolder: promotion
                            )
                            .ConfigureAwait(false);
                    }
                    continue;
                }
            }

            libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
        }

        logger.LogInformation(
            "Trakt Next Episodes refresh retained {Count} next-episode series",
            retained.Count
        );
        progress.Report(100);
    }

    private Jellyfin.Database.Implementations.Entities.User? ResolveLinkedUser(
        TraktSettings settings
    )
    {
        if (
            Guid.TryParse(settings.LinkedJellyfinUserId, out var linkedUserId)
            && userManager.GetUserById(linkedUserId) is { } linked
        )
        {
            return linked;
        }

        var users = userManager.GetUsers().ToList();
        if (users.Count == 1)
        {
            return users[0];
        }

        logger.LogWarning(
            "Trakt watched state was not imported because the connected account is not linked to one unambiguous Jellyfin user"
        );
        return null;
    }

    private void ImportWatchedState(
        Jellyfin.Database.Implementations.Entities.User user,
        Series series,
        IEnumerable<TraktWatchedEpisode> watched,
        CancellationToken cancellationToken
    )
    {
        var watchedByNumber = watched
            .GroupBy(item => (item.Season, item.Episode))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.LastWatchedAt).First()
            );
        var episodes = libraryManager
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
            .Where(episode =>
                episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
            );

        var imported = 0;
        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value);
            if (!watchedByNumber.TryGetValue(key, out var traktEpisode))
            {
                continue;
            }

            var data = userDataManager.GetUserData(user, episode);
            if (data is null || data.Played)
            {
                continue;
            }

            data.Played = true;
            data.PlayCount = Math.Max(data.PlayCount, traktEpisode.Plays);
            data.PlaybackPositionTicks = 0;
            data.LastPlayedDate = traktEpisode.LastWatchedAt?.UtcDateTime;
            userDataManager.SaveUserData(
                user,
                episode,
                data,
                UserDataSaveReason.Import,
                cancellationToken
            );
            imported++;
        }

        if (imported > 0)
        {
            logger.LogDebug(
                "Imported {Count} Trakt watched episodes into the persisted tree for {SeriesName}",
                imported,
                series.Name
            );
        }
    }

    private bool HasUserState(Series series)
    {
        var items = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    AncestorIds = [series.Id],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .Prepend(series)
            .ToList();
        return userManager.GetUsers().Any(user => items.Any(item =>
        {
            var data = userDataManager.GetUserData(user, item);
            return data is not null
                && (data.Played || data.IsFavorite || data.PlaybackPositionTicks > 0);
        }));
    }

    private static string? BuildShowKey(TraktWatchedEpisode item) =>
        item.ShowImdbId ?? item.ShowTmdbId ?? item.ShowTvdbId ?? item.ShowTraktId;

    internal static StremioMeta? SelectNextEpisode(
        IEnumerable<StremioMeta> episodes,
        IReadOnlySet<(int Season, int Episode)> watched,
        DateTime utcNow
    ) =>
        episodes
            .Where(item => item.Season is > 0 && (item.Episode ?? item.Number) is > 0)
            .Where(item =>
                !watched.Contains(
                    (item.Season!.Value, (item.Episode ?? item.Number)!.Value)
                )
            )
            .Where(item => item.GetPremiereDate() is { } airDate && airDate <= utcNow)
            .OrderBy(item => item.Season)
            .ThenBy(item => item.Episode ?? item.Number)
            .FirstOrDefault();
}
