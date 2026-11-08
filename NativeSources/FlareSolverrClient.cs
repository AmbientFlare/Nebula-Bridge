using System.Text;
using System.Text.Json;
using NebulaBridge.Config;

namespace NebulaBridge.NativeSources;

public interface IFlareSolverrSettings
{
    Uri? Endpoint { get; }
}

public sealed class PluginFlareSolverrSettings : IFlareSolverrSettings
{
    public Uri? Endpoint
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(
                "NEBULA_BRIDGE_FLARESOLVERR_URL"
            );
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = NebulaBridgePlugin.Instance?.Configuration.FlareSolverrUrl;
            }

            return NormalizeEndpoint(configured);
        }
    }

    internal static Uri? NormalizeEndpoint(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var candidate = configured.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "http://" + candidate;
        }

        if (
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not ("" or "/" or "/v1" or "/v1/")
        )
        {
            return null;
        }

        return new UriBuilder(uri) { Path = "/v1", Query = "", Fragment = "" }.Uri;
    }
}

public sealed record FlareSolverrResponse(Uri ResponseUri, string Content);

public sealed class FlareSolverrClient(
    IHttpClientFactory httpClientFactory,
    IFlareSolverrSettings settings
)
{
    private const int MaxOuterResponseBytes = 6 * 1024 * 1024;
    private const int MaxSolutionResponseBytes = 2 * 1024 * 1024;
    private const int SolverTimeoutMilliseconds = 60_000;

    public async Task<FlareSolverrResponse> SendAsync(
        Uri target,
        HttpMethod method,
        string? postData,
        string? cookieHeader,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? requestHeaders = null
    )
    {
        var endpoint = settings.Endpoint
            ?? throw new InvalidOperationException(
                "This indexer requires a configured FlareSolverr endpoint."
            );
        if (method != HttpMethod.Get && method != HttpMethod.Post)
        {
            throw new InvalidOperationException("FlareSolverr supports only GET and POST requests.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["cmd"] = method == HttpMethod.Post ? "request.post" : "request.get",
            ["url"] = target.AbsoluteUri,
            ["maxTimeout"] = SolverTimeoutMilliseconds,
        };
        if (method == HttpMethod.Post)
        {
            payload["postData"] = postData ?? string.Empty;
        }
        var cookies = ParseCookies(cookieHeader);
        if (cookies.Count > 0)
        {
            payload["cookies"] = cookies;
        }
        if (requestHeaders is { Count: > 0 })
        {
            payload["headers"] = requestHeaders;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            ),
        };
        using var response = await httpClientFactory
            .CreateClient(nameof(FlareSolverrClient))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxOuterResponseBytes)
        {
            throw new InvalidDataException("FlareSolverr response exceeds the 6 MiB limit.");
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (bounded.Length + read > MaxOuterResponseBytes)
            {
                throw new InvalidDataException("FlareSolverr response exceeds the 6 MiB limit.");
            }

            bounded.Write(buffer, 0, read);
        }

        using var document = JsonDocument.Parse(bounded.ToArray());
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString()
            : null;
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(message)
                    ? "FlareSolverr could not solve the request."
                    : $"FlareSolverr could not solve the request: {message}"
            );
        }

        if (!root.TryGetProperty("solution", out var solution))
        {
            throw new InvalidDataException("FlareSolverr response is missing its solution.");
        }

        var solutionStatus = solution.TryGetProperty("status", out var solutionStatusElement)
            ? solutionStatusElement.GetInt32()
            : 0;
        if (solutionStatus is < 200 or >= 300)
        {
            throw new HttpRequestException(
                $"FlareSolverr target returned HTTP {solutionStatus}."
            );
        }

        var finalUrl = solution.TryGetProperty("url", out var urlElement)
            ? urlElement.GetString()
            : null;
        var content = solution.TryGetProperty("response", out var contentElement)
            ? contentElement.GetString()
            : null;
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var responseUri) || content is null)
        {
            throw new InvalidDataException("FlareSolverr returned an incomplete solution.");
        }

        if (Encoding.UTF8.GetByteCount(content) > MaxSolutionResponseBytes)
        {
            throw new InvalidDataException("Indexer response exceeds the 2 MiB limit.");
        }

        return new FlareSolverrResponse(responseUri, content);
    }

    private static IReadOnlyList<Dictionary<string, string>> ParseCookies(
        string? cookieHeader
    )
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return [];
        }

        var cookies = new List<Dictionary<string, string>>();
        foreach (var segment in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            var name = pair[0].Trim();
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            cookies.Add(new Dictionary<string, string>
            {
                ["name"] = name,
                ["value"] = pair[1].Trim(),
            });
        }

        return cookies;
    }
}
