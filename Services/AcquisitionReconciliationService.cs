using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;

namespace NebulaBridge.Services;

/// <summary>
/// Replaces a Nebula-owned virtual video with its scanned local counterpart only after a
/// provider-identity match and idempotent per-user data transfer.  The imported file is never
/// touched by this recovery path.
/// </summary>
public sealed class AcquisitionReconciliationService(
    AcquisitionJobStore store,
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    AcquisitionActivityTracker activity,
    ILogger<AcquisitionReconciliationService> logger
) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcilePendingAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Nebula acquisition reconciliation pass failed"); }
            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task ReconcilePendingAsync(CancellationToken ct)
    {
        var jobs = await store.ReadAsync(ct).ConfigureAwait(false);
        foreach (var job in jobs.Where(job => job.State == AcquisitionJobState.Imported
            && job.ReconciliationState != AcquisitionReconciliationState.Reconciled))
        {
            ct.ThrowIfCancellationRequested();
            await ReconcileAsync(job.Id, ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> ReconcileAsync(Guid jobId, CancellationToken ct)
    {
        var initial = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == jobId);
        if (initial is null || activity.IsActive(initial.ItemId) || activity.IsActive(jobId)) return false;
        await using var itemTransition = await activity.AcquireExclusiveAsync(initial.ItemId, ct).ConfigureAwait(false);
        await using var transition = await activity.AcquireExclusiveAsync(jobId, ct).ConfigureAwait(false);
        var job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Id == jobId);
        if (job is null || job.State != AcquisitionJobState.Imported || string.IsNullOrWhiteSpace(job.FinalPath)
            || !File.Exists(job.FinalPath))
            return false;

        var virtualItem = libraryManager.GetItemById(job.ItemId);
        if (virtualItem is not Video virtualVideo || !virtualItem.IsNebulaBridge())
        {
            // A previous successful cleanup is indistinguishable from a manually removed source.
            // Reconcile it only when the ordinary scanned file still exists; never recreate a
            // virtual row and never call a missing source item "successful" prematurely.
            var localAtFinalPath = job.LocalItemId is { } localId
                ? libraryManager.GetItemById(localId) as Video
                : null;
            if (localAtFinalPath is null || localAtFinalPath.IsNebulaBridge()
                || !string.Equals(localAtFinalPath.Path, job.FinalPath, StringComparison.Ordinal))
            {
                await MarkAsync(job, AcquisitionReconciliationState.Pending, "virtual_item_missing", ct).ConfigureAwait(false);
                return false;
            }
            await MarkAsync(job, AcquisitionReconciliationState.Reconciled, null, ct).ConfigureAwait(false);
            return true;
        }

        var matches = FindLocalMatches(virtualVideo, job.FinalPath).ToArray();
        if (matches.Length != 1)
        {
            var error = matches.Length == 0 ? "local_item_pending_scan" : "local_item_ambiguous";
            await MarkAsync(job, AcquisitionReconciliationState.Pending, error, ct).ConfigureAwait(false);
            return false;
        }

        var local = matches[0];
        job = job with { LocalItemId = local.Id };
        await MarkAsync(job, AcquisitionReconciliationState.LocalItemMatched, null, ct).ConfigureAwait(false);
        foreach (var user in userManager.GetUsers())
        {
            ct.ThrowIfCancellationRequested();
            var source = userDataManager.GetUserData(user, virtualVideo);
            if (!HasPersistedState(source)) continue;
            var target = userDataManager.GetUserData(user, local) ?? new UserItemData { Key = local.Id.ToString("N") };
            if (!Merge(target, source!)) continue;
            userDataManager.SaveUserData(user, local, target, UserDataSaveReason.Import, ct);
        }

        await MarkAsync(job, AcquisitionReconciliationState.StateTransferred, null, ct).ConfigureAwait(false);
        // Delete exactly one bridge-owned video. Deleting an episode leaves its remote siblings,
        // season and series intact; Jellyfin handles empty parent cleanup normally.
        libraryManager.DeleteItem(virtualVideo, new DeleteOptions { DeleteFileLocation = false }, true);
        await MarkAsync(job, AcquisitionReconciliationState.Reconciled, null, ct).ConfigureAwait(false);
        return true;
    }

    internal static bool HasPersistedState(UserItemData? data) => data is not null
        && (data.Played || data.IsFavorite || data.PlaybackPositionTicks > 0 || data.PlayCount > 0 || data.LastPlayedDate is not null);

    /// <summary>Monotonic merge: watched/favourite can only gain, and resume never decreases.</summary>
    internal static bool Merge(UserItemData target, UserItemData source)
    {
        var changed = false;
        if (source.Played && !target.Played) { target.Played = true; changed = true; }
        if (source.IsFavorite && !target.IsFavorite) { target.IsFavorite = true; changed = true; }
        if (!target.Played && source.PlaybackPositionTicks > target.PlaybackPositionTicks)
        {
            target.PlaybackPositionTicks = source.PlaybackPositionTicks;
            changed = true;
        }
        if (source.PlayCount > target.PlayCount) { target.PlayCount = source.PlayCount; changed = true; }
        if (source.LastPlayedDate > target.LastPlayedDate) { target.LastPlayedDate = source.LastPlayedDate; changed = true; }
        return changed;
    }

    private IEnumerable<Video> FindLocalMatches(Video virtualVideo, string finalPath)
    {
        if (virtualVideo is Movie)
        {
            if (string.IsNullOrWhiteSpace(virtualVideo.GetProviderId(MetadataProvider.Tmdb))
                && string.IsNullOrWhiteSpace(virtualVideo.GetProviderId(MetadataProvider.Imdb))) return [];
            return libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                Recursive = true,
                ExcludeTags = [NebulaBridgeManager.StreamTag],
                IsDeadPerson = true,
            })
                .OfType<Movie>().Where(item => !item.IsNebulaBridge()
                    && MatchesFinalPath(item, finalPath)
                    && MatchesMovie((Movie)virtualVideo, item));
        }

        if (virtualVideo is not Episode source || source.ParentIndexNumber is not { } season || source.IndexNumber is not { } episode)
            return [];
        var sourceSeries = libraryManager.GetItemById(source.SeriesId) as Series;
        if (string.IsNullOrWhiteSpace(sourceSeries?.GetProviderId(MetadataProvider.Tvdb))
            && string.IsNullOrWhiteSpace(sourceSeries?.GetProviderId(MetadataProvider.Tmdb))) return [];
        return libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            ExcludeTags = [NebulaBridgeManager.StreamTag],
            IsDeadPerson = true,
        })
            .OfType<Episode>().Where(item => !item.IsNebulaBridge()
                && MatchesFinalPath(item, finalPath)
                && HasUsableSeriesIdentity(item)
                && libraryManager.GetItemById(item.SeriesId) is Series localSeries
                && MatchesEpisode(source, sourceSeries!, item, localSeries));
    }

    internal static bool MatchesMovie(Movie source, Movie local)
    {
        var tmdb = source.GetProviderId(MetadataProvider.Tmdb);
        var imdb = source.GetProviderId(MetadataProvider.Imdb);
        return (string.IsNullOrWhiteSpace(tmdb) || string.Equals(local.GetProviderId(MetadataProvider.Tmdb), tmdb, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(imdb) || string.Equals(local.GetProviderId(MetadataProvider.Imdb), imdb, StringComparison.OrdinalIgnoreCase))
            && (!string.IsNullOrWhiteSpace(tmdb) || !string.IsNullOrWhiteSpace(imdb));
    }

    internal static bool MatchesFinalPath(Video item, string finalPath)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(finalPath)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(finalPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static bool MatchesEpisode(Episode source, Series sourceSeries, Episode local, Series localSeries) =>
        source.ParentIndexNumber == local.ParentIndexNumber
        && source.IndexNumber == local.IndexNumber
        && (sourceSeries.GetProviderId(MetadataProvider.Tvdb) is not { Length: > 0 } tvdb
                || string.Equals(localSeries.GetProviderId(MetadataProvider.Tvdb), tvdb, StringComparison.OrdinalIgnoreCase))
        && (sourceSeries.GetProviderId(MetadataProvider.Tmdb) is not { Length: > 0 } tmdb
                || string.Equals(localSeries.GetProviderId(MetadataProvider.Tmdb), tmdb, StringComparison.OrdinalIgnoreCase))
        && (sourceSeries.GetProviderId(MetadataProvider.Tvdb) is { Length: > 0 } || sourceSeries.GetProviderId(MetadataProvider.Tmdb) is { Length: > 0 });

    internal static bool HasUsableSeriesIdentity(Episode episode) => episode.SeriesId != Guid.Empty;

    private async Task MarkAsync(AcquisitionJob job, AcquisitionReconciliationState state, string? error, CancellationToken ct)
    {
        _ = await store.MutateAsync(job.Id, current => current is null ? null : current with
        {
            LocalItemId = job.LocalItemId ?? current.LocalItemId,
            ReconciliationState = state,
            ReconciliationError = error,
            UpdatedUtc = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "reconciliation-state",
            job.Id,
            new Dictionary<string, object?>
            {
                ["state"] = state.ToString(),
                ["status"] = error,
            });
    }
}
