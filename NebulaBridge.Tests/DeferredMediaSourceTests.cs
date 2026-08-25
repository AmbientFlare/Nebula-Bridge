using System.Globalization;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using NebulaBridge.Decorators;

namespace NebulaBridge.Tests;

/// <summary>
/// An item that exists must never report zero media sources.
///
/// Stock DtoService indexes MediaSources[0] without checking the collection is
/// non-empty, so an empty list throws ArgumentOutOfRangeException, which the API
/// layer reports as HTTP 400. That kills the entire listing response — every row,
/// grid and search containing the item — not just the one item.
///
/// The placeholder these tests cover is what keeps that from happening, while
/// staying away from ffprobe until a client actually asks to play.
/// </summary>
public sealed class DeferredMediaSourceTests
{
    private static Movie NewItem() =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "Some Title",
            RunTimeTicks = 81216000000000,
        };

    [Fact]
    public void DeferredSourceIsNotEmptyAndCarriesTheItemIdentity()
    {
        var item = NewItem();

        var source = MediaSourceManagerDecorator.CreateDeferredSource(item, null);

        Assert.NotNull(source);
        Assert.Equal(item.Id.ToString("N", CultureInfo.InvariantCulture), source.Id);
        Assert.Equal(item.Name, source.Name);
        Assert.Equal(item.RunTimeTicks, source.RunTimeTicks);
    }

    [Fact]
    public void DeferredSourceIsNeverProbedBeforeItIsOpened()
    {
        // RequiresOpening keeps Jellyfin from materialising the source during a
        // listing; SupportsProbing=false keeps ffprobe away from it entirely.
        var source = MediaSourceManagerDecorator.CreateDeferredSource(NewItem(), null);

        Assert.True(source.RequiresOpening);
        Assert.False(source.SupportsProbing);
        Assert.True(source.IsRemote);
    }

    [Fact]
    public void DeferredSourceNeverExposesTheInternalStubPath()
    {
        // ffprobe cannot open nebulabridge://; handing it over turns "no cached
        // source" into a broken transcode session.
        var source = MediaSourceManagerDecorator.CreateDeferredSource(NewItem(), null);

        Assert.Null(source.Path);
        Assert.False(MediaSourceManagerDecorator.IsInternalStubPath(source.Path));
    }

    [Fact]
    public void DeferredSourceDoesNotClaimToBeDirectlyPlayable()
    {
        var source = MediaSourceManagerDecorator.CreateDeferredSource(NewItem(), null);

        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.Empty(source.MediaStreams);
    }

    [Fact]
    public void OpenTokenRoundTripsBackToTheItem()
    {
        var item = NewItem();

        var source = MediaSourceManagerDecorator.CreateDeferredSource(item, null);
        var parsed = MediaSourceManagerDecorator.TryParseDeferredToken(
            source.OpenToken,
            out var itemId
        );

        Assert.True(parsed);
        Assert.Equal(item.Id, itemId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone-elses-token")]
    [InlineData("nebulabridge|")]
    [InlineData("nebulabridge|not-a-guid")]
    public void TokensThisDecoratorDidNotIssueAreDelegated(string? token)
    {
        // Anything unrecognised must fall through to the inner manager rather
        // than being claimed or throwing.
        Assert.False(MediaSourceManagerDecorator.TryParseDeferredToken(token, out var itemId));
        Assert.Equal(Guid.Empty, itemId);
    }
}
