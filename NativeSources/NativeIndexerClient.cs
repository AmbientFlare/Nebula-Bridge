using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.NativeSources;

public sealed class NativeIndexerClient(
    IHttpClientFactory httpClientFactory,
    INetworkTargetValidator targetValidator,
    CardigannTemplateEngine templates,
    CardigannValueFilters valueFilters,
    CardigannResponseParser responseParser,
    ILogger<NativeIndexerClient> logger
) : IIndexerSearchEngine
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 3;
    private const int MaxDownloadInfoHashResolutions = 24;
    private const int MaxConcurrentDownloadResolutions = 6;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastRequests = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RequestLocks = new(
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
        logger.LogInformation("Searching indexer: {IndexerId}", definition.Id);
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
        logger.LogInformation(
            "Indexer search completed: {IndexerId} — {ResultCount} results",
            definition.Id,
            deduplicated.Count
        );
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
        Exception? lastError = null;
        foreach (var link in definition.Links)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var baseUri))
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
        var uri = initialUri;
        var requestMethod = method == "post" ? HttpMethod.Post : HttpMethod.Get;
        using var client = CreateHttpClient(definition);
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
            return new(uri, content, context);
        }

        throw new InvalidOperationException("Indexer request failed.");
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
