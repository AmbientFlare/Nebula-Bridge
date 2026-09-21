using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NebulaBridge.Controllers;
using NebulaBridge.Decorators;

namespace NebulaBridge.Tests;

public sealed class HierarchyCapabilityTests
{
    [Fact]
    public void CapabilityContractStartsAtVersionOneAndIsProviderNeutral()
    {
        var contract = new NebulaBridgeCapabilities(
            1,
            new NebulaBridgeFeatures(true, true, true, false)
        );

        Assert.Equal(1, contract.ApiVersion);
        Assert.True(contract.Features.HierarchyPrefetch);
        Assert.True(contract.Features.SeriesHydration);
        Assert.True(contract.Features.SeasonHydration);
        Assert.False(contract.Features.PlaybackPrefetch);
        Assert.DoesNotContain("TorBox", contract.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Debrid", contract.ToString(), StringComparison.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(contract);
        Assert.Contains("\"apiVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"hierarchyPrefetch\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ApiVersion\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityContractCanAdvertiseVersionsAndCallerAvailability()
    {
        var contract = new NebulaBridgeCapabilities(
            1,
            new NebulaBridgeFeatures(true, true, true, false),
            [1],
            new NebulaBridgeAvailability(true, true)
        );

        var json = JsonSerializer.Serialize(contract);
        Assert.Contains("\"supportedVersions\":[1]", json, StringComparison.Ordinal);
        Assert.Contains("\"hierarchyPrefetchAllowed\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TorBox", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(nameof(NebulaBridgeApiController.GetCapabilities))]
    [InlineData(nameof(NebulaBridgeApiController.HydrateSeries))]
    [InlineData(nameof(NebulaBridgeApiController.HydrateSeason))]
    public void CapabilityAndHydrationEndpointsRequireAuthentication(string methodName)
    {
        var method = typeof(NebulaBridgeApiController).GetMethod(methodName)!;

        Assert.NotNull(method.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void UnpinnedPlaybackInfoPreservesAllStandardMediaSources()
    {
        var first = new MediaSourceInfo { Id = "one", Name = "1080p" };
        var second = new MediaSourceInfo { Id = "two", Name = "2160p" };

        var result = MediaSourceManagerDecorator.SelectPlaybackResponseSources(
            [first, second],
            first,
            false
        );

        Assert.Equal([first, second], result);
    }

    [Fact]
    public void PinnedPlaybackInfoReturnsOnlyTheRequestedSource()
    {
        var first = new MediaSourceInfo { Id = "one", Name = "1080p" };
        var second = new MediaSourceInfo { Id = "two", Name = "2160p" };

        var result = MediaSourceManagerDecorator.SelectPlaybackResponseSources(
            [first, second],
            second,
            true
        );

        Assert.Equal([second], result);
    }

    [Fact]
    public void ExplicitUserQueryFallsBackFromEmptyApiKeyPrincipal()
    {
        var userId = Guid.NewGuid();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("UserId", Guid.Empty.ToString())])
            ),
        };
        context.Request.QueryString = new QueryString($"?userId={userId:N}");

        Assert.True(context.TryGetUserId(out var resolved));
        Assert.Equal(userId, resolved);
    }
}
