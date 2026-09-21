using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class CoalescedFlightsTests
{
    [Fact]
    public async Task ConcurrentCallersShareOneRun()
    {
        var flights = new CoalescedFlights<Guid, int>();
        var key = Guid.NewGuid();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource<int>();
        var runs = 0;
        var joined = 0;

        Task<int> Work(CancellationToken _)
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            return gate.Task;
        }

        var first = flights.JoinAsync(key, Work, CancellationToken.None, () => joined++);
        await started.Task;
        var second = flights.JoinAsync(key, Work, CancellationToken.None, () => joined++);

        Assert.Equal(1, flights.InFlightCount);
        gate.SetResult(42);
        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
        Assert.Equal(1, runs);
        Assert.Equal(1, joined);

        // Wait for the flight to unwind, then a new caller starts fresh work.
        await WaitForAsync(() => flights.InFlightCount == 0);
        gate = new TaskCompletionSource<int>();
        var third = flights.JoinAsync(key, Work, CancellationToken.None, () => joined++);
        gate.SetResult(7);
        Assert.Equal(7, await third);
        Assert.Equal(2, runs);
        Assert.Equal(1, joined);
    }

    [Fact]
    public async Task OneCallerLeavingDoesNotCancelTheWorkOthersAwait()
    {
        var flights = new CoalescedFlights<Guid, string>();
        var key = Guid.NewGuid();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource<string>();
        CancellationToken workToken = default;

        Task<string> Work(CancellationToken token)
        {
            workToken = token;
            started.TrySetResult();
            return gate.Task;
        }

        using var leaver = new CancellationTokenSource();
        var leaving = flights.JoinAsync(key, Work, leaver.Token);
        await started.Task;
        var staying = flights.JoinAsync(key, Work, CancellationToken.None);

        leaver.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);
        Assert.False(workToken.IsCancellationRequested);

        gate.SetResult("answer");
        Assert.Equal("answer", await staying);
    }

    [Fact]
    public async Task WorkIsCancelledOnceEveryCallerHasLeft()
    {
        var flights = new CoalescedFlights<Guid, string>();
        var key = Guid.NewGuid();
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();

        async Task<string> Work(CancellationToken token)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return "unreachable";
        }

        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = flights.JoinAsync(key, Work, first.Token);
        await started.Task;
        var b = flights.JoinAsync(key, Work, second.Token);

        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(cancelled.Task.IsCompleted);

        second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => flights.InFlightCount == 0);
    }

    [Fact]
    public async Task ShutdownCancelsTheFlight()
    {
        using var shutdown = new CancellationTokenSource();
        var flights = new CoalescedFlights<Guid, string>(shutdown.Token);
        var started = new TaskCompletionSource();

        var run = flights.JoinAsync(
            Guid.NewGuid(),
            async token =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            },
            CancellationToken.None);

        await started.Task;
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task FailuresReachEveryJoinerAndClearTheFlight()
    {
        var flights = new CoalescedFlights<Guid, string>();
        var key = Guid.NewGuid();
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource<string>();

        Task<string> Work(CancellationToken _)
        {
            started.TrySetResult();
            return gate.Task;
        }

        var a = flights.JoinAsync(key, Work, CancellationToken.None);
        await started.Task;
        var b = flights.JoinAsync(key, Work, CancellationToken.None);

        gate.SetException(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => a);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b);
        await WaitForAsync(() => flights.InFlightCount == 0);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not met in time");
            await Task.Delay(10);
        }
    }
}
