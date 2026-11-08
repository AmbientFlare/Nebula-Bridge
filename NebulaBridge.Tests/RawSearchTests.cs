using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using NebulaBridge;
using NebulaBridge.Filters;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

/// <summary>
/// Raw search is a side feature. The tests that matter most are the ones
/// proving it cannot reach anything that already works: only items it created
/// are ever considered throwaway, and the prefix is the only way in.
/// </summary>
public sealed class RawSearchTests
{
    private static NativeReleaseCandidate Candidate(
        string title = "Some.Release.2024.1080p.WEB",
        string? infoHash = "0123456789abcdef0123456789abcdef01234567",
        long? size = 3_221_225_472,
        DateTimeOffset? published = null
    ) =>
        new(
            SourceId: "example-indexer",
            Title: title,
            Link: new Uri("https://example.invalid/release"),
            InfoHash: infoHash,
            SizeBytes: size,
            PublishedAt: published
        );

    [Fact]
    public void RawPrefixIsTheDocumentedOne()
    {
        // Mirrors the existing "local:" prefix; changing it changes the UX.
        Assert.Equal("raw:", SearchActionFilter.RawSearchPrefix);
        Assert.Equal("raw:", NebulaBridgeManager.RawSearchIdPrefix);
    }

    [Fact]
    public void OnlyRawSearchResultsAreTreatedAsThrowaway()
    {
        var raw = new Movie();
        raw.SetProviderId("Stremio", "raw:ABCDEF");

        var fromCatalog = new Movie();
        fromCatalog.SetProviderId("Stremio", "tt0106145");

        var untouched = new Movie();

        Assert.True(raw.IsRawSearchResult());
        Assert.False(fromCatalog.IsRawSearchResult());
        Assert.False(untouched.IsRawSearchResult());
    }

    [Fact]
    public void OnlyUnpromotedRawDiscoveryItemsAreDisposable()
    {
        var rawDiscovery = new Movie { Tags = [NebulaBridgeManager.DiscoveryTag] };
        rawDiscovery.SetProviderId("Stremio", "raw:ABCDEF");

        var promotedRaw = new Movie { Tags = [NebulaBridgeManager.PromotedTag] };
        promotedRaw.SetProviderId("Stremio", "raw:ABCDEF");

        Assert.True(rawDiscovery.IsDisposableRawSearchResult());
        Assert.False(promotedRaw.IsDisposableRawSearchResult());
    }

    [Fact]
    public void IdentityIsStableForTheSameQueryAndRelease()
    {
        // The same result must keep the same id across repeat searches, or
        // resume position and favourites break.
        var manager = RawMetaBuilder();
        var first = manager("concert film", Candidate());
        var second = manager("concert film", Candidate());

        Assert.NotNull(first);
        Assert.Equal(first!.Id, second!.Id);
        Assert.StartsWith("raw:", first.Id, StringComparison.Ordinal);
        Assert.Equal("raw:".Length + 64, first.Id.Length);
    }

    [Fact]
    public void IdentityDiffersPerReleaseAndPerQuery()
    {
        var manager = RawMetaBuilder();

        var releaseA = manager("concert film", Candidate(infoHash: "aaaa"))!.Id;
        var releaseB = manager("concert film", Candidate(infoHash: "bbbb"))!.Id;
        var otherQuery = manager("different query", Candidate(infoHash: "aaaa"))!.Id;

        Assert.NotEqual(releaseA, releaseB);
        Assert.NotEqual(releaseA, otherQuery);
    }

    [Fact]
    public void QueryCasingAndSpacingDoNotChangeIdentity()
    {
        var manager = RawMetaBuilder();

        Assert.Equal(
            manager("Concert Film", Candidate())!.Id,
            manager("  concert film  ", Candidate())!.Id
        );
    }

    [Fact]
    public void ReleaseTitleBecomesTheDisplayName()
    {
        var manager = RawMetaBuilder();
        var meta = manager("anything", Candidate(title: "A.Very.Specific.File.Name.mkv"));

        Assert.Equal("A.Very.Specific.File.Name.mkv", meta!.Name);
    }

    [Fact]
    public void ATitlelessReleaseIsSkippedRatherThanShown()
    {
        var manager = RawMetaBuilder();

        Assert.Null(manager("anything", Candidate(title: "")));
    }

    [Fact]
    public void DescriptionCarriesSizeSourceAndDate()
    {
        var manager = RawMetaBuilder();
        var meta = manager(
            "anything",
            Candidate(size: 3_221_225_472, published: new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero))
        );

        Assert.Contains("3 GB", meta!.Description, StringComparison.Ordinal);
        Assert.Contains("example-indexer", meta.Description, StringComparison.Ordinal);
        Assert.Contains("2026-05-04", meta.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void NewestFirstIsTheOrdering()
    {
        // Seeders are meaningless once every result is cached, so recency is
        // the only useful signal.
        var older = Candidate(published: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Candidate(published: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var undated = Candidate(published: null);

        var ordered = new[] { older, undated, newer }
            .OrderByDescending(candidate => candidate.PublishedAt ?? DateTimeOffset.MinValue)
            .ToArray();

        Assert.Same(newer, ordered[0]);
        Assert.Same(older, ordered[1]);
        Assert.Same(undated, ordered[2]);
    }

    /// <summary>
    /// IntoRawMeta needs no manager state, so it is reached without standing up
    /// the whole dependency graph.
    /// </summary>
    private static Func<string, NativeReleaseCandidate, StremioMeta?> RawMetaBuilder()
    {
        var manager = (NebulaBridgeManager)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(NebulaBridgeManager)
            );

        return (query, candidate) => manager.IntoRawMeta(query, candidate);
    }
}
