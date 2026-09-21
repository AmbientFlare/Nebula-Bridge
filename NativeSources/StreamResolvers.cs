namespace NebulaBridge.NativeSources;

public interface IStreamResolver
{
    Task<NativeResolvedStream?> ResolveAsync(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    );
}

public sealed class DirectHttpStreamResolver(INetworkTargetValidator targetValidator) : IStreamResolver
{
    private static readonly string[] MediaExtensions =
    [
        ".mkv",
        ".mp4",
        ".m4v",
        ".webm",
        ".m3u8",
        ".mpd",
    ];

    public async Task<NativeResolvedStream?> ResolveAsync(
        NativeReleaseCandidate candidate,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var playable = candidate.Link.Scheme is "http" or "https"
            && MediaExtensions.Any(extension =>
                candidate.Link.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            );
        if (!playable || candidate.Link.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            await targetValidator.ValidateAsync(candidate.Link, cancellationToken)
                .ConfigureAwait(false);
            return new NativeResolvedStream(
                candidate.SourceId,
                candidate.Title,
                candidate.Link,
                candidate.SizeBytes,
                Path.GetFileName(candidate.Link.AbsolutePath)
            );
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
