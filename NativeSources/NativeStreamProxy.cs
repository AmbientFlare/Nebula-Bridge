using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NebulaBridge.NativeSources;

/// <summary>
/// Keeps provider download links inside the Jellyfin process. MediaSource paths contain only a
/// loopback URL with an opaque stable key, so signed provider URLs and query tokens never appear
/// in Jellyfin client responses or ffmpeg command logs.
/// </summary>
public sealed class NativeStreamProxyRegistry
{
    private readonly ConcurrentDictionary<string, ProxyEntry> _entries = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly IReadOnlyDictionary<string, IDebridProvider> _providers;

    public NativeStreamProxyRegistry()
        : this([])
    { }

    public NativeStreamProxyRegistry(IEnumerable<IDebridProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    public Uri Register(NativeResolvedStream stream, int jellyfinHttpPort)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var identity = $"{stream.SourceId}\n{stream.Name}\n{stream.Filename}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var key = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        _entries[key] = new ProxyEntry(stream.Url, null);
        return new Uri(
            $"http://127.0.0.1:{jellyfinHttpPort}/nebulabridge/native-stream/{key}",
            UriKind.Absolute
        );
    }

    public Uri Register(NativePreparedStream stream, int jellyfinHttpPort)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.DirectUrl is null && stream.DebridRequest is null)
        {
            throw new ArgumentException("A prepared stream requires a direct URL or debrid request.", nameof(stream));
        }

        var requestIdentity = stream.DebridRequest is null
            ? stream.DirectUrl?.AbsoluteUri
            : $"{stream.DebridRequest.Provider}\n{stream.DebridRequest.Candidate.InfoHash}\n"
                + $"{stream.DebridRequest.Query.Season}\n{stream.DebridRequest.Query.Episode}";
        var identity = $"{stream.SourceId}\n{stream.Name}\n{stream.Filename}\n{requestIdentity}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var key = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        _entries.AddOrUpdate(
            key,
            _ => new ProxyEntry(stream.DirectUrl, stream.DebridRequest),
            (_, existing) => existing.Update(stream.DirectUrl, stream.DebridRequest)
        );
        return new Uri(
            $"http://127.0.0.1:{jellyfinHttpPort}/nebulabridge/native-stream/{key}",
            UriKind.Absolute
        );
    }

    public bool TryGetTarget(string key, out Uri? target)
    {
        target = null;
        return key.Length == 32
            && key.All(Uri.IsHexDigit)
            && _entries.TryGetValue(key, out var entry)
            && (target = entry.DirectTarget) is not null;
    }

    public async Task<DebridPlaybackResult> ResolveTargetAsync(
        string key,
        bool forceRefresh,
        CancellationToken cancellationToken
    )
    {
        if (
            key.Length != 32
            || !key.All(Uri.IsHexDigit)
            || !_entries.TryGetValue(key, out var entry)
        )
        {
            return new DebridPlaybackResult(
                null,
                new NativeSourceFailure("proxy", "The stream selection was not found.", Stage: "selection", Reason: "not_found")
            );
        }

        if (entry.DirectTarget is not null)
        {
            return new DebridPlaybackResult(
                new NativeResolvedStream("direct", "Direct source", entry.DirectTarget)
            );
        }

        if (entry.Request is null || !_providers.TryGetValue(entry.Request.Provider, out var provider))
        {
            return new DebridPlaybackResult(
                null,
                new NativeSourceFailure("proxy", "The debrid provider is unavailable.", Stage: "provider", Reason: "not_found")
            );
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (
                !forceRefresh
                && entry.Resolved is not null
                && entry.ResolvedUntilUtc > DateTimeOffset.UtcNow
            )
            {
                return new DebridPlaybackResult(entry.Resolved);
            }

            var result = await provider
                .ResolvePlaybackAsync(entry.Request.Candidate, entry.Request.Query, cancellationToken)
                .ConfigureAwait(false);
            if (result.Stream is not null)
            {
                entry.Resolved = result.Stream;
                // TorBox URLs are temporary; this stays in memory and is refreshed before expiry.
                entry.ResolvedUntilUtc = DateTimeOffset.UtcNow.AddHours(2);
            }

            return result;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class ProxyEntry(Uri? directTarget, DebridPlaybackRequest? request)
    {
        public Uri? DirectTarget { get; private set; } = directTarget;

        public DebridPlaybackRequest? Request { get; private set; } = request;

        public NativeResolvedStream? Resolved { get; set; }

        public DateTimeOffset ResolvedUntilUtc { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ProxyEntry Update(Uri? target, DebridPlaybackRequest? newRequest)
        {
            DirectTarget = target;
            Request = newRequest;
            if (target is not null)
            {
                Resolved = null;
                ResolvedUntilUtc = default;
            }

            return this;
        }
    }
}

/// <summary>A no-logging, no-redirect HTTP client used only by the loopback media proxy.</summary>
public sealed class NativeStreamProxyHttpClient : IDisposable
{
    public NativeStreamProxyHttpClient()
    {
        Client = new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
            },
            disposeHandler: true
        )
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}
