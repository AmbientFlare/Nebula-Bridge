using System.Net;

namespace NebulaBridge.NativeSources;

/// <summary>
/// Recognises a Cloudflare interstitial in an ordinary HTTP response so the caller can retry
/// through FlareSolverr. Detection is deliberately conservative: a false positive costs a
/// pointless ~11s solver round trip, so a response only counts as a challenge when Cloudflare
/// itself is implicated, not merely because the site returned 403.
/// </summary>
internal static class CloudflareChallengeDetector
{
    /// <summary>Markers that only appear in a Cloudflare challenge page.</summary>
    private static readonly string[] BodyMarkers =
    [
        "cf_chl_opt",
        "cf-browser-verification",
        "challenge-platform",
        "Just a moment...",
        "Checking your browser before accessing",
        "Attention Required! | Cloudflare",
        "Enable JavaScript and cookies to continue",
    ];

    /// <summary>
    /// Statuses Cloudflare uses to serve a challenge. Any other status is a genuine answer from
    /// the origin (or a genuine error) and must not be retried through the solver.
    /// </summary>
    internal static bool IsChallengeStatus(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.TooManyRequests;

    /// <summary>
    /// True when the response carries a header Cloudflare only sets while mitigating. This is
    /// checked before the body so an obvious challenge costs no read at all.
    /// </summary>
    internal static bool HasChallengeHeaders(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // cf-mitigated: challenge is Cloudflare stating outright that it interrupted the request.
        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated))
        {
            foreach (var value in mitigated)
            {
                if (value.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when the body is a challenge page.</summary>
    internal static bool HasChallengeBody(string? content) =>
        !string.IsNullOrEmpty(content)
        && BodyMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="response"/> is a Cloudflare challenge rather than a real answer.
    /// <paramref name="content"/> may be null when the body has not been read.
    /// </summary>
    internal static bool IsChallenge(HttpResponseMessage response, string? content)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (HasChallengeHeaders(response))
        {
            return true;
        }

        // A challenge served with a normal-looking status still has to come from Cloudflare's
        // edge, so require the server header before trusting body markers alone.
        return HasChallengeBody(content) && IsServedByCloudflare(response);
    }

    private static bool IsServedByCloudflare(HttpResponseMessage response)
    {
        if (response.Headers.Contains("cf-ray"))
        {
            return true;
        }

        foreach (var value in response.Headers.Server)
        {
            if (
                value.Product?.Name is { } name
                && name.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }
}
