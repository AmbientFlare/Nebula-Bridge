using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class DiscoveryPrefetchQueueTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static PrefetchIntent Opened(Guid? item = null) =>
        new(PrefetchTrigger.HierarchyOpened, item ?? Guid.NewGuid(), User);

    [Fact]
    public async Task DequeuesInArrivalOrderAndDropsDuplicates()
    {
        var queue = new DiscoveryPrefetchQueue();
        var a = Opened();
        var b = Opened();

        Assert.True(queue.Enqueue(a));
        Assert.True(queue.Enqueue(b));
        Assert.False(queue.Enqueue(a));
        Assert.Equal(2, queue.Count);

        Assert.Equal(a, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(b, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void TheSameItemForAnotherUserOrTriggerIsSeparateWork()
    {
        var queue = new DiscoveryPrefetchQueue();
        var item = Guid.NewGuid();

        Assert.True(queue.Enqueue(new PrefetchIntent(PrefetchTrigger.HierarchyOpened, item, User)));
        Assert.True(queue.Enqueue(new PrefetchIntent(PrefetchTrigger.PlaybackStarted, item, User)));
        Assert.True(queue.Enqueue(new PrefetchIntent(PrefetchTrigger.HierarchyOpened, item, Guid.NewGuid())));
        Assert.Equal(3, queue.Count);
    }

    [Fact]
    public async Task ABurstBeyondCapacityDropsTheOldest()
    {
        var queue = new DiscoveryPrefetchQueue(capacity: 2);
        var first = Opened();
        var second = Opened();
        var third = Opened();

        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.Enqueue(third);

        Assert.Equal(2, queue.Count);
        Assert.Equal(second, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(third, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequeuedWorkGoesToTheFront()
    {
        var queue = new DiscoveryPrefetchQueue();
        var interrupted = Opened();
        var waiting = Opened();

        queue.Enqueue(waiting);
        queue.Requeue(interrupted);

        Assert.Equal(interrupted, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(waiting, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DequeueWaitsForWorkAndHonoursCancellation()
    {
        var queue = new DiscoveryPrefetchQueue();
        var pending = queue.DequeueAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        var intent = Opened();
        queue.Enqueue(intent);
        Assert.Equal(intent, await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.DequeueAsync(cts.Token));
    }

    [Fact]
    public async Task SparesignalsFromDroppedEntriesDoNotYieldPhantomWork()
    {
        var queue = new DiscoveryPrefetchQueue(capacity: 1);
        var kept = Opened();
        queue.Enqueue(Opened());
        queue.Enqueue(kept);

        Assert.Equal(kept, await queue.DequeueAsync(CancellationToken.None));
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.DequeueAsync(cts.Token));
    }
}
