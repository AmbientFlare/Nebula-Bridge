using System.Collections.Concurrent;

namespace NebulaBridge.Services;

/// <summary>
/// In-flight deduplication with joiner-owned cancellation. The first caller for a key starts
/// the work; every later caller for the same key awaits the same task at whatever stage it has
/// reached. The work is not tied to any one caller's token: it is cancelled only when every
/// joiner has left, or when <paramref name="shutdown"/> fires.
/// </summary>
public sealed class CoalescedFlights<TKey, TResult>(CancellationToken shutdown = default)
    where TKey : notnull
{
    private sealed class Flight(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public Task<TResult> Task { get; set; } = null!;

        public int Joiners { get; set; }

        public bool Closed { get; set; }
    }

    private readonly ConcurrentDictionary<TKey, Flight> _flights = new();

    internal int InFlightCount => _flights.Count;

    /// <summary>
    /// Runs <paramref name="work"/> for <paramref name="key"/>, or joins the run already in
    /// flight for it. <paramref name="onJoined"/> is called when this caller attached to
    /// existing work rather than starting it.
    /// </summary>
    public async Task<TResult> JoinAsync(
        TKey key,
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken,
        Action? onJoined = null
    )
    {
        while (true)
        {
            var created = false;
            var flight = _flights.GetOrAdd(
                key,
                _ =>
                {
                    created = true;
                    return new Flight(CancellationTokenSource.CreateLinkedTokenSource(shutdown));
                });

            bool joinable;
            lock (flight)
            {
                if (created)
                {
                    flight.Joiners = 1;
                    flight.Task = RunAsync(key, flight, work);
                }
                else if (!flight.Closed)
                {
                    flight.Joiners++;
                }

                joinable = created || !flight.Closed;
            }

            if (!joinable)
            {
                // A flight whose joiners all left is on its way out; let it finish unwinding,
                // then start a fresh one.
                await Task.Yield();
                continue;
            }

            if (!created)
                onJoined?.Invoke();

            try
            {
                return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Leave(flight);
            }
        }
    }

    private static void Leave(Flight flight)
    {
        lock (flight)
        {
            flight.Joiners--;
            if (flight.Joiners > 0 || flight.Closed)
                return;

            flight.Closed = true;
            if (!flight.Task.IsCompleted)
                flight.Cts.Cancel();
        }
    }

    private async Task<TResult> RunAsync(TKey key, Flight flight, Func<CancellationToken, Task<TResult>> work)
    {
        try
        {
            await Task.Yield();
            return await work(flight.Cts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (flight)
            {
                flight.Closed = true;
            }

            _flights.TryRemove(new KeyValuePair<TKey, Flight>(key, flight));
            flight.Cts.Dispose();
        }
    }
}
