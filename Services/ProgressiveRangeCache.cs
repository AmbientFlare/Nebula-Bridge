using System.Collections.Concurrent;

namespace NebulaBridge.Services;

/// <summary>
/// One sparse staging representation shared by foreground playback and later completion.
/// The caller supplies provider bytes; this class persists only local bytes and ranges.
/// </summary>
public sealed class ProgressiveRangeCache
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public async Task<byte[]> ReadOrFetchAsync(
        AcquisitionJob job,
        long start,
        long length,
        Func<long, long, CancellationToken, Task<byte[]>> fetch,
        AcquisitionJobStore store,
        CancellationToken cancellationToken
    )
    {
        if (start < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        var end = checked(start + length - 1);
        var gate = _gates.GetOrAdd(job.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            job = (await store.ReadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == job.Id) ?? job;
            var path = Path.Combine(store.StagingPath, job.Id.ToString("N"), "media.partial");
            var ranges = job.CachedRanges ?? [];
            if (ranges.Any(range => range.Contains(start, end)) && File.Exists(path))
            {
                return await ReadAsync(path, start, length, cancellationToken).ConfigureAwait(false);
            }

            var bytes = await fetch(start, length, cancellationToken).ConfigureAwait(false);
            if (bytes.Length != length) throw new IOException("The provider returned an incomplete byte range.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteAsync(path, start, bytes, cancellationToken).ConfigureAwait(false);
            await store.MutateAsync(job.Id, current => current is null ? null : current with
            {
                BytesDownloaded = Math.Max(current.BytesDownloaded, end + 1),
                CachedRanges = Normalize([.. current.CachedRanges ?? [], new CachedByteRange(start, end)]),
                LastAccessUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        finally { gate.Release(); }
    }

    internal static IReadOnlyList<CachedByteRange> Normalize(IEnumerable<CachedByteRange> ranges)
    {
        var result = new List<CachedByteRange>();
        foreach (var range in ranges.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (result.LastOrDefault() is { } last && range.Start <= last.End + 1)
                result[^1] = new CachedByteRange(last.Start, Math.Max(last.End, range.End));
            else result.Add(range);
        }
        return result;
    }

    public async Task<byte[]?> TryReadAsync(AcquisitionJob job, long start, long length, AcquisitionJobStore store, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(job.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == job.Id) ?? job;
            if (!(job.CachedRanges ?? []).Any(range => range.Contains(start, start + length - 1))) return null;
            var path = Path.Combine(store.StagingPath, job.Id.ToString("N"), "media.partial");
            return File.Exists(path) ? await ReadAsync(path, start, length, ct).ConfigureAwait(false) : null;
        }
        finally { gate.Release(); }
    }

    public async Task StoreAsync(AcquisitionJob job, long start, byte[] bytes, AcquisitionJobStore store, CancellationToken ct)
    {
        if (bytes.Length == 0) return;
        var gate = _gates.GetOrAdd(job.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            job = (await store.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == job.Id) ?? job;
            var path = Path.Combine(store.StagingPath, job.Id.ToString("N"), "media.partial");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteAsync(path, start, bytes, ct).ConfigureAwait(false);
            var end = checked(start + bytes.Length - 1);
            await store.MutateAsync(job.Id, current => current is null ? null : current with
            {
                BytesDownloaded = Math.Max(current.BytesDownloaded, end + 1),
                CachedRanges = Normalize([.. current.CachedRanges ?? [], new(start, end)]),
                LastAccessUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static async Task<byte[]> ReadAsync(string path, long start, long length, CancellationToken ct)
    {
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length));
        var bytes = new byte[(int)length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        stream.Position = start;
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private static async Task WriteAsync(string path, long start, byte[] bytes, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
        stream.Position = start;
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }
}
