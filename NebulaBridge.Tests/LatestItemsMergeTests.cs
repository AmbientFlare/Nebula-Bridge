using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using NebulaBridge.Decorators;

namespace NebulaBridge.Tests;

public sealed class LatestItemsMergeTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Tuple<BaseItem, List<BaseItem>> Added(string name, int day) =>
        new(null!, [new Movie { Name = name, DateCreated = T0.AddDays(day) }]);

    [Fact]
    public void MergesLibrariesNewestFirstAndKeepsTheLimit()
    {
        List<Tuple<BaseItem, List<BaseItem>>> movies = [Added("Training Day", 9), Added("Seven Samurai", 3)];
        List<Tuple<BaseItem, List<BaseItem>>> adult = [Added("Other", 6)];

        var merged = UserViewManagerDecorator.Merge([movies, adult], null, 2);

        Assert.Equal(["Training Day", "Other"], merged.Select(group => group.Item2[0].Name));
    }

    [Fact]
    public void AGroupSortsByItsNewestItem()
    {
        var show = new Tuple<BaseItem, List<BaseItem>>(
            null!,
            [new Movie { Name = "old", DateCreated = T0 }, new Movie { Name = "new", DateCreated = T0.AddDays(5) }]
        );

        var merged = UserViewManagerDecorator.Merge([[Added("Film", 4)], [show]], null, 10);

        Assert.Equal("old", merged[0].Item2[0].Name);
    }

    [Fact]
    public void DropsWhatWasNotAskedForLikeAlbumsInTheMoviesRow()
    {
        var album = new Tuple<BaseItem, List<BaseItem>>(
            null!,
            [new MusicAlbum { Name = "The Black Parade", DateCreated = T0.AddDays(20) }]
        );

        var merged = UserViewManagerDecorator.Merge([[album], [Added("Training Day", 9)]], [BaseItemKind.Movie], 10);

        Assert.Equal(["Training Day"], merged.Select(group => group.Item2[0].Name));
    }

    [Fact]
    public void AShowCountsForTheEpisodesRow()
    {
        var show = new Tuple<BaseItem, List<BaseItem>>(
            null!,
            [new MediaBrowser.Controller.Entities.TV.Series { Name = "Rick and Morty", DateCreated = T0.AddDays(2) }]
        );

        var merged = UserViewManagerDecorator.Merge([[show], [Added("Film", 4)]], [BaseItemKind.Episode], 10);

        Assert.Equal(["Rick and Morty"], merged.Select(group => group.Item2[0].Name));
    }
}
