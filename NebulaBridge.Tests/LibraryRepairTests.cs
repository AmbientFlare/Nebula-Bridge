using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using NebulaBridge.Config;
using NebulaBridge.ScheduledTasks;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class LibraryRepairTests
{
    [Fact]
    public void IdentityPathUsesStremioIdInsideTheScope()
    {
        var series = new Series { Path = "/ignored", ProviderIds = { ["Stremio"] = "tt13111078" } };
        Assert.Equal(
            "nebulabridge://catalog-next-episodes/tt13111078",
            NebulaBridgeManager.BuildIdentityPath(series, "catalog-next-episodes")
        );
    }

    [Fact]
    public void IdentityPathFallsBackToTheCurrentPathAndEscapesIt()
    {
        var movie = new Movie { Path = "trakt:movies/1 2" };
        Assert.Equal(
            "nebulabridge://catalog-trending/trakt%3Amovies%2F1%202",
            NebulaBridgeManager.BuildIdentityPath(movie, "catalog-trending")
        );
    }

    [Fact]
    public void LegacyPrefixMapsOntoTheCurrentIdentityScheme()
    {
        const string legacy = LibraryRepairService.LegacyIdentityPrefix + "stub/tt13111078";
        var migrated = string.Concat(
            LibraryRepairService.IdentityPrefix,
            legacy.AsSpan(LibraryRepairService.LegacyIdentityPrefix.Length)
        );
        Assert.Equal("nebulabridge://stub/tt13111078", migrated);
    }

    [Fact]
    public void RepairTaskIsManualOnly()
    {
        var task = new RepairLibraryTask(null!, null!);
        Assert.Equal("NebulaBridgeRepairLibrary", task.Key);
        Assert.Empty(task.GetDefaultTriggers());
    }
}

public class ConsolidatedLibraryTests
{
    [Theory]
    [InlineData("trakt-movies-trending", "movie", false, true)]
    [InlineData("trakt-shows-popular", "series", false, true)]
    [InlineData("trakt-movies-anticipated", "movie", false, false)]
    [InlineData("trakt-watchlist-shows", "series", true, false)]
    [InlineData("trakt-progress-shows", "series", true, false)]
    public void OnlyPublicTrendingAndPopularFeedsStartEnabled(
        string id,
        string type,
        bool requiresAccount,
        bool expected
    )
    {
        var definition = new TraktCatalogDefinition(id, type, id, "path", null, requiresAccount);
        Assert.Equal(expected, CatalogService.IsDefaultEnabledSource(definition));
    }

    [Fact]
    public void SharedSourcesNeverGetTheirOwnFolder()
    {
        Assert.False(new CatalogConfig().SeparateLibrary);
    }

    [Theory]
    [InlineData("nebulabridge://catalog-trending/tt0111161", "nebulabridge://catalog-trending/")]
    [InlineData("nebulabridge://movies/tt0111161", "nebulabridge://movies/")]
    [InlineData("nebulabridge://shows/tt0903747%3A1%3A1", "nebulabridge://shows/")]
    [InlineData("nebulabridge://stub/tt0111161", null)]
    [InlineData("/config/media/movies/Sintel/Sintel.mkv", null)]
    [InlineData(null, null)]
    public void IdentityScopePrefixIsOnlyReadFromScopedPaths(string? path, string? expected)
    {
        Assert.Equal(expected, NebulaBridgeManager.GetIdentityScopePrefix(path));
    }

    [Fact]
    public void ManagedViewsAreAppendedAfterNativeOnes()
    {
        var movies = Guid.NewGuid();
        var shows = Guid.NewGuid();
        var music = Guid.NewGuid();
        var nebulaMovies = Guid.NewGuid();
        var nebulaShows = Guid.NewGuid();
        var folders = new List<(Guid Id, string Name)>
        {
            (nebulaShows, "Nebula Bridge — Shows"),
            (shows, "Shows"),
            (nebulaMovies, "Nebula Bridge — Movies"),
            (movies, "Movies"),
            (music, "Music"),
        };
        var managed = new HashSet<Guid> { nebulaMovies, nebulaShows };

        var fresh = UserAccessService.BuildViewOrder([], folders, managed);
        Assert.Equal([movies, music, shows, nebulaMovies, nebulaShows], fresh);

        var customised = UserAccessService.BuildViewOrder([shows, movies], folders, managed);
        Assert.Equal([shows, movies, music, nebulaMovies, nebulaShows], customised);

        Assert.Null(UserAccessService.BuildViewOrder([movies, music, shows, nebulaShows, nebulaMovies], folders, managed));

        // A managed view that drifted ahead of native ones is moved back; removed views are dropped.
        var stale = Guid.NewGuid();
        var drifted = UserAccessService.BuildViewOrder([nebulaShows, movies, stale, nebulaMovies, shows], folders, managed);
        Assert.Equal([movies, shows, music, nebulaShows, nebulaMovies], drifted);
    }
}
