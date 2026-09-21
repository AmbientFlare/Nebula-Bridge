using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;
using System.Xml;

namespace NebulaBridge.Tests;

public sealed class CardigannSearchCoordinatorTests
{
    [Fact]
    public async Task SearchesConcurrentlyReturnsPartialResultsAndIsolatesFailure()
    {
        var archive = File.ReadAllText(CardigannTestSupport.FixturePath("internetarchive.yml"));
        var showRss = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"));
        var preferences = new CardigannTestSupport.MemoryPreferenceStore(
            ["internetarchive", "showrss-yml"]
        );
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(
                [new("archive.yml", archive), new("showrss.yml", showRss)]
            ),
            preferences
        );
        await loader.RefreshAsync(CancellationToken.None);
        var engine = new BarrierSearchEngine();
        var coordinator = new CardigannSearchCoordinator(
            loader,
            engine,
            new NativeReleaseAggregator(),
            NullLogger<CardigannSearchCoordinator>.Instance
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await coordinator.SearchAsync(
            new NativeMediaQuery("public domain"),
            null,
            timeout.Token
        );

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("internetarchive", candidate.SourceId);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("showrss-yml", failure.SourceId);
        Assert.Equal("parse", failure.Stage);
        Assert.Equal(2, engine.Started);
    }

    [Theory]
    [InlineData(IndexerSearchBudget.Routed, 0)]
    [InlineData(IndexerSearchBudget.Complete, 0)]
    [InlineData(IndexerSearchBudget.Unbounded, 1)]
    public async Task AnUnboundedSweepWaitsOutAnIndexerThatARoutedOneWouldAbandon(
        IndexerSearchBudget budget,
        int expectedCandidates
    )
    {
        var archive = File.ReadAllText(CardigannTestSupport.FixturePath("internetarchive.yml"));
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider([new("archive.yml", archive)]),
            new CardigannTestSupport.MemoryPreferenceStore(["internetarchive"])
        );
        await loader.RefreshAsync(CancellationToken.None);
        var coordinator = new CardigannSearchCoordinator(
            loader,
            new SlowSearchEngine(
                budget: TimeSpan.FromMilliseconds(20),
                takes: TimeSpan.FromMilliseconds(300)
            ),
            new NativeReleaseAggregator(),
            NullLogger<CardigannSearchCoordinator>.Instance
        );

        var result = await coordinator.SearchAsync(
            new NativeMediaQuery("public domain"),
            null,
            CancellationToken.None,
            budget
        );

