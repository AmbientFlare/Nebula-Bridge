using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using NebulaBridge.Decorators;

namespace NebulaBridge.Tests;

public sealed class MediaSourceAssociationTests
{
    [Theory]
    [InlineData("nebulabridge-stream")]
    [InlineData("gelato-stream")]
    public void AssociatedCurrentAndLegacyStreamRowsAreAccepted(string tag)
    {
        var owner = Episode("tt10986410:1:1");
        var source = Episode("tt10986410:1:1", tag);

        Assert.True(
            MediaSourceManagerDecorator.IsAssociatedStreamItemForPlayback(source, owner)
        );
    }

    [Fact]
    public void CrossEpisodeStreamRowIsRejected()
    {
        var owner = Episode("tt10986410:1:1");
        var source = Episode("tt10986410:1:2", "nebulabridge-stream");

        Assert.False(
            MediaSourceManagerDecorator.IsAssociatedStreamItemForPlayback(source, owner)
        );
    }

    [Fact]
    public void UntaggedRowIsRejected()
    {
        var owner = Episode("tt10986410:1:1");
        var source = Episode("tt10986410:1:1");

        Assert.False(
            MediaSourceManagerDecorator.IsAssociatedStreamItemForPlayback(source, owner)
        );
    }

    [Fact]
    public void NativeEpisodeQueryUsesSeriesTitleAndSeriesImdbIdentity()
    {
        var episode = new Episode
        {
            Name = "Pilot",
            SeriesName = "Ted Lasso",
            ParentIndexNumber = 1,
            IndexNumber = 1,
            ProviderIds = new Dictionary<string, string>
            {
                ["Stremio"] = "tt10986410:1:1",
            },
        };

        var query = NebulaBridgeManager.BuildNativeMediaQuery(episode);

        Assert.Equal("Ted Lasso", query.Title);
        Assert.Equal(1, query.Season);
        Assert.Equal(1, query.Episode);
        Assert.Equal("tt10986410", query.ImdbId);
    }

    [Fact]
    public void NativeMovieQueryRetainsMovieTitleAndProviderIds()
    {
        var movie = new Movie
        {
            Name = "Night of the Living Dead",
            ProductionYear = 1968,
            ProviderIds = new Dictionary<string, string>
            {
                [nameof(MetadataProvider.Imdb)] = "tt0063350",
            },
        };

        var query = NebulaBridgeManager.BuildNativeMediaQuery(movie);

        Assert.Equal("Night of the Living Dead", query.Title);
        Assert.Equal(1968, query.Year);
        Assert.Equal("tt0063350", query.ImdbId);
    }

    private static Episode Episode(string stremioId, string? tag = null) =>
        new()
        {
            ProviderIds = new Dictionary<string, string> { ["Stremio"] = stremioId },
            Tags = tag is null ? [] : [tag],
        };
}
