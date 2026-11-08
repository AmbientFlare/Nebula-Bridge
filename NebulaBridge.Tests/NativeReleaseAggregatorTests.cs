using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class NativeReleaseAggregatorTests
{
    [Fact]
    public void DeduplicatesByInfoHashAndPrefersMoreSeeders()
    {
        var candidates = new[]
        {
            Candidate("alpha", "One", "https://example.com/one", "abcdefabcdefabcdefabcdefabcdefabcdefabcd", 2),
            Candidate("beta", "One duplicate", "https://example.net/one", "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD", 20),
        };

        var result = new NativeReleaseAggregator().Aggregate(candidates);

        var winner = Assert.Single(result);
        Assert.Equal("beta", winner.SourceId);
        Assert.Equal(20, winner.Seeders);
    }

    [Fact]
    public void AppliesPerSourceAndGlobalLimitsDeterministically()
    {
        var candidates = Enumerable.Range(0, 10)
            .Select(index => Candidate("source", $"Release {index}", $"https://example.com/{index}", null, index));

        var result = new NativeReleaseAggregator().Aggregate(candidates, perSourceLimit: 3, globalLimit: 5);

        Assert.Equal(3, result.Count);
        Assert.Equal<int?>([9, 8, 7], result.Select(candidate => candidate.Seeders));
    }

    private static NativeReleaseCandidate Candidate(
        string source,
        string title,
        string url,
        string? infoHash,
        int seeders
    ) => new(source, title, new Uri(url), "torrent", infoHash, Seeders: seeders);
}
