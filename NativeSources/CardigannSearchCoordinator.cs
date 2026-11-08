using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.NativeSources;

public interface IIndexerSearchEngine
{
    Task<IReadOnlyList<NativeReleaseCandidate>> SearchAsync(
        IndexerDefinition definition,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// How long <paramref name="definition"/> may take. The engine decides, because only it
    /// knows whether the indexer will be fetched directly or through FlareSolverr, and the two
    /// differ by an order of magnitude.
    /// </summary>
    TimeSpan GetSearchTimeout(IndexerDefinition definition);
}

/// <summary>
/// How long a sweep may take. Playback discovery is on a person's clock and has to give up on
/// an indexer eventually; a search the user typed is not, and is expected to be complete rather
/// than prompt, so every indexer is allowed to answer however long it takes.
/// </summary>
public enum IndexerSearchBudget
{
    /// <summary>Each indexer gets the budget its route needs, then is abandoned.</summary>
    Routed,

    /// <summary>No per-indexer deadline; only the caller's cancellation applies.</summary>
    Unbounded,

    /// <summary>
    /// Every indexer gets its routed budget, but there is no discovery deadline: the sweep
    /// answers when the last indexer has answered or run out its budget. For work nobody is
    /// waiting on, such as prefetch, where a complete answer matters more than a prompt one.
    /// </summary>
    Complete,
}

public sealed class CardigannSearchCoordinator(
    IndexerDefinitionLoader definitionLoader,
    IIndexerSearchEngine searchEngine,
    NativeReleaseAggregator aggregator,
    ILogger<CardigannSearchCoordinator> logger
)
{
    /// <summary>How long a sweep that finished after its deadline is kept for the next discovery of the same title.</summary>
    internal static readonly TimeSpan CompletedSweepLifetime = TimeSpan.FromMinutes(10);
    private const int PerSourceLimit = 100;
    private const int GlobalLimit = 200;

    private readonly ConcurrentDictionary<string, (NativeSearchResult Result, DateTimeOffset ExpiresUtc)> _completedSweeps =
        new(StringComparer.Ordinal);
    private TimeSpan? _deadlineOverride;
    private Task _lastBackgroundSweep = Task.CompletedTask;

    /// <summary>Test seam: completes when the most recent gated sweep has finished and been kept.</summary>
    internal Task LastBackgroundSweep => Volatile.Read(ref _lastBackgroundSweep);

    /// <summary>Test seam: a fixed discovery deadline instead of the configured one.</summary>
    internal CardigannSearchCoordinator WithDeadline(TimeSpan deadline)
    {
        _deadlineOverride = deadline;
        return this;
    }

    private TimeSpan Deadline =>
        _deadlineOverride
        ?? TimeSpan.FromSeconds(NebulaBridgePlugin.Instance?.Configuration.DiscoveryDeadlineSeconds ?? 25);

    /// <summary>
    /// Sweeps every enabled indexer. A routed sweep is on a viewer's clock: it answers at the
    /// discovery deadline with the best of what has arrived, never with the first hit, so an
    /// early answer can only win on time and never lose on quality. Indexers still running are
    /// left to finish and their complete answer is served to the next sweep for the same query.
    /// </summary>
    public async Task<NativeSearchResult> SearchAsync(
        NativeMediaQuery query,
        string? definitionId,
        CancellationToken cancellationToken,
        IndexerSearchBudget budget = IndexerSearchBudget.Routed
    )
    {
        var definitions = string.IsNullOrWhiteSpace(definitionId)
            ? definitionLoader.GetEnabledDefinitions()
            : [definitionLoader.GetRequired(definitionId)];

        var sweepKey = SweepKey(query, definitionId);
        if (budget != IndexerSearchBudget.Unbounded && TryTakeCompletedSweep(sweepKey, out var completedSweep))
        {
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "source-sweep-reused",
                fields: new Dictionary<string, object?>
                {
                    ["candidates"] = completedSweep.Candidates.Count,
                });
            return completedSweep;
        }

        if (budget != IndexerSearchBudget.Routed)
        {
            var searches = definitions.Select(definition =>
                SearchOneAsync(definition, query, budget, cancellationToken));
            return Combine(await Task.WhenAll(searches).ConfigureAwait(false), complete: true);
        }

        // The sweep is tied to the caller only until the deadline; after that the stragglers
        // belong to this coordinator and run out their own per-indexer budgets.
        using var sweep = new CancellationTokenSource();
        var gatePassed = false;
        using var abandon = cancellationToken.Register(() =>
        {
            if (!Volatile.Read(ref gatePassed))
                sweep.Cancel();
        });

        var pending = definitions
            .Select(definition => (Definition: definition, Task: SearchOneAsync(definition, query, budget, sweep.Token)))
            .ToList();
        var all = Task.WhenAll(pending.Select(entry => entry.Task));
        var deadline = Deadline;
        var stopwatch = Stopwatch.StartNew();
        using var gate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = await Task.WhenAny(all, Task.Delay(deadline, gate.Token)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        gate.Cancel();
        Volatile.Write(ref gatePassed, true);

        if (finished == all)
            return Combine(await all.ConfigureAwait(false), complete: true);

        var outcomes = new List<IndexerSearchOutcome>(pending.Count);
        var stragglers = new List<string>();
        foreach (var (definition, task) in pending)
        {
            if (task.IsCompletedSuccessfully)
            {
                outcomes.Add(task.Result);
                continue;
            }

            stragglers.Add(definition.Id);
            outcomes.Add(new(
                [],
                new NativeSourceFailure(
                    definition.Id,
                    "The indexer had not answered by the discovery deadline.",
                    definition.Name,
                    "request",
                    "deadline"
                )));
        }

        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "source-sweep-gated",
            fields: new Dictionary<string, object?>
            {
                ["deadlineMs"] = deadline.TotalMilliseconds,
                ["elapsedMs"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds),
                ["answered"] = pending.Count - stragglers.Count,
                ["pending"] = string.Join(",", stragglers),
            });

        Volatile.Write(ref _lastBackgroundSweep, KeepCompletedSweepAsync(sweepKey, all, stopwatch));
        return Combine(outcomes, complete: false);
    }

