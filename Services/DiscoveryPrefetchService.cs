using System.Diagnostics;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Services;

/// <summary>
/// Warms source discovery ahead of the viewer (latency plan §4.4). Opening a series or season
/// queues that user's next-up episode; starting an episode queues the ones that follow it. The
/// worker drains the queue one search at a time with the <see cref="IndexerSearchBudget.Complete"/>
/// budget, so a prefetched episode's later PlaybackInfo finds the full sweep already cached and
/// skips discovery. Interactive discovery always wins: the moment a viewer's own search starts,
/// the in-flight prefetch is cancelled and put back at the front of the queue, and the worker
/// waits until nobody is waiting on a search before it resumes.
/// </summary>
public sealed class DiscoveryPrefetchService(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    ISessionManager sessionManager,
    NebulaBridgeManager manager,
    DiscoveryActivity activity,
    BackgroundWork backgroundWork,
    ILogger<DiscoveryPrefetchService> logger
) : IHostedService
{
    private static readonly TimeSpan InteractivePoll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly DiscoveryPrefetchQueue _queue = new();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;

    internal int Pending => _queue.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart += OnPlaybackStart;
        activity.InteractiveStarted += Preempt;
        _ = backgroundWork.Run("discovery-prefetch", RunAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart -= OnPlaybackStart;
        activity.InteractiveStarted -= Preempt;
        return Task.CompletedTask;
    }

    /// <summary>A series or season page was opened for <paramref name="userId"/>.</summary>
    public void RequestNextUp(Guid seriesOrSeasonId, Guid userId)
    {
        if (!Enabled || userId == Guid.Empty)
            return;

        _queue.Enqueue(new PrefetchIntent(PrefetchTrigger.HierarchyOpened, seriesOrSeasonId, userId));
    }

    private static bool Enabled => NebulaBridgePlugin.Instance?.Configuration.EnableDiscoveryPrefetch == true;

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (!Enabled || e.Item is not Episode episode || !IsPrefetchable(episode))
            return;

        foreach (var user in e.Users)
            _queue.Enqueue(new PrefetchIntent(PrefetchTrigger.PlaybackStarted, episode.Id, user.Id));
    }

    private void Preempt()
    {
        lock (_gate)
        {
            _current?.Cancel();
        }
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            var intent = await _queue.DequeueAsync(shutdown).ConfigureAwait(false);
            try
            {
                await PrefetchAsync(intent, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discovery prefetch failed for {Intent}", intent);
            }
        }
    }

    private async Task PrefetchAsync(PrefetchIntent intent, CancellationToken shutdown)
    {
        if (userManager.GetUserById(intent.UserId) is not { } user)
            return;

        foreach (var episode in ResolveTargets(intent, user))
        {
            var cacheKey = NebulaBridgeManager.StreamSyncKey(episode, intent.UserId);
            if (manager.HasStreamSync(cacheKey))
                continue;

            await WaitForQuietAsync(shutdown).ConfigureAwait(false);
            // The interactive search we waited out may have been for this very episode.
            if (manager.HasStreamSync(cacheKey))
                continue;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            lock (_gate)
            {
                _current = cts;
                // An interactive search that began between the quiet check and this point has
                // already fired its event; do not let it wait on us.
                if (activity.InteractiveCount > 0)
                    cts.Cancel();
            }

            var stopwatch = Stopwatch.StartNew();
            Log("prefetch-start", episode, intent);
            try
            {
                var outcome = await manager
                    .SyncStreams(episode, intent.UserId, cts.Token, IndexerSearchBudget.Complete)
                    .ConfigureAwait(false);
                manager.SetStreamSync(
                    cacheKey,
                    outcome.Count > 0 && outcome.Complete ? null : TimeSpan.FromMinutes(2)
                );
                Log(
                    "prefetch-complete",
                    episode,
                    intent,
                    new Dictionary<string, object?>
                    {
                        ["count"] = outcome.Count,
                        ["complete"] = outcome.Complete,
                        ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    });
            }
            catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
            {
                Log(
                    "prefetch-preempted",
                    episode,
                    intent,
                    new Dictionary<string, object?> { ["elapsedMs"] = stopwatch.ElapsedMilliseconds });
                _queue.Requeue(intent);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discovery prefetch failed for {ItemId}", episode.Id);
            }
            finally
            {
                lock (_gate)
                {
                    _current = null;
                }

                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Waits until no interactive discovery is running, then a little longer so a viewer who is
    /// clicking through several titles is not interleaved with indexer work of ours.
    /// </summary>
    private async Task WaitForQuietAsync(CancellationToken shutdown)
    {
        while (true)
        {
            var waited = false;
            while (activity.InteractiveCount > 0)
            {
                waited = true;
                await Task.Delay(InteractivePoll, shutdown).ConfigureAwait(false);
            }

            if (!waited)
                return;

            await Task.Delay(Settle, shutdown).ConfigureAwait(false);
            if (activity.InteractiveCount == 0)
                return;
        }
    }

    private IReadOnlyList<Episode> ResolveTargets(
        PrefetchIntent intent,
        Jellyfin.Database.Implementations.Entities.User user
    )
    {
        var item = libraryManager.GetItemById(intent.ItemId);
        switch (intent.Trigger)
        {
            case PrefetchTrigger.HierarchyOpened:
                {
                    // A season narrows the choice to its own episodes: the viewer went there on
                    // purpose. A series uses the whole tree.
                    BaseItem? scope = item is Season or Series ? item : null;
                    var series = item switch
                    {
                        Season season => season.Series ?? libraryManager.GetItemById(season.SeriesId) as Series,
                        Series s => s,
                        _ => null,
                    };
                    if (scope is null || series is null || !series.IsNebulaBridge())
                        return [];

                    var episodes = EpisodesUnder(scope);
                    var resumable = episodes
                        .Select(episode => (Episode: episode, Data: userDataManager.GetUserData(user, episode)))
                        .Where(entry => entry.Data is { Played: false, PlaybackPositionTicks: > 0 })
                        .OrderByDescending(entry => entry.Data!.LastPlayedDate)
                        .Select(entry => entry.Episode)
                        .FirstOrDefault();
                    if (resumable is not null)
                        return [resumable];

                    var watched = episodes
                        .Select(episode => (Episode: episode, Data: userDataManager.GetUserData(user, episode)))
                        .Where(entry => entry.Data?.Played == true
                            && entry.Episode.ParentIndexNumber is { } && entry.Episode.IndexNumber is { })
                        .Select(entry => new EpisodeWatch(
                            entry.Episode.ParentIndexNumber!.Value,
                            entry.Episode.IndexNumber!.Value,
                            entry.Data!.LastPlayedDate is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : null))
                        .ToList();
                    var next = NextUpSelector.Select(
                        episodes,
                        episode => episode.ParentIndexNumber,
                        episode => episode.IndexNumber,
                        episode => episode.PremiereDate,
                        watched,
                        DateTime.UtcNow,
                        requireAirDate: false);
                    return next is null ? [] : [next];
                }

            case PrefetchTrigger.PlaybackStarted:
                {
                    if (item is not Episode current || current.Series is not { } series)
                        return [];

                    var depth = NebulaBridgePlugin.Instance?.Configuration.DiscoveryPrefetchDepth ?? 2;
                    return NextUpSelector.Following(
                        EpisodesUnder(series),
                        current,
                        depth,
                        episode => episode.ParentIndexNumber,
                        episode => episode.IndexNumber,
                        episode => episode.PremiereDate,
                        DateTime.UtcNow);
                }

            default:
                return [];
        }
    }

    private List<Episode> EpisodesUnder(BaseItem root) =>
        libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    AncestorIds = [root.Id],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Episode>()
            // Discovered sources are alternate versions of the episode and answer the same
            // query; only the primary version is a prefetch target.
            .Where(episode => !episode.IsStream() && IsPrefetchable(episode))
            .ToList();

    private static bool IsPrefetchable(Episode episode) =>
        episode.HasStreamTag()
        || (episode.Path?.StartsWith("nebulabridge://", StringComparison.OrdinalIgnoreCase) ?? false);

    private static void Log(
        string eventName,
        Episode episode,
        PrefetchIntent intent,
        Dictionary<string, object?>? fields = null
    )
    {
        fields ??= [];
        fields["trigger"] = intent.Trigger.ToString();
        fields["season"] = episode.ParentIndexNumber;
        fields["episode"] = episode.IndexNumber;
        fields["series"] = episode.SeriesName;
        NebulaBridgeFileLog.Write(NebulaLogVerbosity.Information, eventName, episode.Id, fields);
    }
}
