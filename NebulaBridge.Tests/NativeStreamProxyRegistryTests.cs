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
    public void IdleProxyRegistrationsExpire()
    {
        var registry = new NativeStreamProxyRegistry();
        var proxy = registry.Register(
            new NativeResolvedStream(
                "fixture",
                "Release",
                new Uri("https://cdn.example/release?token=secret")
            ),
            8096
        );
        var key = proxy.Segments[^1];

        Assert.Equal(1, registry.PruneExpired(DateTimeOffset.UtcNow.AddHours(7)));
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
    public void PreparedStreamProxyIdentityIncludesTheLogicalItem()
    {
        var registry = new NativeStreamProxyRegistry();
        var prepared = new NativePreparedStream(
            "fake:indexer",
            "Release · Fake",
            100,
            "release.mkv",
            DebridRequest: new("fake", BuildCandidate(), new("Release"))
        );
        var firstItem = Guid.NewGuid();
        var secondItem = Guid.NewGuid();

        var first = registry.Register(prepared, 8096, firstItem);
        var firstAgain = registry.Register(prepared, 8096, firstItem);
        var second = registry.Register(prepared, 8096, secondItem);

        Assert.Equal(first, firstAgain);
        Assert.NotEqual(first, second);
        Assert.True(registry.TryGetAcquisitionIdentity(first.Segments[^1], out var firstIdentity));
        Assert.True(registry.TryGetAcquisitionIdentity(second.Segments[^1], out var secondIdentity));
        Assert.Equal(firstItem, firstIdentity.ItemId);
        Assert.Equal(secondItem, secondIdentity.ItemId);
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

    [Fact]
    public async Task DurableCompletedFileRestoresADeadProxyWithoutPersistingItsUrl()
    {
        var provider = new FakeDebridProvider();
        var registry = new NativeStreamProxyRegistry([provider]);
        var key = "0123456789abcdef0123456789abcdef";
        var itemId = Guid.NewGuid();
        var handle = new DebridCompletedFileHandle(
            "fake",
            "remote",
            "file",
            "release.mkv",
            100);

        Assert.True(registry.RestoreCompletedFile(key, handle, itemId, "series"));
        var first = await registry.ResolveTargetAsync(key, false, default);
        var refreshed = await registry.ResolveTargetAsync(key, true, default);

        Assert.Equal(itemId, AssertAcquisitionIdentity(registry, key).ItemId);
        Assert.Equal(handle, first.Stream?.CompletedFileHandle);
        Assert.NotEqual(first.Stream?.Url, refreshed.Stream?.Url);
        Assert.Equal(2, provider.CompletedFileCalls);
    }

    [Fact]
    public void UnrangedGetBecomesPlaybackOnlyAfterAProbe()
    {
        var registry = new NativeStreamProxyRegistry();
        var proxy = registry.Register(
            new NativeResolvedStream("direct", "Release", new Uri("https://cdn.example/release")),
            8096);
        var key = proxy.Segments[^1];

        Assert.False(registry.ShouldBeginUnrangedPlayback(key));
        Assert.True(registry.ShouldBeginUnrangedPlayback(key));

        var other = registry.Register(
            new NativeResolvedStream("direct", "Other", new Uri("https://cdn.example/other")),
            8096);
        var otherKey = other.Segments[^1];
        registry.MarkProbe(otherKey);
        Assert.True(registry.ShouldBeginUnrangedPlayback(otherKey));
    }

    [Fact]
    public async Task StalledProviderFailsOverToTheAlternateRouteAndStaysThere()
    {
        var primary = new FakeDebridProvider("primary");
        var alternate = new FakeDebridProvider("alternate");
        var registry = new NativeStreamProxyRegistry([primary, alternate]);
        var proxy = registry.Register(
            new NativePreparedStream(
                "fake:indexer",
                "Release · Fake",
                100,
                "release.mkv",
                DebridRequest: new("primary", BuildCandidate(), new("Release"), Alternates: ["alternate"])),
            8096);
        var key = proxy.Segments[^1];

        var first = await registry.ResolveTargetAsync(key, false, CancellationToken.None);
        var failedOver = await registry.FailOverAsync(key, NativeStreamOpen.Stalled, CancellationToken.None);
        var again = await registry.ResolveTargetAsync(key, false, CancellationToken.None);

        Assert.Contains("/primary/", first.Stream?.Url.AbsolutePath);
        Assert.Contains("/alternate/", failedOver.Stream?.Url.AbsolutePath);
        Assert.Equal(failedOver.Stream, again.Stream);
        Assert.Equal(1, primary.ResolveCalls);
        Assert.Equal(1, alternate.ResolveCalls);
    }

    [Fact]
    public async Task FailOverWithoutAnAlternateFailsFastAndForgetsTheStalledUrl()
    {
        var primary = new FakeDebridProvider("primary");
        var registry = new NativeStreamProxyRegistry([primary]);
        var proxy = registry.Register(
            new NativePreparedStream(
                "fake:indexer",
                "Release · Fake",
                100,
                "release.mkv",
                DebridRequest: new("primary", BuildCandidate(), new("Release"))),
            8096);
        var key = proxy.Segments[^1];

        Assert.NotNull((await registry.ResolveTargetAsync(key, false, CancellationToken.None)).Stream);
        var failedOver = await registry.FailOverAsync(key, NativeStreamOpen.Rejected, CancellationToken.None);
        var again = await registry.ResolveTargetAsync(key, false, CancellationToken.None);

        Assert.Null(failedOver.Stream);
        // The stalled provider is backing off, so the next open does not hand out its URL either.
        Assert.Null(again.Stream);
        Assert.Equal(1, primary.ResolveCalls);
    }

    [Fact]
    public async Task DirectTargetsHaveNoFailover()
    {
        var registry = new NativeStreamProxyRegistry();
        var proxy = registry.Register(
            new NativeResolvedStream("direct", "Release", new Uri("https://cdn.example/release")),
            8096);

        var result = await registry.FailOverAsync(proxy.Segments[^1], NativeStreamOpen.Stalled, CancellationToken.None);

        Assert.Null(result.Stream);
        Assert.Equal("upstream", result.Failure?.Stage);
    }

    [Fact]
    public async Task CompletedFileFailsOverToTheAlternateHandle()
    {
        var owner = new FakeDebridProvider("owner");
        var alternate = new FakeDebridProvider("alternate");
        var registry = new NativeStreamProxyRegistry([owner, alternate]);
        var key = "0123456789abcdef0123456789abcdef";
        var ownerHandle = new DebridCompletedFileHandle("owner", "remote", "file", "release.mkv", 100);
        var alternateHandle = new DebridCompletedFileHandle("alternate", "remote-2", "file-2", "release.mkv", 100);

        Assert.True(registry.RestoreCompletedFile(key, ownerHandle, Guid.NewGuid(), null, [alternateHandle]));
        var first = await registry.ResolveTargetAsync(key, false, default);
        var failedOver = await registry.FailOverAsync(key, NativeStreamOpen.Faulted, default);

        Assert.Equal("owner", first.Stream?.SourceId);
        Assert.Equal("alternate", failedOver.Stream?.SourceId);
        Assert.Equal(alternateHandle, failedOver.Stream?.CompletedFileHandle);
    }

    private static NativeProxyAcquisitionIdentity AssertAcquisitionIdentity(
        NativeStreamProxyRegistry registry,
        string key)
    {
        Assert.True(registry.TryGetAcquisitionIdentity(key, out var identity));
        return identity;
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

    private sealed class FakeDebridProvider(string id = "fake") : IDebridProvider
    {
        public string Id => id;

        public string Name => "Fake";

        public bool Enabled => true;

        public bool Configured => true;

        public DebridProviderCapabilities Capabilities =>
            DebridProviderCapabilities.CachedAvailability
            | DebridProviderCapabilities.DirectStreamUrl
            | DebridProviderCapabilities.CompletedFileSource;

        public int ResolveCalls { get; private set; }
        public int CompletedFileCalls { get; private set; }

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
                        new Uri($"https://cdn.example/{Id}/release-{ResolveCalls}.mkv"),
                        100,
                        "release.mkv"
                    )
                )
            );
        }

        public Task<DebridCompletedFileSource?> ResolveCompletedFileSourceAsync(
            DebridCompletedFileHandle handle,
            CancellationToken cancellationToken)
        {
            CompletedFileCalls++;
            return Task.FromResult<DebridCompletedFileSource?>(new(
                new Uri($"https://cdn.example/{Id}/completed-{CompletedFileCalls}.mkv?token=private"),
                handle.ExpectedLength,
                true));
        }
    }
}
