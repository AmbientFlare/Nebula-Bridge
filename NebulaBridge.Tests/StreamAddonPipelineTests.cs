using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

/// <summary>Discovery asks the stream addons and the indexer sweep together; whoever can answer the viewer first does.</summary>
public sealed class StreamAddonPipelineTests
{
    private const string AddonHash = "0123456789abcdef0123456789abcdef01234567";
    private const string HeldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PromptHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly NativeMediaQuery Query = new("Reacher", Season: 4, Episode: 7, ImdbId: "tt1234567");

    [Fact]
    public async Task AnAddonAnswerIsServedWithoutWaitingForTheSweepWhichFinishesForNextTime()
    {
        var engine = new HeldSearchEngine();
        var coordinator = (await CreateCoordinatorAsync(engine)).WithDeadline(TimeSpan.FromSeconds(30));
        var pipeline = CreatePipeline(coordinator, cached: [AddonHash, HeldHash, PromptHash], addonHashes: [AddonHash]);

        var first = await pipeline.DiscoverAsync(Query, null, ["https://torrentio.strem.fun"], TimeSpan.FromSeconds(5), CancellationToken.None, IndexerSearchBudget.Routed);

        Assert.False(first.Complete);
        Assert.Equal("addon:torrentio.strem.fun", Assert.Single(first.Candidates).SourceId);
        Assert.True(Assert.Single(first.Candidates).Playable);
        Assert.False(engine.HeldToken.IsCancellationRequested);

        engine.Release();
        await coordinator.LastBackgroundSweep.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await pipeline.DiscoverAsync(Query, null, ["https://torrentio.strem.fun"], TimeSpan.FromSeconds(5), CancellationToken.None, IndexerSearchBudget.Routed);

        Assert.True(second.Complete);
        Assert.Equal(3, second.Candidates.Count);
        Assert.Contains(second.Candidates, candidate => candidate.SourceId == "addon:torrentio.strem.fun");
        Assert.Contains(second.Candidates, candidate => candidate.InfoHash == HeldHash);
    }

    [Fact]
    public async Task AnAddonAnswerNobodyHoldsFallsBackToTheSweep()
    {
        var engine = new HeldSearchEngine();
        var coordinator = (await CreateCoordinatorAsync(engine)).WithDeadline(TimeSpan.FromMilliseconds(100));
        var pipeline = CreatePipeline(coordinator, cached: [PromptHash], addonHashes: [AddonHash]);

        var result = await pipeline.DiscoverAsync(Query, null, ["https://torrentio.strem.fun"], TimeSpan.FromSeconds(5), CancellationToken.None, IndexerSearchBudget.Routed);

        Assert.False(result.Complete);
        Assert.Equal(PromptHash, Assert.Single(result.Candidates).InfoHash);
        engine.Release();
    }

    [Fact]
    public async Task AnAddonRowForAReleaseTheSweepFoundIsNotADuplicate()
    {
        var engine = new HeldSearchEngine();
        var coordinator = (await CreateCoordinatorAsync(engine)).WithDeadline(TimeSpan.FromMilliseconds(100));
        var pipeline = CreatePipeline(coordinator, cached: [PromptHash, HeldHash, AddonHash], addonHashes: [PromptHash, AddonHash]);
        engine.Release();

        var result = await pipeline.DiscoverAsync(Query, null, ["https://torrentio.strem.fun"], TimeSpan.FromSeconds(5), CancellationToken.None, IndexerSearchBudget.Complete);

        Assert.True(result.Complete);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(1, result.Candidates.Count(candidate => candidate.InfoHash == PromptHash));
        Assert.Equal("showrss-yml", result.Candidates.Single(candidate => candidate.InfoHash == PromptHash).SourceId);
    }

    [Fact]
    public async Task AnAddonThatIsDownCostsNothingButAFailureRow()
    {
        var engine = new HeldSearchEngine();
        var coordinator = (await CreateCoordinatorAsync(engine)).WithDeadline(TimeSpan.FromMilliseconds(100));
        var pipeline = CreatePipeline(coordinator, cached: [PromptHash], addonHashes: [], addonDown: true);

        var result = await pipeline.DiscoverAsync(Query, null, ["https://torrentio.strem.fun"], TimeSpan.FromSeconds(5), CancellationToken.None, IndexerSearchBudget.Routed);

        Assert.Equal(PromptHash, Assert.Single(result.Candidates).InfoHash);
        Assert.Contains(result.Failures, failure => failure.SourceId == "addon:torrentio.strem.fun");
        engine.Release();
    }

