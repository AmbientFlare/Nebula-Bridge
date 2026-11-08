namespace NebulaBridge.Services;

/// <summary>Forwards media incrementally while optionally committing the same bounded chunks to cache.</summary>
public static class BoundedMediaTee
{
    public const int BufferBytes = 1024 * 1024;

    public static async Task CopyAsync(
        Stream source,
        Stream destination,
        long cacheOffset,
        Func<long, ReadOnlyMemory<byte>, CancellationToken, Task>? cacheWrite,
        CancellationToken ct,
        Action<Exception>? onCacheFailure = null)
    {
        var buffer = new byte[BufferBytes];
        var cacheEnabled = cacheWrite is not null;
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (cacheEnabled)
            {
                try { await cacheWrite!(cacheOffset, buffer.AsMemory(0, count), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { cacheEnabled = false; onCacheFailure?.Invoke(ex); }
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            cacheOffset += count;
        }
    }
}
