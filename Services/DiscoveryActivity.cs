namespace NebulaBridge.Services;

/// <summary>
/// Counts source discoveries that a viewer is waiting on, so work nobody is waiting on (prefetch)
/// can yield to them. Interactive discoveries register on entry and raise
/// <see cref="InteractiveStarted"/>; the count drops when they finish.
/// </summary>
public sealed class DiscoveryActivity
{
    private int _interactive;

    /// <summary>Raised when an interactive discovery begins.</summary>
    public event Action? InteractiveStarted;

    public int InteractiveCount => Volatile.Read(ref _interactive);

    public IDisposable BeginInteractive()
    {
        Interlocked.Increment(ref _interactive);
        InteractiveStarted?.Invoke();
        return new Scope(this);
    }

    private sealed class Scope(DiscoveryActivity owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref owner._interactive);
        }
    }
}
