using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class DiscoveryActivityTests
{
    [Fact]
    public void ScopesCountWhileOpenAndAnnounceEachStart()
    {
        var activity = new DiscoveryActivity();
        var announced = 0;
        activity.InteractiveStarted += () => announced++;

        var first = activity.BeginInteractive();
        var second = activity.BeginInteractive();
        Assert.Equal(2, activity.InteractiveCount);
        Assert.Equal(2, announced);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, activity.InteractiveCount);

        second.Dispose();
        Assert.Equal(0, activity.InteractiveCount);
        Assert.Equal(2, announced);
    }
}
