using System.Net;
using System.Net.Http.Headers;

namespace NebulaBridge.Services;

public sealed record CacheWritePlan(long Start, long Length, long? TotalLength);

/// <summary>Validates the exact byte location before an upstream body may enter sparse storage.</summary>
public static class CacheHttpResponseValidator
{
    public static CacheWritePlan? Validate(
        HttpStatusCode status,
        ContentRangeHeaderValue? contentRange,
        long? contentLength,
        string? contentType,
        long? requestedStart,
        long? requestedLength,
        long? expectedLength)
    {
        if (contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            || contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            return null;

        if (requestedStart is { } start && requestedLength is { } length)
        {
            var end = checked(start + length - 1);
            if (status != HttpStatusCode.PartialContent || contentRange?.From != start
                || contentRange.To != end
                || contentLength is { } declaredLength && declaredLength != length)
                return null;
            if (expectedLength is { } expected
                && (contentRange.Length != expected || end >= expected))
                return null;
            return new(start, length, contentRange.Length);
        }

        if (status != HttpStatusCode.OK || contentLength is not > 0) return null;
        if (expectedLength is { } known && contentLength != known) return null;
        return new(0, contentLength.Value, contentLength);
    }
}
