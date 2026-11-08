using System.Diagnostics;
using System.Net;

namespace NebulaBridge.NativeSources;

/// <summary>
/// Opens a provider URL for the native stream proxy with a bound on how long the provider may
/// take to answer with response headers. Only the wait for headers is bounded; once the body is
/// flowing the copy runs on the request's own cancellation. A stalled open is what held
/// Jellyfin's probe of a media source for minutes when a provider edge timed out.
/// </summary>
public static class NativeStreamOpen
{
    public const string Stalled = "upstream_stall";
    public const string Faulted = "upstream_error";
    public const string Rejected = "upstream_status";

    public static async Task<NativeStreamOpenResult> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        TimeSpan headerTimeout,
        CancellationToken cancellationToken)
    {
        // The linked token only governs the wait for headers: SocketsHttpHandler unregisters
        // from it once the status line and headers have arrived, so disposing the source here
        // never interrupts the body copy that follows.
        using var gate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gate.CancelAfter(headerTimeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, gate.Token)
                .ConfigureAwait(false);
            return new NativeStreamOpenResult(response, null, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NativeStreamOpenResult(null, Stalled, Stopwatch.GetElapsedTime(started));
        }
        catch (HttpRequestException)
        {
            // Not rethrown: the message can carry the signed provider URL.
            return new NativeStreamOpenResult(null, Faulted, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>A status the proxy passes through to the client as-is.</summary>
    public static bool IsServable(HttpStatusCode status) =>
        status is HttpStatusCode.OK
            or HttpStatusCode.PartialContent
            or HttpStatusCode.RequestedRangeNotSatisfiable;

    /// <summary>A status that means the URL itself went stale and the same provider should re-sign it.</summary>
    public static bool IsStaleLink(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound;
}

public sealed record NativeStreamOpenResult(HttpResponseMessage? Response, string? Failure, TimeSpan Elapsed)
{
    /// <summary>Failure reason for an answered open that the proxy cannot serve; null when servable.</summary>
    public string? Classify()
    {
        if (Failure is not null)
            return Failure;
        return Response is not null && NativeStreamOpen.IsServable(Response.StatusCode)
            ? null
            : NativeStreamOpen.Rejected;
    }
}
