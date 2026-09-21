using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NebulaBridge.Config;
using NebulaBridge.Services;

namespace NebulaBridge.NativeSources;

public sealed class NativeIndexerClient(
    IHttpClientFactory httpClientFactory,
    INetworkTargetValidator targetValidator,
    CardigannTemplateEngine templates,
    CardigannValueFilters valueFilters,
    CardigannResponseParser responseParser,
    FlareSolverrClient flareSolverrClient,
    ILogger<NativeIndexerClient> logger
) : IIndexerSearchEngine
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 3;

    // A challenge page is a few KiB of markup; this only needs enough to spot its markers.
    private const int MaxChallengeProbeBytes = 64 * 1024;
    private static readonly TimeSpan ChallengeProbeTimeout = TimeSpan.FromSeconds(5);

    // Budgets follow the route, not the definition's info_flaresolverr flag: a direct fetch is
    // about a second, where the solver is a flat ~11s plus queueing against four browser slots.
    internal static readonly TimeSpan DirectSearchTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan FlareSolverrSearchTimeout = TimeSpan.FromSeconds(75);

    // Route decisions are per host, not per definition: several indexers can share a host, and
    // the discovery is about the host's Cloudflare posture rather than the definition. This client
    // is registered as a singleton, so an instance field is process-wide in practice.
    private readonly IndexerRouteCache _routes = new();
    private const int MaxDownloadInfoHashResolutions = 24;
    private const int MaxConcurrentDownloadResolutions = 6;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastRequests = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RequestLocks = new(
        StringComparer.OrdinalIgnoreCase
    );
    // An indexer that answered 429 is left alone for a while: retrying it, or walking its mirror
    // list, only extends the ban and delays the sweep for everybody else.
    internal static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RateLimitedUntil = new(
        StringComparer.OrdinalIgnoreCase
    );

    public async Task<IReadOnlyList<NativeReleaseCandidate>> SearchAsync(
        IndexerDefinition definition,
        NativeMediaQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(query);
        if (definition.SchemaVersion != IndexerDefinition.SupportedSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported Cardigann schema version {definition.SchemaVersion}."
            );
        }

        var search = definition.Document["search"]?.AsObject()
            ?? throw new InvalidDataException("Cardigann search block is missing.");
        var context = BuildContext(definition, query, search);
        var paths = GetSearchPaths(search);
        var results = new List<NativeReleaseCandidate>();
        var stopwatch = Stopwatch.StartNew();
        logger.LogDebug("Searching indexer: {IndexerId}", definition.Id);
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "indexer-search-start",
            fields: new Dictionary<string, object?> { ["indexer"] = definition.Id });
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathTemplate = CardigannDefinitionParser.Text(path, "path") ?? string.Empty;
            var relativePath = templates.Render(pathTemplate, context);
            var method = templates
                .Render(CardigannDefinitionParser.Text(path, "method") ?? "get", context)
                .Trim()
                .ToLowerInvariant();
            if (method is not ("get" or "post"))
            {
                throw new InvalidOperationException(
                    $"Unsupported Cardigann HTTP method '{method}'."
                );
            }

            var inputs = BuildInputs(search, path, context);
            var responseType = CardigannDefinitionParser.Text(
                path["response"] as JsonObject,
                "type"
            ) ?? "html";
            var response = await SendWithLinkFallbackAsync(
                definition,
                relativePath,
                method,
                inputs,
                search["headers"]?.AsObject(),
                context,
                cancellationToken
            ).ConfigureAwait(false);
            results.AddRange(
                responseParser.Parse(
                    definition,
                    responseType,
                    response.Content,
                    response.ResponseUri,
                    response.Context
                )
            );
        }

        results = (await ResolveDownloadInfoHashesAsync(
                definition,
                results,
                context,
                cancellationToken
            ).ConfigureAwait(false))
            .ToList();

        var deduplicated = results
            .GroupBy(
                result => result.InfoHash is null
                    ? $"url:{result.Link.AbsoluteUri}"
                    : $"hash:{result.InfoHash}",
                StringComparer.OrdinalIgnoreCase
            )
            .Select(group => group.First())
            .Take(200)
            .ToList();
        logger.LogDebug(
            "Indexer search completed: {IndexerId} — {ResultCount} results",
            definition.Id,
            deduplicated.Count
        );
        NebulaBridgeFileLog.Write(
            NebulaLogVerbosity.Information,
            "indexer-search-complete",
            fields: new Dictionary<string, object?>
            {
                ["indexer"] = definition.Id,
                ["resultCount"] = deduplicated.Count,
                ["durationMs"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds),
            });
        return deduplicated;
    }

    private async Task<IReadOnlyList<NativeReleaseCandidate>> ResolveDownloadInfoHashesAsync(
        IndexerDefinition definition,
        IReadOnlyList<NativeReleaseCandidate> candidates,
        CardigannTemplateContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            definition.Document["download"] is not JsonObject download
            || download["infohash"] is not JsonObject infoHashDefinition
        )
        {
            return candidates;
        }

        var declaredHosts = definition.Links
            .Select(link => Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.IdnHost : null)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(MaxConcurrentDownloadResolutions);
        var resolutionIndexes = candidates
            .Select((candidate, index) => (candidate, index))
            .Where(item =>
                item.candidate.InfoHash is null
                && item.candidate.MagnetUrl is null
                && (item.candidate.DownloadUrl ?? item.candidate.DetailsUrl) is not null
            )
            .Take(MaxDownloadInfoHashResolutions)
            .ToList();
        if (resolutionIndexes.Count == 0)
        {
            return candidates;
        }

        var resolved = candidates.ToArray();
        await Task.WhenAll(resolutionIndexes.Select(async item =>
        {
            var sourceUri = item.candidate.DownloadUrl ?? item.candidate.DetailsUrl!;
            if (!declaredHosts.Contains(sourceUri.IdnHost))
            {
                logger.LogWarning(
                    "Skipped download flow outside declared hosts: {IndexerId} — {Host}",
                    definition.Id,
                    sourceUri.IdnHost
                );
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var resultContext = context with
                {
                    Result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["title"] = item.candidate.Title,
                    },
                };
                var baseUri = new Uri(sourceUri.GetLeftPart(UriPartial.Authority) + "/");
                var response = await SendAsync(
                    definition,
                    baseUri,
                    sourceUri.PathAndQuery,
                    "get",
                    new Dictionary<string, string>(),
                    download["headers"]?.AsObject()
                        ?? definition.Document["search"]?["headers"]?.AsObject(),
                    ContextForLink(resultContext, baseUri),
                    cancellationToken
                ).ConfigureAwait(false);
                var resolution = responseParser.ParseDownloadInfoHash(
                    infoHashDefinition,
                    response.Content,
                    response.Context
                );
                if (resolution is null)
                {
                    logger.LogWarning(
                        "Indexer download info-hash selectors did not match: {IndexerId} — {Uri}",
                        definition.Id,
                        sourceUri
                    );
                    return;
                }

                var title = string.IsNullOrWhiteSpace(resolution.Title)
                    ? item.candidate.Title
                    : System.Net.WebUtility.HtmlDecode(resolution.Title.Trim());
                var magnet = new Uri(
                    $"magnet:?xt=urn:btih:{resolution.InfoHash}&dn={Uri.EscapeDataString(title)}"
                );
                resolved[item.index] = item.candidate with
                {
                    Title = title,
                    Link = magnet,
                    Kind = "torrent",
                    InfoHash = resolution.InfoHash,
                    MagnetUrl = magnet,
                    DownloadUrl = null,
                };
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException
                    or InvalidDataException
                    or InvalidOperationException
                    or TaskCanceledException
            )
            {
                logger.LogWarning(
                    ex,
                    "Indexer download info-hash resolution failed: {IndexerId} — {Uri}",
                    definition.Id,
                    sourceUri
                );
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return resolved;
    }

    private async Task<CardigannHttpResponse> SendWithLinkFallbackAsync(
        IndexerDefinition definition,
        string relativePath,
        string method,
        IReadOnlyDictionary<string, string> inputs,
        JsonObject? headers,
        CardigannTemplateContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            RateLimitedUntil.TryGetValue(definition.Id, out var cooldownEnd)
            && cooldownEnd > DateTimeOffset.UtcNow
        )
        {
            throw new HttpRequestException(
                $"Indexer '{definition.Id}' is rate limited until {cooldownEnd:HH:mm:ss}.",
                null,
                HttpStatusCode.TooManyRequests
            );
        }

        Exception? lastError = null;
        var attempted = new HashSet<Uri>();
        foreach (var link in definition.Links)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var baseUri))
            {
                continue;
            }

            // A search path that is itself absolute (an API host shared by every mirror) resolves
            // to the same request under each link; it gets one attempt, not one per mirror.
            if (!attempted.Add(new Uri(baseUri, relativePath)))
            {
                continue;
            }

            try
            {
                return await SendAsync(
                    definition,
                    baseUri,
                    relativePath,
                    method,
                    inputs,
                    headers,
                    ContextForLink(context, baseUri),
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (
                !cancellationToken.IsCancellationRequested
                && ex.StatusCode == HttpStatusCode.TooManyRequests
            )
            {
                // The site is up and has told us to slow down; another mirror is the opposite.
                var until = DateTimeOffset.UtcNow + RateLimitCooldown;
                RateLimitedUntil[definition.Id] = until;
                NebulaBridgeFileLog.Write(
                    NebulaLogVerbosity.Information,
                    "indexer-rate-limited",
                    fields: new Dictionary<string, object?>
                    {
                        ["indexer"] = definition.Id,
                        ["host"] = baseUri.IdnHost,
                        ["cooldownSeconds"] = (int)RateLimitCooldown.TotalSeconds,
                    });
                throw;
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException or InvalidOperationException
            )
            {
                lastError = ex;
            }
        }

        throw new HttpRequestException(
            $"All declared links failed for indexer '{definition.Id}'.",
            lastError
        );
    }

    private async Task<CardigannHttpResponse> SendAsync(
        IndexerDefinition definition,
        Uri baseUri,
        string relativePath,
        string method,
        IReadOnlyDictionary<string, string> inputs,
        JsonObject? headers,
        CardigannTemplateContext context,
        CancellationToken cancellationToken
    )
    {
        var raw = inputs.GetValueOrDefault("$raw");
        var ordinaryInputs = inputs
            .Where(pair => pair.Key != "$raw")
            .ToList();
        var initialUri = new Uri(baseUri, relativePath);
        if (method == "get")
        {
            initialUri = AppendQuery(initialUri, ordinaryInputs, raw);
        }

        await WaitForRateLimitAsync(definition, cancellationToken).ConfigureAwait(false);
        var allowedHosts = GetDeclaredRequestHosts(definition, context);
        if (!allowedHosts.Contains(initialUri.IdnHost))
        {
            throw new InvalidOperationException(
                "Indexer search path resolved outside its declared hosts."
            );
        }
        var body = BuildFormBody(ordinaryInputs, raw);
        var requestMethod = method == "post" ? HttpMethod.Post : HttpMethod.Get;
        var host = initialUri.IdnHost;

        // Direct-first. The definition's info_flaresolverr flag is only an authoring hint, so the
        // route is discovered by trying rather than declared: an ordinary fetch is about a second
        // where the solver is a flat ~11s against four shared browser slots. Only a recognised
        // Cloudflare challenge falls through; a real 403 from the origin stays a 403.
        if (_routes.GetRoute(host) != IndexerRoute.FlareSolverr)
        {
            var attempt = await TrySendDirectAsync(
                definition,
                initialUri,
                requestMethod,
                body,
                headers,
                context,
                allowedHosts,
                cancellationToken
            ).ConfigureAwait(false);
            if (attempt.Uri is { } directUri && attempt.Content is { } directContent)
            {
                _routes.RecordDirect(host);
                return new(directUri, directContent, context);
            }

            _routes.RecordChallenge(host);
            logger.LogDebug(
                "Cloudflare challenge from {Host}; retrying indexer {IndexerId} via FlareSolverr.",
                host,
                definition.Id
            );
            NebulaBridgeFileLog.Write(
                NebulaLogVerbosity.Information,
                "indexer-route-challenge",
                fields: new Dictionary<string, object?>
                {
                    ["indexer"] = definition.Id,
                    ["host"] = host,
                });
        }

        return await SendThroughFlareSolverrAsync(
            initialUri,
            requestMethod,
            body,
            headers,
            context,
            allowedHosts,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<CardigannHttpResponse> SendThroughFlareSolverrAsync(
        Uri initialUri,
        HttpMethod requestMethod,
        string body,
        JsonObject? headers,
        CardigannTemplateContext context,
        HashSet<string> allowedHosts,
        CancellationToken cancellationToken
    )
    {
        await targetValidator.ValidateAsync(initialUri, cancellationToken).ConfigureAwait(false);
        var solved = await flareSolverrClient
            .SendAsync(
                initialUri,
                requestMethod,
                body,
                GetHeaderValues(headers, context, "cookie"),
                cancellationToken,
                GetFlareSolverrHeaders(headers, context)
            )
            .ConfigureAwait(false);
        if (!allowedHosts.Contains(solved.ResponseUri.IdnHost))
        {
            throw new InvalidOperationException(
                "Indexer redirected outside its declared hosts."
            );
        }

        await targetValidator
            .ValidateAsync(solved.ResponseUri, cancellationToken)
            .ConfigureAwait(false);
        return new(solved.ResponseUri, solved.Content, context);
    }

    /// <summary>
    /// Result of an ordinary fetch. Both members are null when the host served a Cloudflare
    /// challenge and the request should be retried through the solver.
    /// </summary>
    private readonly record struct DirectAttempt(Uri? Uri, string? Content)
    {
        internal static DirectAttempt Challenged => new(null, null);
    }

    /// <summary>
    /// The budget for <paramref name="definition"/>, taken from the route its hosts are known to
    /// use. A host that has not been probed yet gets the solver budget, because the probe may well
    /// end in a challenge; the probe itself is capped at <see cref="DirectSearchTimeout"/>, so the
    /// probe-then-solve path still fits inside that budget.
    /// </summary>
    public TimeSpan GetSearchTimeout(IndexerDefinition definition)
    {
        var sawDirect = false;
        foreach (var link in definition.Links)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            {
                continue;
            }

            switch (_routes.GetRoute(uri.IdnHost))
            {
                case IndexerRoute.FlareSolverr:
                    return FlareSolverrSearchTimeout;
                case IndexerRoute.Direct:
                    sawDirect = true;
                    break;
                default:
                    return FlareSolverrSearchTimeout;
            }
        }

        return sawDirect ? DirectSearchTimeout : FlareSolverrSearchTimeout;
    }

    private async Task<DirectAttempt> TrySendDirectAsync(
        IndexerDefinition definition,
        Uri initialUri,
        HttpMethod method,
        string body,
        JsonObject? headers,
        CardigannTemplateContext context,
        HashSet<string> allowedHosts,
        CancellationToken cancellationToken
    )
    {
        var uri = initialUri;
        var requestMethod = method;
        using var client = CreateHttpClient(definition);

        // Cap the probe so that trying direct first and then falling back to the solver still
        // fits within the solver budget the coordinator granted an unprobed host.
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(DirectSearchTimeout);
        cancellationToken = probeTimeout.Token;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            if (!allowedHosts.Contains(uri.IdnHost))
            {
                throw new InvalidOperationException(
                    "Indexer redirected outside its declared hosts."
                );
            }

            await targetValidator.ValidateAsync(uri, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(requestMethod, uri);
            request.Headers.UserAgent.ParseAdd("NebulaBridge-Cardigann/1.0");
            AddHeaders(request, headers, context);
            if (requestMethod == HttpMethod.Post)
            {
                request.Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                );
            }

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaxRedirects || response.Headers.Location is null)
                {
                    throw new InvalidOperationException("Indexer exceeded the redirect limit.");
                }

                uri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(uri, response.Headers.Location);
                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.SeeOther)
                {
                    requestMethod = HttpMethod.Get;
                }

                continue;
            }

            // A mitigating header is conclusive on its own, so answer before touching the body.
            if (CloudflareChallengeDetector.HasChallengeHeaders(response))
            {
                return DirectAttempt.Challenged;
            }

            if (CloudflareChallengeDetector.IsChallengeStatus(response.StatusCode))
            {
                // Read the body only for statuses Cloudflare actually challenges with, so an
                // ordinary error response is not paid for twice.
                var errorContent = await ReadChallengeProbeAsync(response, cancellationToken)
                    .ConfigureAwait(false);
                if (CloudflareChallengeDetector.IsChallenge(response, errorContent))
                {
                    return DirectAttempt.Challenged;
                }
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                throw new InvalidOperationException("Indexer response exceeds the 2 MiB limit.");
            }

            var content = await ReadLimitedAsync(
                response.Content,
                definition.Encoding,
                cancellationToken
            ).ConfigureAwait(false);

            // Cloudflare can also serve an interstitial with a 200, so a successful-looking body
            // still has to be checked before it is handed to the parser as results.
            if (CloudflareChallengeDetector.IsChallenge(response, content))
            {
                return DirectAttempt.Challenged;
            }

            return new(uri, content);
        }

        throw new InvalidOperationException("Indexer request failed.");
    }

    /// <summary>
    /// Reads a possible challenge page, tolerating failure: this runs on an error response whose
    /// body is advisory, so a read problem must not mask the status the caller is about to throw.
    /// </summary>
    private static async Task<string?> ReadChallengeProbeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (response.Content.Headers.ContentLength is > MaxChallengeProbeBytes)
        {
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ChallengeProbeTimeout);
            var buffer = new byte[MaxChallengeProbeBytes];
            await using var stream = await response
                .Content.ReadAsStreamAsync(cts.Token)
                .ConfigureAwait(false);
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(total), cts.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static string BuildFormBody(
        IReadOnlyList<KeyValuePair<string, string>> ordinaryInputs,
        string? raw
    )
    {
        var body = string.Join(
            '&',
            ordinaryInputs.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"
            )
        );
        if (!string.IsNullOrEmpty(raw))
        {
            body = string.IsNullOrEmpty(body) ? raw : raw + "&" + body;
        }

        return body;
    }

    private static HashSet<string> GetDeclaredRequestHosts(
        IndexerDefinition definition,
        CardigannTemplateContext context
    )
    {
        var hosts = definition
            .Links.Select(value => new Uri(value, UriKind.Absolute).IdnHost)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Cardigann definitions such as YTS and TPB explicitly declare a separate API
        // host through settings named apiurl. Treat URL-valued settings as declared
        // request hosts, but never trust an arbitrary absolute search path by itself.
        foreach (var (name, value) in context.Config)
        {
            if (
                !name.EndsWith("url", StringComparison.OrdinalIgnoreCase)
                || value is not string text
                || string.IsNullOrWhiteSpace(text)
            )
            {
                continue;
            }

            var candidate = text.Contains("://", StringComparison.Ordinal)
                ? text
                : "https://" + text.TrimStart('/');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                hosts.Add(uri.IdnHost);
            }
        }

        return hosts;
    }

    private HttpClient CreateHttpClient(IndexerDefinition definition)
    {
        if (definition.CertificateFingerprints.Count == 0)
        {
            return httpClientFactory.CreateClient(nameof(NativeIndexerClient));
        }

        var pins = definition.CertificateFingerprints.ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, policyErrors) =>
                CertificateIsAccepted(certificate, policyErrors, pins),
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    internal static bool CertificateIsAccepted(
        X509Certificate2? certificate,
        SslPolicyErrors policyErrors,
        IReadOnlySet<string> fingerprints
    )
    {
        if (fingerprints.Count == 0)
        {
            return policyErrors == SslPolicyErrors.None;
        }

        if (certificate is null)
        {
            return false;
        }

        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA1);
        return fingerprints.Contains(fingerprint);
    }

    private IReadOnlyDictionary<string, string> BuildInputs(
        JsonObject search,
        JsonObject path,
        CardigannTemplateContext context
    )
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        AddInputs(inputs, search["inputs"]?.AsObject(), context);
        AddInputs(inputs, path["inputs"]?.AsObject(), context);
        return inputs;
    }

    private void AddInputs(
        Dictionary<string, string> output,
        JsonObject? source,
        CardigannTemplateContext context
    )
    {
        foreach (var (key, value) in source ?? [])
        {
            output[key] = templates.Render(CardigannValueFilters.Scalar(value), context);
        }
    }

    private static IReadOnlyList<JsonObject> GetSearchPaths(JsonObject search)
    {
        if (search["paths"] is JsonArray paths)
        {
            return paths.OfType<JsonObject>().ToList();
        }

        return search["path"] is null
            ? []
            : [new JsonObject { ["path"] = search["path"]!.DeepClone() }];
    }

    private CardigannTemplateContext BuildContext(
        IndexerDefinition definition,
        NativeMediaQuery query,
        JsonObject search
    )
    {
        var keywords = BuildKeywords(query);
        var queryValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Q"] = query.Title,
            ["Keywords"] = keywords,
            ["Year"] = query.Year,
            ["Season"] = query.Season,
            ["Ep"] = query.Episode,
            ["Episode"] = query.Episode,
            ["IMDBID"] = query.ImdbId,
            ["TMDBID"] = query.TmdbId,
            ["TVDBID"] = query.TvdbId,
        };
        var config = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in definition.Document["settings"]?.AsArray() ?? [])
        {
            if (setting is not JsonObject settingObject)
            {
                continue;
            }

            var name = CardigannDefinitionParser.Text(settingObject, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (settingObject["default"] is JsonNode defaultValue)
            {
                config[name] = ToObject(defaultValue);
            }
            else
            {
                var settingType = CardigannDefinitionParser.Text(settingObject, "type");
                if (settingType == "text")
                {
                    config[name] = string.Empty;
                }
                else if (settingType == "checkbox")
                {
                    config[name] = false;
                }
            }
        }

        config["sitelink"] = definition.Links.FirstOrDefault() ?? string.Empty;

        var initial = new CardigannTemplateContext(
            keywords,
            queryValues,
            config,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            []
        );
        keywords = valueFilters.Apply(
            keywords,
            search["keywordsfilters"] as JsonArray,
            initial
        );
        queryValues["Keywords"] = keywords;

        return initial with { Keywords = keywords, Query = queryValues };
    }

    private static CardigannTemplateContext ContextForLink(
        CardigannTemplateContext context,
        Uri baseUri
    )
    {
        var config = new Dictionary<string, object?>(
            context.Config,
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["sitelink"] = baseUri.AbsoluteUri,
        };
        return context with { Config = config };
    }

    internal static string BuildKeywords(NativeMediaQuery query)
    {
        if (query.Season.HasValue && query.Episode.HasValue)
        {
            return $"{query.Title} S{query.Season:00}E{query.Episode:00}";
        }

        if (query.Season.HasValue)
        {
            return $"{query.Title} S{query.Season:00}";
        }

        return query.Year.HasValue ? $"{query.Title} {query.Year}" : query.Title;
    }

    private static object? ToObject(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }

            if (value.TryGetValue<long>(out var integer))
            {
                return integer;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number;
            }
        }

        return node.ToJsonString();
    }

    private static Uri AppendQuery(
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> inputs,
        string? raw
    )
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(uri.Query))
        {
            parts.Add(uri.Query.TrimStart('?'));
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            parts.Add(raw.Trim('&', '?'));
        }

        parts.AddRange(
            inputs.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"
            )
        );
        return new UriBuilder(uri) { Query = string.Join('&', parts) }.Uri;
    }

    private void AddHeaders(
        HttpRequestMessage request,
        JsonObject? headers,
        CardigannTemplateContext context
    )
    {
        foreach (var (name, valuesNode) in headers ?? [])
        {
            var values = valuesNode is JsonArray array
                ? array.Select(value =>
                    templates.Render(CardigannValueFilters.Scalar(value), context)
                )
                : [templates.Render(CardigannValueFilters.Scalar(valuesNode), context)];
            request.Headers.TryAddWithoutValidation(name, values);
        }
    }

    private string? GetHeaderValues(
        JsonObject? headers,
        CardigannTemplateContext context,
        string requestedName
    )
    {
        var header = (headers ?? []).FirstOrDefault(pair =>
            pair.Key.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
        );
        if (header.Key is null)
        {
            return null;
        }

        var values = header.Value is JsonArray array
            ? array.Select(value =>
                templates.Render(CardigannValueFilters.Scalar(value), context)
            )
            : [templates.Render(CardigannValueFilters.Scalar(header.Value), context)];
        return string.Join("; ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private IReadOnlyDictionary<string, string> GetFlareSolverrHeaders(
        JsonObject? headers,
        CardigannTemplateContext context
    )
    {
        var forwarded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, valuesNode) in headers ?? [])
        {
            // Cookies are represented by FlareSolverr's structured cookies field.
            // Hop-by-hop and transport-generated headers must not be sent to the
            // browser session because FlareSolverr will construct those itself.
            if (
                name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                || name.Equals("host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var values = valuesNode is JsonArray array
                ? array.Select(value =>
                    templates.Render(CardigannValueFilters.Scalar(value), context)
                )
                : [templates.Render(CardigannValueFilters.Scalar(valuesNode), context)];
            var value = string.Join(", ", values.Where(item => !string.IsNullOrWhiteSpace(item)));
            if (!string.IsNullOrWhiteSpace(value))
            {
                forwarded[name] = value;
            }
        }

        return forwarded;
    }

    private static async Task<string> ReadLimitedAsync(
        HttpContent content,
        string definitionEncoding,
        CancellationToken cancellationToken
    )
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaxResponseBytes)
            {
                throw new InvalidOperationException("Indexer response exceeds the 2 MiB limit.");
            }

            output.Write(buffer, 0, read);
        }

        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(
                string.IsNullOrWhiteSpace(charset) ? definitionEncoding : charset
            );
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(output.ToArray());
    }

    private static async Task WaitForRateLimitAsync(
        IndexerDefinition definition,
        CancellationToken cancellationToken
    )
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(definition.RequestDelaySeconds, 0, 60));
        if (delay == TimeSpan.Zero)
        {
            return;
        }

        var gate = RequestLocks.GetOrAdd(definition.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = LastRequests.TryGetValue(definition.Id, out var previous)
                ? previous + delay - DateTimeOffset.UtcNow
                : TimeSpan.Zero;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            LastRequests[definition.Id] = DateTimeOffset.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private sealed record CardigannHttpResponse(
        Uri ResponseUri,
        string Content,
        CardigannTemplateContext Context
    );
}
