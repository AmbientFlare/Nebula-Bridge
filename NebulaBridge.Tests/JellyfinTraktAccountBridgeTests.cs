using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class JellyfinTraktAccountBridgeTests
{
    [Fact]
    public void MissingOptionalArrayIsNormalizedOnce()
    {
        var user = new TraktUserShape();

        Assert.True(
            JellyfinTraktAccountBridge.NormalizeNullableArray(user, "LocationsExcluded")
        );
        Assert.Empty(user.LocationsExcluded!);
        Assert.False(
            JellyfinTraktAccountBridge.NormalizeNullableArray(user, "LocationsExcluded")
        );
    }

    [Fact]
    public void UnknownOrNonArrayPropertyIsIgnored()
    {
        var user = new TraktUserShape();

        Assert.False(JellyfinTraktAccountBridge.NormalizeNullableArray(user, "Missing"));
        Assert.False(JellyfinTraktAccountBridge.NormalizeNullableArray(user, "AccessToken"));
    }

    private sealed class TraktUserShape
    {
        public string[]? LocationsExcluded { get; set; }

        public string AccessToken { get; set; } = string.Empty;
    }
}
