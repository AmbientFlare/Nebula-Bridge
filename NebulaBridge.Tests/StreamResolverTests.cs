using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class StreamResolverTests
{
    [Fact]
    public async Task DirectHttpsMediaRequiresSafeNetworkTarget()
    {
        var validator = new RecordingValidator();
        var resolver = new DirectHttpStreamResolver(validator);
        var candidate = new NativeReleaseCandidate(
            "fixture",
            "Open movie",
            new Uri("https://media.example/open-movie.mp4"),
            "http"
        );

        var result = await resolver.ResolveAsync(
            candidate,
            new NativeMediaQuery("Open movie"),
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Equal(1, validator.Calls);
    }

    [Theory]
    [InlineData("http://media.example/open-movie.mp4")]
    [InlineData("https://media.example/readme.txt")]
    public async Task DirectResolverRejectsUnsafeProtocolOrNonMediaPath(string url)
    {
        var validator = new RecordingValidator();
        var resolver = new DirectHttpStreamResolver(validator);

        var result = await resolver.ResolveAsync(
            new NativeReleaseCandidate("fixture", "Invalid", new Uri(url)),
            new NativeMediaQuery("Invalid"),
            CancellationToken.None
        );

        Assert.Null(result);
        Assert.Equal(0, validator.Calls);
    }

    [Fact]
    public async Task DirectResolverDropsPrivateTargets()
    {
        var resolver = new DirectHttpStreamResolver(new RejectingValidator());

        var result = await resolver.ResolveAsync(
            new NativeReleaseCandidate(
                "fixture",
                "Private",
                new Uri("https://private.example/video.mkv")
            ),
            new NativeMediaQuery("Private"),
            CancellationToken.None
        );

        Assert.Null(result);
    }

    private sealed class RecordingValidator : INetworkTargetValidator
    {
        public int Calls { get; private set; }

        public Task ValidateAsync(Uri uri, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingValidator : INetworkTargetValidator
    {
        public Task ValidateAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Private targets are blocked."));
    }
}