    private async Task KeepCompletedSweepAsync(string sweepKey, Task<IndexerSearchOutcome[]> all, Stopwatch stopwatch)
    {
        IndexerSearchOutcome[] outcomes;
        try
        {
            outcomes = await all.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SearchOneAsync converts everything but cancellation; the sweep token is only
            // cancelled when the caller left before the deadline, and then nobody wants this.
            logger.LogDebug(ex, "Background indexer sweep did not complete");
            return;
        }

        var result = Combine(outcomes, complete: true);
        var now = DateTimeOffset.UtcNow;
        _completedSweeps[sweepKey] = (result, now + CompletedSweepLifetime);
        foreach (var stale in _completedSweeps.Where(entry => entry.Value.ExpiresUtc <= now).Select(entry => entry.Key).ToList())
            _completedSweeps.TryRemove(stale, out _);

        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "source-sweep-completed",
            fields: new Dictionary<string, object?>
            {
                ["elapsedMs"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds),
                ["candidates"] = result.Candidates.Count,
            });
    }

    private bool TryTakeCompletedSweep(string sweepKey, out NativeSearchResult result)
    {
        result = null!;
        if (!_completedSweeps.TryRemove(sweepKey, out var entry))
            return false;
        if (entry.ExpiresUtc <= DateTimeOffset.UtcNow)
            return false;
        result = entry.Result;
        return true;
    }

    internal static string SweepKey(NativeMediaQuery query, string? definitionId) =>
        string.Join(
            "\n",
            definitionId ?? string.Empty,
            query.Title.Trim().ToLowerInvariant(),
            query.Year,
            query.Season,
            query.Episode,
            query.ImdbId,
            query.TmdbId,
            query.TvdbId);

    private NativeSearchResult Combine(IReadOnlyList<IndexerSearchOutcome> completed, bool complete)
    {
        var candidates = completed.SelectMany(result => result.Candidates).ToList();
        var failures = completed
            .Where(result => result.Failure is not null)
            .Select(result => result.Failure!)
            .ToList();
        var output = aggregator.Aggregate(candidates, perSourceLimit: PerSourceLimit, globalLimit: GlobalLimit);
        return new NativeSearchResult(output, failures, complete);
    }

    private async Task<IndexerSearchOutcome> SearchOneAsync(
        IndexerDefinition definition,
        NativeMediaQuery query,
        IndexerSearchBudget budget,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (budget != IndexerSearchBudget.Unbounded)
        {
            timeout.CancelAfter(searchEngine.GetSearchTimeout(definition));
        }
        try
        {
            var candidates = await searchEngine
                .SearchAsync(definition, query, timeout.Token)
                .ConfigureAwait(false);
            return new(candidates, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Indexer search timeout: {IndexerId}", definition.Id);
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "indexer-search-timeout",
                fields: new Dictionary<string, object?> { ["indexer"] = definition.Id });
            return new(
                [],
                new NativeSourceFailure(
                    definition.Id,
                    "Request exceeded the per-indexer timeout.",
                    definition.Name,
                    "request",
                    "timeout"
                )
            );
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Indexer search failed: {IndexerId}", definition.Id);
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "indexer-search-failed",
                fields: new Dictionary<string, object?>
                {
                    ["indexer"] = definition.Id,
                    ["failureType"] = ex.GetType().Name,
                });
            return new(
                [],
                new NativeSourceFailure(
                    definition.Id,
                    "The indexer query failed.",
                    definition.Name,
                    ex is FormatException
                        or InvalidDataException
                        or System.Text.Json.JsonException
                        or System.Xml.XmlException
                        ? "parse"
                        : "request",
                    ex.GetType().Name.ToLowerInvariant()
                )
            );
        }
    }

    private sealed record IndexerSearchOutcome(
        IReadOnlyList<NativeReleaseCandidate> Candidates,
        NativeSourceFailure? Failure
    );
}
