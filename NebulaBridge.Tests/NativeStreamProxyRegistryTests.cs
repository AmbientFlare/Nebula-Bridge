using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class NativeStreamProxyRegistryTests
{
    [Fact]
    public void RegistrationReturnsStableLoopbackUrlAndKeepsProviderUrlPrivate()
    {
        var registry = new NativeStreamProxyRegistry();
        var first = new NativeResolvedStream(
            "torbox:test",
            "Release",
            new Uri("https://cdn.example/first?token=secret-one"),
            100,
            "release.mp4"
        );
        var refreshed = first with
        {
            Url = new Uri("https://cdn.example/second?token=secret-two"),
        };

        var firstProxy = registry.Register(first, 8096);
        var refreshedProxy = registry.Register(refreshed, 8096);

        Assert.Equal(firstProxy, refreshedProxy);
        Assert.Equal("127.0.0.1", firstProxy.Host);
        Assert.Equal(8096, firstProxy.Port);
        Assert.DoesNotContain("secret", firstProxy.AbsoluteUri, StringComparison.Ordinal);
        var key = firstProxy.Segments[^1];
        Assert.True(registry.TryGetTarget(key, out var target));
        Assert.Equal(refreshed.Url, target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("0123456789abcdef")]
    public void InvalidProxyKeysAreRejected(string key)
    {
        var registry = new NativeStreamProxyRegistry();
        Assert.False(registry.TryGetTarget(key, out _));
    }

    [Fact]
    public async Task DeferredDebridResolutionRunsOnlyAfterSelectionAndIsCachedInMemory()
    {
        var provider = new FakeDebridProvider();
        var registry = new NativeStreamProxyRegistry([provider]);
        var candidate = BuildCandidate();
        var prepared = new NativePreparedStream(
            "fake:indexer",
            "Release · Fake",
            100,
            "release.mkv",
            DebridRequest: new DebridPlaybackRequest("fake", candidate, new("Release"))
        );

        var proxy = registry.Register(prepared, 8096);

        Assert.Equal(0, provider.ResolveCalls);
        var key = proxy.Segments[^1];
        var first = await registry.ResolveTargetAsync(key, false, CancellationToken.None);
        var second = await registry.ResolveTargetAsync(key, false, CancellationToken.None);
        Assert.NotNull(first.Stream);
        Assert.Equal(first.Stream, second.Stream);
        Assert.Equal(1, provider.ResolveCalls);
    }

    [Fact]
    public async Task ForcedRefreshReplacesExpiredProviderUrl()
    {
        var provider = new FakeDebridProvider();
        var registry = new NativeStreamProxyRegistry([provider]);
        var candidate = BuildCandidate();
        var proxy = registry.Register(
            new NativePreparedStream(
                "fake:indexer",
                "Release · Fake",
                100,
                "release.mkv",
                DebridRequest: new("fake", candidate, new("Release"))
            ),
            8096
        );
        var key = proxy.Segments[^1];

        var first = await registry.ResolveTargetAsync(key, false, CancellationToken.None);
        var refreshed = await registry.ResolveTargetAsync(key, true, CancellationToken.None);

        Assert.NotEqual(first.Stream?.Url, refreshed.Stream?.Url);
        Assert.Equal(2, provider.ResolveCalls);
    }

    private static NativeReleaseCandidate BuildCandidate() =>
        new(
            "indexer",
            "Release",
            new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
            "torrent",
            "0123456789abcdef0123456789abcdef01234567",
            Availability: [new("fake", true, [new(1, "release.mkv", 100)])],
            Playable: true
        );

    private sealed class FakeDebridProvider : IDebridProvider
    {
        public string Id => "fake";

        public string Name => "Fake";

        public bool Enabled => true;

        public bool Configured => true;

        public int ResolveCalls { get; private set; }

        public Task<DebridCacheCheckResult> CheckCachedAsync(
            IReadOnlyCollection<string> infoHashes,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<DebridPlaybackResult> ResolvePlaybackAsync(
            NativeReleaseCandidate candidate,
            NativeMediaQuery query,
            CancellationToken cancellationToken
        )
        {
            ResolveCalls++;
            return Task.FromResult(
                new DebridPlaybackResult(
                    new NativeResolvedStream(
                        "fake:indexer",
                        "Release · Fake",
                        new Uri($"https://cdn.example/release-{ResolveCalls}.mkv"),
                        100,
                        "release.mkv"
                    )
                )
            );
        }
    }
}
