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

    private sealed class BarrierSearchEngine : IIndexerSearchEngine
    {
        private readonly TaskCompletionSource _bothStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _started;

        public int Started => _started;

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
