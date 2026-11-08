using System.Collections.Concurrent;

namespace NebulaBridge.Services;

/// <summary>Coordinates active cache users with exclusive purge/import/reconciliation transitions per job.</summary>
public sealed class AcquisitionActivityTracker
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public bool IsActive(Guid jobId) => _entries.TryGetValue(jobId, out var entry) && entry.IsActive;

    public async Task<IAsyncDisposable> AcquireUseAsync(Guid jobId, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(jobId, _ => new Entry());
        await entry.Transition.WaitAsync(ct).ConfigureAwait(false);
        try { entry.AddUse(); }
        finally { entry.Transition.Release(); }
        return new Lease(entry.ReleaseUse);
    }

    public async Task<IAsyncDisposable> AcquireExclusiveAsync(Guid jobId, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(jobId, _ => new Entry());
        await entry.Transition.WaitAsync(ct).ConfigureAwait(false);
        try { await entry.WaitForIdleAsync(ct).ConfigureAwait(false); }
        catch { entry.Transition.Release(); throw; }
        return new Lease(() => entry.Transition.Release());
    }

    private sealed class Entry
    {
        private readonly object _sync = new();
        private int _active;
        private TaskCompletionSource _idle = CompletedIdle();
        public SemaphoreSlim Transition { get; } = new(1, 1);
        public bool IsActive { get { lock (_sync) return _active > 0; } }

        public void AddUse()
        {
            lock (_sync)
            {
                if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseUse()
        {
            lock (_sync)
            {
                if (--_active == 0) _idle.TrySetResult();
            }
        }

        public Task WaitForIdleAsync(CancellationToken ct)
        {
            lock (_sync) return _active == 0 ? Task.CompletedTask : _idle.Task.WaitAsync(ct);
        }

        private static TaskCompletionSource CompletedIdle()
        {
            var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            value.SetResult();
            return value;
        }
    }

    private sealed class Lease(Action release) : IAsyncDisposable
    {
        private Action? _release = release;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
