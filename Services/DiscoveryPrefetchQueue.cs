namespace NebulaBridge.Services;

public enum PrefetchTrigger
{
    /// <summary>A series or season page was opened; warm that user's next-up episode.</summary>
    HierarchyOpened,

    /// <summary>An episode started playing; warm the episodes that follow it.</summary>
    PlaybackStarted,
}

/// <summary>What to warm, resolved to concrete episodes only when it is dequeued.</summary>
public readonly record struct PrefetchIntent(PrefetchTrigger Trigger, Guid ItemId, Guid UserId);

/// <summary>
/// The prefetch backlog: one intent per (trigger, item, user), newest at the back, bounded so a
/// browsing burst cannot pile up minutes of indexer work. Preempted work goes back to the front.
/// </summary>
public sealed class DiscoveryPrefetchQueue(int capacity = 16)
{
    private readonly LinkedList<PrefetchIntent> _intents = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _intents.Count;
            }
        }
    }

    /// <summary>Adds an intent at the back; a duplicate keeps its place. Returns whether it was added.</summary>
    public bool Enqueue(PrefetchIntent intent)
    {
        lock (_gate)
        {
            if (_intents.Contains(intent))
                return false;

            _intents.AddLast(intent);
            while (_intents.Count > capacity)
                _intents.RemoveFirst();
        }

        _signal.Release();
        return true;
    }

    /// <summary>Puts an intent that was interrupted back at the front.</summary>
    public void Requeue(PrefetchIntent intent)
    {
        lock (_gate)
        {
            _intents.Remove(intent);
            _intents.AddFirst(intent);
            while (_intents.Count > capacity)
                _intents.RemoveLast();
        }

        _signal.Release();
    }

    public async Task<PrefetchIntent> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                // Dropped or deduplicated entries leave spare signals behind; skip them.
                if (_intents.First is { } first)
                {
                    _intents.RemoveFirst();
                    return first.Value;
                }
            }
        }
    }

    internal IReadOnlyList<PrefetchIntent> Snapshot()
    {
        lock (_gate)
        {
            return [.. _intents];
        }
    }
}
