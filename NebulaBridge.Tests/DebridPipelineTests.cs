using NebulaBridge.NativeSources;
using Microsoft.Extensions.Logging.Abstractions;

namespace NebulaBridge.Tests;

public sealed class DebridPipelineTests
{
    private const string CachedHash = "0123456789abcdef0123456789abcdef01234567";
    private const string UncachedHash = "89abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task AvailabilityIsBulkMappedAndUncachedCandidatesAreNotPlayable()
    {
        var provider = new RecordingProvider(
            new Dictionary<string, DebridAvailability>(StringComparer.OrdinalIgnoreCase)
            {
                [CachedHash] = new("fake", true, [new(7, "Movie.2026.mkv", 900)]),
                [UncachedHash] = new("fake", false, []),
            }
        );
        var pipeline = new NativeSourcePipeline(
            null!,
            [],
            [provider],
            NullLogger<NativeSourcePipeline>.Instance
        );
        var search = new NativeSearchResult(
            [
                Candidate("one", CachedHash.ToUpperInvariant()),
                Candidate("two", UncachedHash),
            ],
            []
        );

        var result = await pipeline.AttachDebridAvailabilityAsync(search, CancellationToken.None);

        var playable = Assert.Single(result.Candidates);
        Assert.Equal(CachedHash, playable.InfoHash);
        Assert.True(playable.Playable);
        Assert.True(Assert.Single(playable.Availability!).Cached);
        Assert.Equal(2, Assert.Single(provider.Requests).Count);
    }

    [Fact]
    public async Task ProviderFailureDoesNotEraseDirectCandidateOrIndexerFailures()
    {
        var provider = new RecordingProvider(
            new Dictionary<string, DebridAvailability>(),
            new NativeSourceFailure("fake", "Unavailable", "Fake", "cache", "timeout")
        );
        var pipeline = new NativeSourcePipeline(
            null!,
            [],
            [provider],
            NullLogger<NativeSourcePipeline>.Instance
        );
        var existingFailure = new NativeSourceFailure(
            "broken-indexer",
            "Indexer failed",
            "Broken",
            "request",
            "timeout"
        );
        var direct = new NativeReleaseCandidate(
            "archive",
            "Direct movie",
            new Uri("https://archive.example/movie.mp4"),
            "http"
        );

        var result = await pipeline.AttachDebridAvailabilityAsync(
            new NativeSearchResult([Candidate("torrent", CachedHash), direct], [existingFailure]),
            CancellationToken.None
        );

        Assert.Same(direct.Link, Assert.Single(result.Candidates).Link);
        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, failure => failure.SourceId == "broken-indexer");
        Assert.Contains(result.Failures, failure => failure.SourceId == "fake" && failure.Stage == "cache");
    }

    [Fact]
    public async Task PreparingCachedStreamDoesNotResolveOrMutateProvider()
    {
        var provider = new RecordingProvider(
            new Dictionary<string, DebridAvailability>(StringComparer.OrdinalIgnoreCase)
            {
                [CachedHash] = new("fake", true, [new(7, "Show.S02E05.mkv", 900)]),
            }
        );
        var pipeline = new NativeSourcePipeline(
            null!,
            [],
            [provider],
            NullLogger<NativeSourcePipeline>.Instance
        );
        var hydrated = await pipeline.AttachDebridAvailabilityAsync(
            new NativeSearchResult([Candidate("one", CachedHash)], []),
            CancellationToken.None
        );

        Assert.True(Assert.Single(hydrated.Candidates).Playable);
        Assert.Equal(0, provider.ResolveCalls);
    }

    [Fact]
    public void WrongTitleCandidateIsSkippedAndNextCachedCandidateIsPrepared()
    {
        var provider = new RecordingProvider(new Dictionary<string, DebridAvailability>());
        var pipeline = new NativeSourcePipeline(
            null!,
            [],
            [provider],
            NullLogger<NativeSourcePipeline>.Instance
        );
        var query = new NativeMediaQuery("Ted Lasso", Season: 1, Episode: 1);
        var wrong = Candidate("wrong", CachedHash) with
        {
            Title = "The President Carter S01E01",
            Availability =
            [
                new DebridAvailability(
                    "fake",
                    true,
                    [new(1, "The.President.Carter.S01E01.1080p.mkv", 1000)]
                ),
            ],
            Playable = true,
        };
        var correct = Candidate("correct", UncachedHash) with
        {
            Title = "Ted Lasso S01E01",
            Availability =
            [
                new DebridAvailability(
                    "fake",
                    true,
                    [new(2, "Ted.Lasso.S01E01.1080p.mkv", 900)]
                ),
            ],
            Playable = true,
        };

        var prepared = new[] { wrong, correct }
            .Select(candidate => pipeline.PrepareDebridStream(candidate, query))
            .OfType<NativePreparedStream>()
            .ToList();

        var stream = Assert.Single(prepared);
        Assert.Equal("fake:correct", stream.SourceId);
        Assert.Equal("Ted.Lasso.S01E01.1080p.mkv", stream.Filename);
    }

    private static NativeReleaseCandidate Candidate(string source, string hash) =>
        new(
            source,
            "Movie 2026",
            new Uri("magnet:?xt=urn:btih:" + hash),
            "torrent",
            hash
        );

    private sealed class RecordingProvider(
        IReadOnlyDictionary<string, DebridAvailability> availability,
        NativeSourceFailure? failure = null
    ) : IDebridProvider
    {
        public string Id => "fake";

        public string Name => "Fake";

        public bool Enabled => true;

        public bool Configured => true;

        public List<IReadOnlyCollection<string>> Requests { get; } = [];

        public int ResolveCalls { get; private set; }

        public Task<DebridCacheCheckResult> CheckCachedAsync(
            IReadOnlyCollection<string> infoHashes,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(infoHashes);
            return Task.FromResult(new DebridCacheCheckResult(availability, failure));
        }

        public Task<DebridPlaybackResult> ResolvePlaybackAsync(
            NativeReleaseCandidate candidate,
            NativeMediaQuery query,
            CancellationToken cancellationToken
        )
        {
            ResolveCalls++;
            return Task.FromResult<DebridPlaybackResult>(new(null));
        }
    }
}