        Assert.Equal(expectedCandidates, result.Candidates.Count);
    }

    [Fact]
    public void AggregatorDeduplicatesByInfoHashAcrossIndexers()
    {
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        var aggregator = new NativeReleaseAggregator();

        var result = aggregator.Aggregate(
            [
                new("one", "Release", new Uri("magnet:?xt=urn:btih:" + hash), "torrent", hash.ToUpperInvariant(), Seeders: 2, SourceName: "One", DefinitionHash: "definition-one"),
                new("two", "Release", new Uri("magnet:?xt=urn:btih:" + hash), "torrent", hash, Seeders: 10),
            ]
        );

        var selected = Assert.Single(result);
        Assert.Equal("two", selected.SourceId);
        Assert.Equal(hash, selected.InfoHash);
        Assert.Equal(2, selected.Sources?.Count);
        Assert.Contains(
            selected.Sources!,
            source => source.IndexerId == "one" && source.DefinitionHash == "definition-one"
        );
        Assert.Contains(selected.Sources!, source => source.IndexerId == "two");
    }

    [Fact]
    public async Task ARoutedSweepAnswersAtTheDeadlineAndServesTheFinishedSweepNextTime()
    {
        var engine = new HeldSearchEngine(held: "showrss-yml");
        var coordinator = (await CreateTwoIndexerCoordinatorAsync(engine)).WithDeadline(TimeSpan.FromMilliseconds(150));
        var query = new NativeMediaQuery("public domain");

        var gated = await coordinator.SearchAsync(query, null, CancellationToken.None);

        Assert.False(gated.Complete);
        Assert.Equal("internetarchive", Assert.Single(gated.Candidates).SourceId);
        var failure = Assert.Single(gated.Failures);
        Assert.Equal("showrss-yml", failure.SourceId);
        Assert.Equal("deadline", failure.Reason);
        Assert.False(engine.HeldToken.IsCancellationRequested);

        engine.Release();
        await coordinator.LastBackgroundSweep.WaitAsync(TimeSpan.FromSeconds(5));

        var reused = await coordinator.SearchAsync(query, null, CancellationToken.None);

        Assert.True(reused.Complete);
        Assert.Equal(2, reused.Candidates.Count);
        Assert.Empty(reused.Failures);
        Assert.Equal(2, engine.Started);

        // A finished sweep is served once; the next discovery sweeps afresh.
        var fresh = await coordinator.SearchAsync(query, null, CancellationToken.None);
        Assert.True(fresh.Complete);
        Assert.Equal(4, engine.Started);
    }

    [Fact]
    public async Task ACallerLeavingBeforeTheDeadlineTakesTheSweepWithIt()
    {
        var engine = new HeldSearchEngine(held: "showrss-yml");
        var coordinator = (await CreateTwoIndexerCoordinatorAsync(engine)).WithDeadline(TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();

        var search = coordinator.SearchAsync(new NativeMediaQuery("public domain"), null, caller.Token);
        await engine.HeldStarted.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.True(engine.HeldToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ACallerLeavingAfterTheDeadlineLeavesTheStragglersRunning()
    {
        var engine = new HeldSearchEngine(held: "showrss-yml");
        var coordinator = (await CreateTwoIndexerCoordinatorAsync(engine)).WithDeadline(TimeSpan.FromMilliseconds(100));
        using var caller = new CancellationTokenSource();

        var gated = await coordinator.SearchAsync(new NativeMediaQuery("public domain"), null, caller.Token);
        caller.Cancel();

        Assert.False(gated.Complete);
        Assert.False(engine.HeldToken.IsCancellationRequested);
        engine.Release();
        await coordinator.LastBackgroundSweep.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(IndexerSearchBudget.Unbounded)]
    [InlineData(IndexerSearchBudget.Complete)]
    public async Task SweepsNobodyIsWaitingOnIgnoreTheDiscoveryDeadline(IndexerSearchBudget budget)
    {
        var engine = new HeldSearchEngine(held: "showrss-yml");
        var coordinator = (await CreateTwoIndexerCoordinatorAsync(engine)).WithDeadline(TimeSpan.FromMilliseconds(50));

        var search = coordinator.SearchAsync(
            new NativeMediaQuery("public domain"),
            null,
            CancellationToken.None,
            budget
        );
        await Task.Delay(200);
        Assert.False(search.IsCompleted);

        engine.Release();
        var result = await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Complete);
        Assert.Equal(2, result.Candidates.Count);
    }

    private static async Task<CardigannSearchCoordinator> CreateTwoIndexerCoordinatorAsync(IIndexerSearchEngine engine)
    {
        var archive = File.ReadAllText(CardigannTestSupport.FixturePath("internetarchive.yml"));
        var showRss = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"));
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider(
                [new("archive.yml", archive), new("showrss.yml", showRss)]
            ),
            new CardigannTestSupport.MemoryPreferenceStore(["internetarchive", "showrss-yml"])
        );
        await loader.RefreshAsync(CancellationToken.None);
        return new CardigannSearchCoordinator(
            loader,
            engine,
            new NativeReleaseAggregator(),
            NullLogger<CardigannSearchCoordinator>.Instance
        );
    }

    /// <summary>Every indexer answers at once except <paramref name="held"/>, which waits for <see cref="Release"/>.</summary>
    private sealed class HeldSearchEngine(string held) : IIndexerSearchEngine
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _heldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public int Started => _started;
        public Task HeldStarted => _heldStarted.Task;
        public CancellationToken HeldToken { get; private set; }

        public void Release() => _release.TrySetResult();

        public TimeSpan GetSearchTimeout(IndexerDefinition definition) => TimeSpan.FromSeconds(75);

        public async Task<IReadOnlyList<NativeReleaseCandidate>> SearchAsync(
            IndexerDefinition definition,
            NativeMediaQuery query,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _started);
            if (definition.Id == held)
            {
                HeldToken = cancellationToken;
                _heldStarted.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            var hash = definition.Id == held
                ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            return
            [
                new(definition.Id, "Release " + definition.Id, new Uri("magnet:?xt=urn:btih:" + hash), "torrent", hash),
            ];
        }
    }

    private sealed class SlowSearchEngine(TimeSpan budget, TimeSpan takes) : IIndexerSearchEngine
    {
        public TimeSpan GetSearchTimeout(IndexerDefinition definition) => budget;

        public async Task<IReadOnlyList<NativeReleaseCandidate>> SearchAsync(
            IndexerDefinition definition,
            NativeMediaQuery query,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(takes, cancellationToken);
            return
            [
                new(
                    definition.Id,
                    "Release",
                    new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
                    "torrent",
                    "0123456789abcdef0123456789abcdef01234567"
                ),
            ];
        }
    }

    private sealed class BarrierSearchEngine : IIndexerSearchEngine
    {
        private readonly TaskCompletionSource _bothStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _started;

        public int Started => _started;

        public TimeSpan GetSearchTimeout(IndexerDefinition definition) =>
            TimeSpan.FromSeconds(75);

        public async Task<IReadOnlyList<NativeReleaseCandidate>> SearchAsync(
            IndexerDefinition definition,
            NativeMediaQuery query,
            CancellationToken cancellationToken
        )
        {
            if (Interlocked.Increment(ref _started) == 2)
            {
                _bothStarted.TrySetResult();
            }

            await _bothStarted.Task.WaitAsync(cancellationToken);
            if (definition.Id == "showrss-yml")
            {
                throw new XmlException("fixture response is malformed");
            }

            return
            [
                new NativeReleaseCandidate(
                    definition.Id,
                    "Public Domain Film",
                    new Uri("https://archive.org/download/example/example_archive.torrent"),
                    "torrent",
                    "0123456789abcdef0123456789abcdef01234567"
                ),
            ];
        }
    }
}
