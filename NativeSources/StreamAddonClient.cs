using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.NativeSources;

/// <summary>
/// Asks Stremio stream addons (Torrentio, Comet, MediaFusion, an AIOStreams instance…) for the
/// releases they already know for an IMDb id. Those services keep a pre-scraped database keyed by
/// IMDb id, season and episode, so one request answers in well under a second with the same
/// hashes an indexer sweep would take tens of seconds to find. Only hash-bearing streams are used:
/// a stream that is already a debrid URL belongs to somebody else's account.
/// </summary>
public sealed partial class StreamAddonClient(
    IHttpClientFactory httpClientFactory,
    ILogger<StreamAddonClient> logger
)
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Queries every configured addon in parallel and merges what they answered.</summary>
    public async Task<NativeSearchResult> SearchAsync(
        IReadOnlyList<string> addonUrls,
        NativeMediaQuery query,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        if (addonUrls.Count == 0 || string.IsNullOrWhiteSpace(query.ImdbId))
        {
            return new NativeSearchResult([], []);
        }

        var searches = addonUrls
            .Select(url => SearchOneAsync(url, query, timeout, cancellationToken))
            .ToList();
        var outcomes = await Task.WhenAll(searches).ConfigureAwait(false);
        var candidates = outcomes.SelectMany(outcome => outcome.Candidates).ToList();
        var failures = outcomes.Select(outcome => outcome.Failure).OfType<NativeSourceFailure>().ToList();
        return new NativeSearchResult(candidates, failures);
    }

    /// <summary>The stream URL an addon answers for this query, or null when the addon URL is unusable.</summary>
    internal static Uri? BuildStreamUri(string addonUrl, NativeMediaQuery query)
    {
        if (
            string.IsNullOrWhiteSpace(query.ImdbId)
            || !Uri.TryCreate(addonUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
        )
        {
            return null;
        }

        // The configured URL is the addon's base (what Stremio installs minus /manifest.json);
        // a pasted manifest URL is accepted too.
        var basePath = baseUri.AbsoluteUri.TrimEnd('/');
        if (basePath.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            basePath = basePath[..^"/manifest.json".Length];
        }

        var id = query.Season is { } season && query.Episode is { } episode
            ? $"{query.ImdbId}:{season}:{episode}"
            : query.ImdbId;
        var type = query.Season is not null ? "series" : "movie";
        return new Uri($"{basePath}/stream/{type}/{Uri.EscapeDataString(id)}.json", UriKind.Absolute);
    }

    private async Task<AddonOutcome> SearchOneAsync(
        string addonUrl,
        NativeMediaQuery query,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var sourceId = SourceIdFor(addonUrl);
        var uri = BuildStreamUri(addonUrl, query);
        if (uri is null)
        {
            return new([], new NativeSourceFailure(sourceId, "The addon URL is not a usable HTTP(S) address.", sourceId, "request", "invalid"));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "addon-search-start",
            fields: new Dictionary<string, object?> { ["addon"] = sourceId });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            var client = httpClientFactory.CreateClient(nameof(StreamAddonClient));
            using var response = await client.GetAsync(uri, budget.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content
                .ReadFromJsonAsync<StreamsResponse>(JsonOptions, budget.Token)
                .ConfigureAwait(false);
            var candidates = (payload?.Streams ?? [])
                .Select(stream => ToCandidate(stream, sourceId, uri.Host))
                .OfType<NativeReleaseCandidate>()
                .ToList();
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "addon-search-complete",
                fields: new Dictionary<string, object?>
                {
                    ["addon"] = sourceId,
                    ["streams"] = payload?.Streams?.Count ?? 0,
                    ["resultCount"] = candidates.Count,
                    ["durationMs"] = stopwatch.ElapsedMilliseconds,
                });
            return new(candidates, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            var reason = ex is OperationCanceledException ? "timeout" : "failed";
            logger.LogDebug(ex, "Stream addon {Addon} failed", sourceId);
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "addon-search-failed",
                fields: new Dictionary<string, object?>
                {
                    ["addon"] = sourceId,
                    ["failureType"] = ex.GetType().Name,
                    ["durationMs"] = stopwatch.ElapsedMilliseconds,
                });
            return new([], new NativeSourceFailure(sourceId, ex.Message, sourceId, "request", reason));
        }
    }

    /// <summary>A stable, credential-free name for an addon: its host, never its configured path.</summary>
    internal static string SourceIdFor(string addonUrl) =>
        Uri.TryCreate(addonUrl.Trim(), UriKind.Absolute, out var uri) ? $"addon:{uri.Host}" : "addon:invalid";

    internal static NativeReleaseCandidate? ToCandidate(AddonStream stream, string sourceId, string host)
    {
        var infoHash = CardigannResultNormalizer.NormalizeInfoHash(stream.InfoHash);
        if (infoHash is null)
        {
            return null;
        }

        // Torrentio puts the release name on the first line of `title`, Comet and MediaFusion in
        // `description` or behaviorHints.filename; whichever is present names the release.
        var text = string.Join('\n', new[] { stream.Title, stream.Description }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var releaseName = stream.BehaviorHints?.Filename;
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            releaseName = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => !StartsWithBadge(line));
        }
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            releaseName = stream.Name ?? infoHash;
        }

        var trackers = (stream.Sources ?? [])
            .Where(source => source.StartsWith("tracker:", StringComparison.OrdinalIgnoreCase))
            .Select(source => source["tracker:".Length..])
            .ToList();
        var magnet = new Uri(
            $"magnet:?xt=urn:btih:{infoHash}&dn={Uri.EscapeDataString(releaseName.Trim())}"
            + string.Concat(trackers.Select(tracker => "&tr=" + Uri.EscapeDataString(tracker)))
        );
        var seeders = SeedersPattern().Match(text) is { Success: true } seedMatch
            ? int.Parse(seedMatch.Groups[1].Value, CultureInfo.InvariantCulture)
            : (int?)null;
        var size = stream.BehaviorHints?.VideoSize
            ?? (SizePattern().Match(text) is { Success: true } sizeMatch
                ? CardigannResultNormalizer.ParseSize(sizeMatch.Groups[1].Value)
                : null);

        return new NativeReleaseCandidate(
            sourceId,
            releaseName.Trim(),
            magnet,
            "torrent",
            infoHash,
            size,
            seeders,
            SourceName: host,
            MagnetUrl: magnet
        );
    }

    private static bool StartsWithBadge(string line) =>
        line.Length > 0 && (char.IsSurrogate(line[0]) || line[0] is '[' or '⚙');

    [GeneratedRegex(@"👤\s*(\d+)")]
    private static partial Regex SeedersPattern();

    [GeneratedRegex(@"💾\s*([\d.,]+\s*[KMGT]i?B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    private sealed record AddonOutcome(IReadOnlyList<NativeReleaseCandidate> Candidates, NativeSourceFailure? Failure);

    internal sealed record StreamsResponse(List<AddonStream>? Streams);

    internal sealed record AddonStream(
        string? Name,
        string? Title,
        string? Description,
        string? InfoHash,
        int? FileIdx,
        string? Url,
        List<string>? Sources,
        BehaviorHints? BehaviorHints
    );

    internal sealed record BehaviorHints(
        string? Filename,
        long? VideoSize,
        [property: JsonPropertyName("bingeGroup")] string? BingeGroup
    );
}
