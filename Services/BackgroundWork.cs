using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;

namespace NebulaBridge.Services;

/// <summary>
/// Owns the plugin's fire-and-forget work: metadata refreshes, catalog imports, discovery
/// promotion, user-access reconciliation, subtitle prewarm. Every run gets a token that is
/// cancelled when the host stops, failures are logged under the run's name instead of being lost,
/// and <see cref="StopAsync"/> waits for the runs (bounded by the host's shutdown token) so no
/// hierarchy write is torn by a restart. Acquisition completions have their own owner in
/// <see cref="AcquisitionCoordinator"/>.
/// </summary>
public sealed class BackgroundWork(ILogger<BackgroundWork> logger) : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextId;

    /// <summary>Cancelled once the host asks the plugin to stop.</summary>
    public CancellationToken Shutdown => _shutdown.Token;

    internal int RunningCount => _running.Count;

    /// <summary>
    /// Starts <paramref name="work"/> and returns its completion. Once shutdown has begun the
    /// work is skipped and a completed task is returned, so stopping never spawns new work.
    /// </summary>
    public Task Run(string name, Func<CancellationToken, Task> work, Guid? correlationId = null)
    {
        if (_shutdown.IsCancellationRequested)
        {
            logger.LogDebug("Skipped background work {Name}: shutting down", name);
            return Task.CompletedTask;
        }

        var id = Interlocked.Increment(ref _nextId);
        var task = RunCoreAsync(id, name, work, correlationId);
        // The run removes itself; a synchronously completed run must not be re-added afterwards.
        if (!task.IsCompleted)
            _running.TryAdd(id, task);
        if (task.IsCompleted)
            _running.TryRemove(id, out _);
        return task;
    }

    private async Task RunCoreAsync(long id, string name, Func<CancellationToken, Task> work, Guid? correlationId)
    {
        try
        {
            await Task.Yield();
            await work(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            logger.LogDebug("Background work {Name} stopped for shutdown", name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background work {Name} failed", name);
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "background-work-failed",
                correlationId,
                new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["failureType"] = ex.GetType().Name,
                });
        }
        finally
        {
            _running.TryRemove(id, out _);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        var running = _running.Values.ToArray();
        if (running.Length == 0)
            return;
        try
        {
            await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "{Count} background work item(s) were still running when the host stopped waiting",
                _running.Count);
        }
    }

    public void Dispose() => _shutdown.Dispose();
}