    [Fact]
    public async Task ASearchAimedAtOneIndexerDoesNotAskTheAddons()
    {
        var engine = new HeldSearchEngine();
        var coordinator = await CreateCoordinatorAsync(engine);
        var asked = 0;
        var pipeline = CreatePipeline(coordinator, cached: [PromptHash], addonHashes: [AddonHash], onRequest: () => asked++);
        engine.Release();

        var result = await pipeline.DiscoverAsync(Query, "showrss-yml", ["https://torrentio.strem.fun"], TimeSpan.FromSeconds(5), CancellationToken.None, IndexerSearchBudget.Routed);

        Assert.Equal(0, asked);
        Assert.Equal(PromptHash, Assert.Single(result.Candidates).InfoHash);
    }

    private static NativeSourcePipeline CreatePipeline(
        CardigannSearchCoordinator coordinator,
        string[] cached,
        string[] addonHashes,
        bool addonDown = false,
        Action? onRequest = null
    )
    {
        var handler = new CardigannTestSupport.StubHandler(_ =>
        {
            onRequest?.Invoke();
            if (addonDown)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var streams = string.Join(",", addonHashes.Select(hash => $$"""{"name":"Torrentio","title":"Release.{{hash[..4]}}\n👤 9 💾 1 GB","infoHash":"{{hash}}"}"""));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"streams":[{{streams}}]}""", Encoding.UTF8, "application/json"),
            };
        });
        var addons = new StreamAddonClient(new CardigannTestSupport.StubHttpClientFactory(handler), NullLogger<StreamAddonClient>.Instance);
        var availability = cached.ToDictionary(
            hash => hash,
            hash => new DebridAvailability("fake", true, [new(1, "Release.mkv", 1_000_000_000)]),
            StringComparer.OrdinalIgnoreCase
        );
        return new NativeSourcePipeline(coordinator, [], [new CachedProvider(availability)], NullLogger<NativeSourcePipeline>.Instance, addons);
    }

    private static async Task<CardigannSearchCoordinator> CreateCoordinatorAsync(IIndexerSearchEngine engine)
    {
        var archive = File.ReadAllText(CardigannTestSupport.FixturePath("internetarchive.yml"));
        var showRss = File.ReadAllText(CardigannTestSupport.FixturePath("showrss.yml"));
        var loader = CardigannTestSupport.CreateLoader(
            new CardigannTestSupport.MemoryDefinitionProvider([new("archive.yml", archive), new("showrss.yml", showRss)]),
            new CardigannTestSupport.MemoryPreferenceStore(["internetarchive", "showrss-yml"])
        );
        await loader.RefreshAsync(CancellationToken.None);
        return new CardigannSearchCoordinator(loader, engine, new NativeReleaseAggregator(), NullLogger<CardigannSearchCoordinator>.Instance);
    }

    /// <summary>showrss answers at once; internetarchive waits for <see cref="Release"/>.</summary>
    private sealed class HeldSearchEngine : IIndexerSearchEngine
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken HeldToken { get; private set; }

        public void Release() => _release.TrySetResult();

        public TimeSpan GetSearchTimeout(IndexerDefinition definition) => TimeSpan.FromSeconds(75);

        public async Task<IReadOnlyList<NativeReleaseCandidate>> SearchAsync(IndexerDefinition definition, NativeMediaQuery query, CancellationToken cancellationToken)
        {
            var held = definition.Id == "internetarchive";
            if (held)
            {
                HeldToken = cancellationToken;
                await _release.Task.WaitAsync(cancellationToken);
            }

            var hash = held ? HeldHash : PromptHash;
            return [new(definition.Id, "Release " + definition.Id, new Uri("magnet:?xt=urn:btih:" + hash), "torrent", hash)];
        }
    }

    private sealed class CachedProvider(IReadOnlyDictionary<string, DebridAvailability> availability) : IDebridProvider
    {
        public string Id => "fake";
        public string Name => "Fake";
        public bool Enabled => true;
        public bool Configured => true;
        public DebridProviderCapabilities Capabilities => DebridProviderCapabilities.CachedAvailability;

        public Task<DebridCacheCheckResult> CheckCachedAsync(IReadOnlyCollection<string> infoHashes, CancellationToken cancellationToken) =>
            Task.FromResult(new DebridCacheCheckResult(availability));

        public Task<DebridPlaybackResult> ResolvePlaybackAsync(NativeReleaseCandidate candidate, NativeMediaQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
