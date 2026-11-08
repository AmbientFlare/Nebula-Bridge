using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class DebridMediaFileSelectorTests
{
    [Fact]
    public void MovieUsesTitleAndYearBeforeLargestUnrelatedVideo()
    {
        var selected = DebridMediaFileSelector.Select(
            [
                new(1, "Unrelated.Movie.2025.mkv", 5000),
                new(2, "Night.of.the.Living.Dead.1968.mp4", 1500),
                new(3, "sample.mkv", 9000),
                new(4, "poster.jpg", 100),
            ],
            new NativeMediaQuery("Night of the Living Dead", Year: 1968)
        );

        Assert.Equal(2, selected?.Id);
    }

    [Fact]
    public void EpisodeRequiresExactMatchWhenPackHasMultipleVideos()
    {
        var selected = DebridMediaFileSelector.Select(
            [new(1, "Show.S02E04.mkv", 1000), new(2, "Show.S02E06.mkv", 1100)],
            new NativeMediaQuery("Show", Season: 2, Episode: 5)
        );

        Assert.Null(selected);
    }

    [Fact]
    public void EpisodeRejectsCorrectNumberFromWrongShow()
    {
        var selection = DebridMediaFileSelector.SelectWithDiagnostics(
            [new(1, "The.President.Carter.S01E01.1080p.WEB-DL.mkv", 1000)],
            new NativeMediaQuery("Ted Lasso", Season: 1, Episode: 1)
        );

        Assert.Null(selection.File);
        Assert.Equal("media_title_mismatch", selection.Reason);
    }

    [Theory]
    [InlineData("Ted.Laso.S01E01.1080p.mkv")]
    [InlineData("TedLasso.S01E01.WEB-DL.mkv")]
    public void EpisodeAcceptsLenientTitleVariants(string filename)
    {
        var selected = DebridMediaFileSelector.Select(
            [new(1, filename, 1000)],
            new NativeMediaQuery("Ted Lasso", Season: 1, Episode: 1)
        );

        Assert.NotNull(selected);
    }

    [Fact]
    public void EpisodeAcceptsRequestedImdbIdentifierAsTitleSignal()
    {
        var selected = DebridMediaFileSelector.Select(
            [new(1, "tt10986410.S01E01.1080p.mkv", 1000)],
            new NativeMediaQuery(
                "Ted Lasso",
                Season: 1,
                Episode: 1,
                ImdbId: "tt10986410"
            )
        );

        Assert.NotNull(selected);
    }

    [Fact]
    public void EpisodeNumberWithoutTitleSignalIsRejected()
    {
        var selected = DebridMediaFileSelector.Select(
            [new(1, "S01E01.1080p.mkv", 1000)],
            new NativeMediaQuery("Ted Lasso", Season: 1, Episode: 1)
        );

        Assert.Null(selected);
    }

    [Fact]
    public void MovieRejectsUnrelatedOnlyVideo()
    {
        var selected = DebridMediaFileSelector.Select(
            [new(1, "The.President.Carter.2021.1080p.mkv", 1000)],
            new NativeMediaQuery("Night of the Living Dead", Year: 1968)
        );

        Assert.Null(selected);
    }

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("movie.mp4")]
    [InlineData("movie.m4v")]
    [InlineData("movie.avi")]
    [InlineData("movie.mov")]
    [InlineData("movie.ts")]
    [InlineData("movie.m2ts")]
    [InlineData("movie.webm")]
    public void RecognizesSupportedVideoExtensions(string name)
    {
        Assert.NotNull(
            DebridMediaFileSelector.Select([new DebridFile(1, name, 100)], new("Movie"))
        );
    }
}
