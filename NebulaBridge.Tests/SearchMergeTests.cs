using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NebulaBridge.Filters;

namespace NebulaBridge.Tests;

/// <summary>
/// Search returns the server's own matches first and the provider's after
/// them. A client that pages through the results must see every item once,
/// in that order, and a search narrowed by something only local items carry
/// must not be padded with provider results at all.
/// </summary>
public sealed class SearchMergeTests
{
    private static BaseItemDto[] Dtos(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => new BaseItemDto { Name = $"{prefix}{i}" }).ToArray();

    private static string[] Names(IEnumerable<BaseItemDto> items) =>
        items.Select(item => item.Name!).ToArray();

    /// <summary>Cuts the local page the way Jellyfin does, then merges.</summary>
    private static (BaseItemDto[] Items, int TotalCount) Page(
        BaseItemDto[] local,
        BaseItemDto[] extras,
        int start,
        int limit
    ) =>
        SearchActionFilter.MergePage(
            local.Skip(start).Take(limit).ToArray(),
            local.Length,
            extras,
            start,
            limit
        );

    [Fact]
    public void LocalResultsComeBeforeProviderResults()
    {
        var page = Page(Dtos("local", 2), Dtos("extra", 3), 0, 25);

        Assert.Equal(["local0", "local1", "extra0", "extra1", "extra2"], Names(page.Items));
        Assert.Equal(5, page.TotalCount);
    }

    [Theory]
    [InlineData(7, 4, 3)]
    [InlineData(7, 4, 7)]
    [InlineData(0, 5, 2)]
    [InlineData(10, 0, 3)]
    [InlineData(3, 3, 25)]
    public void PagingVisitsEveryItemOnceInOrder(int localCount, int extraCount, int limit)
    {
        var local = Dtos("local", localCount);
        var extras = Dtos("extra", extraCount);
        var seen = new List<string>();

        for (var start = 0; start < localCount + extraCount + limit; start += limit)
        {
            var page = Page(local, extras, start, limit);
            Assert.Equal(localCount + extraCount, page.TotalCount);
            Assert.True(page.Items.Length <= limit);
            seen.AddRange(Names(page.Items));
        }

        Assert.Equal(Names(local.Concat(extras)), seen);
    }

    [Fact]
    public void AnOwnedTitleIsNotShownAgainAsAProviderResult()
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mine = new Movie();
        mine.SetProviderId(MetadataProvider.Imdb, "tt0113277");
        var other = new Movie();
        other.SetProviderId(MetadataProvider.Imdb, "tt0000001");
        var mineAsStub = new Movie();
        mineAsStub.SetProviderId(MetadataProvider.Imdb, "tt0113277");

        owned.Add("Imdb:Movie:tt0113277");

        Assert.True(SearchActionFilter.IsOwned(mineAsStub, owned));
        Assert.False(SearchActionFilter.IsOwned(other, owned));
    }

    [Fact]
    public void AMovieAndASeriesSharingATmdbNumberAreDifferentTitles()
    {
        // TMDb numbers movies and series separately, so 1399 can be both.
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tmdb:Movie:1399" };
        var series = new Series();
        series.SetProviderId(MetadataProvider.Tmdb, "1399");

        Assert.False(SearchActionFilter.IsOwned(series, owned));
    }

    private static Series Show(string path, string imdb = "tt0903747")
    {
        var show = new Series { Id = Guid.NewGuid(), Path = path };
        show.SetProviderId(MetadataProvider.Imdb, imdb);
        return show;
    }

    [Fact]
    public void TheUsersOwnFileHidesNebulasCatalogueCopies()
    {
        var mine = Show("/config/local/tv/Breaking Bad");
        var catalogue = Show("nebulabridge://shows/tt0903747");
        var nextUp = Show("nebulabridge://catalog-next-episodes/tt0903747");
        var other = Show("nebulabridge://shows/tt0000001", "tt0000001");

        var hidden = SearchActionFilter.DuplicateCopies([nextUp, catalogue, mine, other]);

        Assert.Equal(new HashSet<Guid> { catalogue.Id, nextUp.Id }, hidden);
    }

    [Fact]
    public void WithoutAFileNebulasMainLibraryCopyIsKept()
    {
        var catalogue = Show("nebulabridge://shows/tt0903747");
        var nextUp = Show("nebulabridge://catalog-next-episodes/tt0903747");

        Assert.Equal(new HashSet<Guid> { nextUp.Id }, SearchActionFilter.DuplicateCopies([nextUp, catalogue]));
    }

    [Fact]
    public void ItemsWithoutIdsAreNeverTreatedAsCopies()
    {
        var a = new Movie { Id = Guid.NewGuid(), Path = "/a.mkv" };
        var b = new Movie { Id = Guid.NewGuid(), Path = "/b.mkv" };

        Assert.Empty(SearchActionFilter.DuplicateCopies([a, b]));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(25)]
    public void PagingWithHiddenCopiesVisitsEveryKeptItemOnce(int limit)
    {
        var local = Enumerable.Range(0, 9)
            .Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"local{i}" })
            .ToArray();
        var hidden = new HashSet<Guid> { local[1].Id, local[2].Id, local[7].Id };
        var extras = Dtos("extra", 4);
        var kept = local.Where(item => !hidden.Contains(item.Id)).ToArray();
        var seen = new List<string>();

        for (var start = 0; start < kept.Length + extras.Length + limit; start += limit)
        {
            // Jellyfin is asked from the first match for start + limit + hidden.
            var fromFirst = local.Take(start + limit + hidden.Count).ToArray();
            var cut = SearchActionFilter.CutLocalPage(fromFirst, hidden, start, limit);
            var page = SearchActionFilter.MergePage(cut, local.Length - hidden.Count, extras, start, limit);
            Assert.Equal(kept.Length + extras.Length, page.TotalCount);
            seen.AddRange(Names(page.Items));
        }

        Assert.Equal(Names(kept.Concat(extras)), seen.ToArray());
    }

    private static ActionExecutingContext Context(Dictionary<string, object?> arguments)
    {
        var action = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor()
        );
        return new ActionExecutingContext(action, [], arguments, controller: new object());
    }

    [Fact]
    public void APlainSearchIsNotNarrowed()
    {
        var ctx = Context(
            new()
            {
                ["searchTerm"] = "heat",
                ["parentId"] = Guid.NewGuid(),
                ["genreIds"] = Array.Empty<Guid>(),
                ["isFavorite"] = null,
                ["filters"] = Array.Empty<ItemFilter>(),
                ["includeItemTypes"] = new[] { BaseItemKind.Movie },
            }
        );

        Assert.False(SearchActionFilter.HasNarrowingFilter(ctx));
    }

    [Fact]
    public void AGenreKeepsTheSearchLocal()
    {
        var ctx = Context(new() { ["genreIds"] = new[] { Guid.NewGuid() } });
        Assert.True(SearchActionFilter.HasNarrowingFilter(ctx));
    }

    [Fact]
    public void WatchedAndFavouriteFiltersKeepTheSearchLocal()
    {
        Assert.True(
            SearchActionFilter.HasNarrowingFilter(
                Context(new() { ["filters"] = new[] { ItemFilter.IsUnplayed } })
            )
        );
        Assert.True(SearchActionFilter.HasNarrowingFilter(Context(new() { ["isFavorite"] = true })));
    }

    [Fact]
    public void AStructuralFilterDoesNotKeepTheSearchLocal()
    {
        var ctx = Context(new() { ["filters"] = new[] { ItemFilter.IsNotFolder } });
        Assert.False(SearchActionFilter.HasNarrowingFilter(ctx));
    }
}
