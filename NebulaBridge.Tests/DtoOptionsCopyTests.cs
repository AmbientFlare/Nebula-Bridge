using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using NebulaBridge.Decorators;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

/// <summary>
/// DtoOptions belongs to the caller and may be reused for later calls in the
/// same request, so the decorator must not write to it.
/// </summary>
public sealed class DtoOptionsCopyTests
{
    [Fact]
    public void ShallowCopyDoesNotAliasTheOriginal()
    {
        var original = new DtoOptions { EnableUserData = true, ImageTypeLimit = 3 };

        var copy = DtoServiceDecorator.ShallowCopy(original);
        copy.EnableUserData = false;

        Assert.True(original.EnableUserData);
        Assert.NotSame(original, copy);
    }

    [Fact]
    public void ShallowCopyPreservesTheSettingsThatAffectConversion()
    {
        var original = new DtoOptions
        {
            EnableUserData = true,
            EnableImages = false,
            ImageTypeLimit = 2,
            AddCurrentProgram = false,
        };

        var copy = DtoServiceDecorator.ShallowCopy(original);

        Assert.Equal(original.EnableUserData, copy.EnableUserData);
        Assert.Equal(original.EnableImages, copy.EnableImages);
        Assert.Equal(original.ImageTypeLimit, copy.ImageTypeLimit);
        Assert.Equal(original.AddCurrentProgram, copy.AddCurrentProgram);
        Assert.Equal(original.Fields, copy.Fields);
        Assert.Equal(original.ImageTypes, copy.ImageTypes);
    }
}


/// <summary>
/// A deferred placeholder deliberately carries a null path so ffprobe never
/// sees the internal scheme. Everything that inspects a source path has to
/// cope with that, or a single-item request (/Sessions/Playing, item detail)
/// throws NullReferenceException.
/// </summary>
public sealed class PatchNullSourcePathTests
{
    private static DtoServiceDecorator NewDecorator()
    {
        var decorator = (DtoServiceDecorator)
            RuntimeHelpers.GetUninitializedObject(typeof(DtoServiceDecorator));

        // Patch reads _manager up front; nothing in this path dereferences the
        // value because item and user are null.
        typeof(DtoServiceDecorator)
            .GetField("_manager", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(decorator, new Lazy<NebulaBridgeManager>(() => null!));

        return decorator;
    }

    private static Exception? InvokePatch(BaseItemDto dto, bool isList)
    {
        var patch = typeof(DtoServiceDecorator).GetMethod(
            "Patch",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        try
        {
            patch.Invoke(NewDecorator(), [dto, null, isList, null]);
            return null;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException;
        }
    }

    private static BaseItemDto StubDto(string? sourcePath) =>
        new()
        {
            LocationType = LocationType.Remote,
            Type = BaseItemKind.Movie,
            Path = "nebulabridge://stub/tt0000001",
            MediaSources = [new MediaSourceInfo { Path = sourcePath }],
        };

    [Fact]
    public void ANullSourcePathDoesNotThrowOnTheSingleItemPath()
    {
        Assert.Null(InvokePatch(StubDto(null), isList: false));
    }

    [Fact]
    public void ANullSourcePathDoesNotThrowOnTheListPath()
    {
        Assert.Null(InvokePatch(StubDto(null), isList: true));
    }

    [Fact]
    public void AnInternalStubPathStillMarksTheItemVirtual()
    {
        // Behaviour for a real internal stub source must be unchanged.
        var dto = StubDto("nebulabridge://stub/tt0000001:1:1");

        Assert.Null(InvokePatch(dto, isList: false));
        Assert.Equal(LocationType.Virtual, dto.LocationType);
        Assert.Null(dto.Path);
    }
}
