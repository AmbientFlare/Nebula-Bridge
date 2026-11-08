using NebulaBridge;
using NebulaBridge.Decorators;

namespace NebulaBridge.Tests;

public class StreamProviderIdTests
{
    [Fact]
    public void StreamRowsDropTheTmdbIdSoJellyfinsMissingEpisodeProviderLeavesThemAlone()
    {
        // Jellyfin 12's TmdbMissingEpisodeProvider deletes every virtual episode that carries a
        // TMDb id on each library scan; stream rows are virtual, so they must not carry one.
        var primary = new Dictionary<string, string>
        {
            ["Imdb"] = "tt13111078",
            ["tmdb"] = "1234567",
            ["Tvdb"] = "9876543",
        };

        var ids = NebulaBridgeManager.BuildStreamProviderIds(primary, "tt13111078:3:8");

        Assert.Equal("tt13111078", ids["Imdb"]);
        Assert.Equal("9876543", ids["Tvdb"]);
        Assert.Equal("tt13111078:3:8", ids["Stremio"]);
        Assert.False(ids.ContainsKey("Tmdb"));
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void StreamRowsOverrideAnInheritedStremioId()
    {
        var primary = new Dictionary<string, string> { ["Stremio"] = "tt1:1:1" };

        var ids = NebulaBridgeManager.BuildStreamProviderIds(primary, "tt1:1:2");

        Assert.Equal("tt1:1:2", ids["Stremio"]);
        Assert.Single(ids);
    }
}

public class SeriesWalkStreamRowTests
{
    [Fact]
    public void SeriesEpisodeWalksExcludeStreamRows()
    {
        // SeriesMetadataService.RemoveObsoleteEpisodes walks the series this way on every scan
        // and deletes virtual episodes that share an index number with a real one.
        var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            SeriesPresentationUniqueKey = "series-key",
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode],
        };

        NebulaBridgeItemRepository.ExcludeStreamRowsFromSeriesWalk(query);

        Assert.Contains(NebulaBridgeManager.StreamTag, query.ExcludeTags);
    }

    [Fact]
    public void OtherQueriesAndStreamRowLookupsAreLeftAlone()
    {
        var plain = new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            ParentId = Guid.NewGuid(),
            IndexNumber = 8,
        };
        var streamLookup = new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            SeriesPresentationUniqueKey = "series-key",
            Tags = [NebulaBridgeManager.StreamTag],
        };

        NebulaBridgeItemRepository.ExcludeStreamRowsFromSeriesWalk(plain);
        NebulaBridgeItemRepository.ExcludeStreamRowsFromSeriesWalk(streamLookup);

        Assert.Empty(plain.ExcludeTags);
        Assert.Empty(streamLookup.ExcludeTags);
    }
}
