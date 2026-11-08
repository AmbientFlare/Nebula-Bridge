using Microsoft.Extensions.Logging.Abstractions;
using NebulaBridge.Services;

namespace NebulaBridge.Tests;

public sealed class BackgroundWorkTests
{
    [Fact]
    public async Task RunsAreTrackedUntilTheyFinishAndFailuresAreContained()
    {
        using var work = new BackgroundWork(NullLogger<BackgroundWork>.Instance);
        var gate = new TaskCompletionSource();

        var run = work.Run("held", async _ => await gate.Task);
        var failed = work.Run("failing", _ => throw new InvalidOperationException("boom"));

        await failed;
        Assert.Equal(1, work.RunningCount);
        gate.SetResult();
        await run;
        Assert.Equal(0, work.RunningCount);
    }

    [Fact]
    public async Task StopCancelsRunningWorkAndWaitsForIt()
    {
        using var work = new BackgroundWork(NullLogger<BackgroundWork>.Instance);
        var observed = new TaskCompletionSource<bool>();

        var run = work.Run("held", async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                observed.TrySetResult(token.IsCancellationRequested);
            }
        });

        await work.StopAsync(CancellationToken.None);

        Assert.True(await observed.Task);
        Assert.True(run.IsCompletedSuccessfully);
        Assert.Equal(0, work.RunningCount);
    }

    [Fact]
    public async Task WorkQueuedAfterStopIsSkipped()
    {
        using var work = new BackgroundWork(NullLogger<BackgroundWork>.Instance);
        await work.StopAsync(CancellationToken.None);
        var ran = false;

        var run = work.Run("late", _ => { ran = true; return Task.CompletedTask; });

        await run;
        Assert.False(ran);
        Assert.True(work.Shutdown.IsCancellationRequested);
    }

    [Fact]
    public async Task StopGivesUpWhenTheHostStopsWaiting()
    {
        using var work = new BackgroundWork(NullLogger<BackgroundWork>.Instance);
        var stuck = new TaskCompletionSource();
        var run = work.Run("stuck", _ => stuck.Task);
        using var hostDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await work.StopAsync(hostDeadline.Token);

        Assert.False(run.IsCompleted);
        Assert.Equal(1, work.RunningCount);
        stuck.SetResult();
        await run;
    }
}
