using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Serialization;

namespace NebulaBridge.NativeSources;

public sealed class CardigannDefinitionParser
{
    private const int MaximumDefinitionCharacters = 512 * 1024;
    private static readonly HashSet<string> SupportedFilters = new(
        [
            "andmatch",
            "append",
            "dateparse",
            "fuzzytime",
            "prepend",
            "querystring",
            "re_replace",
            "regexp",
            "replace",
            "split",
            "timeago",
            "tolower",
            "trim",
            "urldecode",
            "validfilename",
        ],
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> SupportedTemplateFunctions = new(
        ["and", "eq", "join", "ne", "not", "or", "re_replace"],
        StringComparer.Ordinal
    );
    private readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();
    private readonly ISerializer _jsonSerializer = new SerializerBuilder().JsonCompatible().Build();
    private readonly JsonSchema _schema;
    public CardigannDefinitionParser()
    {
        var assembly = typeof(CardigannDefinitionParser).Assembly;
        var resource = assembly
            .GetManifestResourceNames()
            .Single(name => name.EndsWith("indexers.schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The embedded Cardigann v11 schema is missing.");
        using var reader = new StreamReader(stream);
        _schema = JsonSchema.FromText(reader.ReadToEnd());
    }

    internal IndexerDefinitionRecord Parse(
        string yaml,
        string sourcePath,
        int definitionVersion = IndexerDefinition.SupportedSchemaVersion
    )
    {
        var fallbackId = Path.GetFileNameWithoutExtension(sourcePath).ToLowerInvariant();
        yaml = yaml.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Invalid(fallbackId, sourcePath, "Definition YAML is empty.");
        }

        if (yaml.Length > MaximumDefinitionCharacters)
        {
            return Invalid(fallbackId, sourcePath, "Definition exceeds 512 KiB.");
        }

        JsonObject document;
        try
        {
            var yamlObject = _yamlDeserializer.Deserialize<object>(yaml);
            var json = _jsonSerializer.Serialize(yamlObject);
            document = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("The YAML root must be an object.");
        }
        catch (Exception ex) when (
            ex is YamlDotNet.Core.YamlException
                or JsonException
                or InvalidDataException
                or InvalidOperationException
        )
        {
            return Invalid(fallbackId, sourcePath, $"Malformed YAML: {ex.Message}");
        }

        var id = Text(document, "id") ?? fallbackId;
        var name = Text(document, "name") ?? id;
        var description = Text(document, "description") ?? string.Empty;
        if (definitionVersion != IndexerDefinition.SupportedSchemaVersion)
        {
            return new(
                id,
                name,
                description,
                sourcePath,
                null,
                true,
                false,
                $"Unsupported Cardigann schema version {definitionVersion}; this client supports v{IndexerDefinition.SupportedSchemaVersion}.",
                definitionVersion
            );
        }

        var schemaErrors = ValidateSchema(document);
        if (schemaErrors.Count > 0)
        {
            return new(
                id,
                name,
                description,
                sourcePath,
                null,
                false,
                false,
                "Cardigann v11 schema validation failed: " + string.Join("; ", schemaErrors.Take(4))
            );
        }

        var links = document["links"]
            ?.AsArray()
            .Select(node => node?.GetValue<string>() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList() ?? [];
        links.AddRange(
            document["legacylinks"]
                ?.AsArray()
                .Select(node => node?.GetValue<string>() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value)) ?? []
        );
        links = links.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var certificateFingerprints = document["certificates"]
            ?.AsArray()
            .Select(node => NormalizeCertificateFingerprint(node?.GetValue<string>()))
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var definition = new IndexerDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            Language = Text(document, "language") ?? string.Empty,
            Type = Text(document, "type") ?? string.Empty,
            Encoding = Text(document, "encoding") ?? "UTF-8",
            RequestDelaySeconds = Number(document, "requestDelay") ?? 0,
            CertificateFingerprints = certificateFingerprints,
            Links = links,
            Document = document,
            SourcePath = sourcePath,
            SchemaVersion = definitionVersion,
            DefinitionHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(yaml))
            ),
        };
        var compatibilityErrors = AnalyzeCompatibility(definition);
        return new(
            id,
            name,
            description,
            sourcePath,
            definition,
            true,
            compatibilityErrors.Count == 0,
            compatibilityErrors.Count == 0 ? null : string.Join("; ", compatibilityErrors),
            definitionVersion
        );
    }

    private List<string> ValidateSchema(JsonObject document)
    {
        using var json = JsonDocument.Parse(document.ToJsonString());
        var results = _schema.Evaluate(
            json.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = true,
            }
        );
        if (results.IsValid)
        {
            return [];
        }

        var errors = new List<string>();
        CollectErrors(results, errors);
        return errors.Distinct(StringComparer.Ordinal).Take(20).ToList();
    }

    private static void CollectErrors(EvaluationResults result, List<string> output)
    {
        if (result.Errors is not null)
        {
            foreach (var (keyword, message) in result.Errors)
            {
                output.Add($"{result.InstanceLocation}: {keyword}: {message}");
            }
        }

        foreach (var detail in result.Details ?? [])
        {
            CollectErrors(detail, output);
        }
    }

    private static List<string> AnalyzeCompatibility(IndexerDefinition definition)
    {
        var errors = new List<string>();
        var document = definition.Document;
        if (!definition.Type.Equals("public", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Requires authentication — only public indexers are supported in Phase 1");
        }

        if (document["login"] is not null)
        {
            errors.Add("Cardigann login flows are not supported");
        }

        if (document["followredirect"] is not null)
        {
            errors.Add("Definition-specific redirect behavior is not supported");
        }

        foreach (var certificate in document["certificates"]?.AsArray() ?? [])
        {
            if (NormalizeCertificateFingerprint(certificate?.GetValue<string>()) is null)
            {
                errors.Add("Certificate fingerprints must be 40-character SHA-1 hex values");
            }
        }

        foreach (var setting in document["settings"]?.AsArray() ?? [])
        {
            if (setting is not JsonObject settingObject)
            {
                continue;
            }

            var settingType = Text(settingObject, "type") ?? string.Empty;
            var settingName = Text(settingObject, "name") ?? "unnamed";
            if (settingType is "password" or "info_cookie")
            {
                errors.Add($"Requires authentication setting '{settingName}'");
            }
            else if (settingType == "info_flaresolverr")
            {
                errors.Add("Requires FlareSolverr, which is not supported in Phase 1");
            }
            else if (settingType == "select" && settingObject["default"] is null)
            {
                errors.Add($"Requires configuration setting '{settingName}'");
            }
        }

        var search = document["search"]?.AsObject();
        if (search is null)
        {
            errors.Add("Search configuration is missing");
            return errors;
        }

        if (search["preprocessingfilters"] is not null)
        {
            errors.Add("Search preprocessing filters are not supported");
        }

        if (search["error"] is not null)
        {
            errors.Add("Definition-specific search error matching is not supported");
        }

        if (document["download"] is JsonObject download)
        {
            if (download["before"] is not null)
            {
                errors.Add("Download pre-request flows are not supported");
            }

            if (download["selectors"] is not null)
            {
                errors.Add("Download link selector flows are not supported");
            }

            if (download["infohash"] is not JsonObject infoHash)
            {
                errors.Add("Only info-hash download flows are currently supported");
            }
            else if (infoHash["usebeforeresponse"]?.GetValue<bool>() == true)
            {
                errors.Add("Info-hash download flows using a pre-request response are not supported");
            }

            var downloadMethod = Text(download, "method");
            if (downloadMethod is not null && !downloadMethod.Equals("get", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Unsupported download HTTP method: {downloadMethod}");
            }
        }

        foreach (var path in search["paths"]?.AsArray() ?? [])
        {
            if (path is not JsonObject pathObject)
            {
                continue;
            }

            var responseType = Text(pathObject["response"] as JsonObject, "type") ?? "html";
            if (responseType is not ("html" or "json" or "xml"))
            {
                errors.Add($"Unsupported response type: {responseType}");
            }

            var method = Text(pathObject, "method");
            if (
                method is not null
                && !method.Contains("{{", StringComparison.Ordinal)
                && method is not ("get" or "post")
            )
            {
                errors.Add($"Unsupported HTTP method: {method}");
            }

            if (pathObject["queryseparator"] is not null)
            {
                errors.Add("Custom query separators are not supported");
            }

            if (pathObject["followredirect"] is not null)
            {
                errors.Add("Path-specific redirect behavior is not supported");
            }

            if (pathObject["categories"] is not null)
            {
                errors.Add("Category-specific search paths are not supported");
            }

            if (pathObject["inheritinputs"]?.GetValue<bool>() == false)
            {
                errors.Add("Disabling inherited search inputs is not supported");
            }
        }

        Visit(document, node =>
        {
            if (node is not JsonObject obj || obj["filters"] is not JsonArray filters)
            {
                return;
            }

            foreach (var filter in filters.OfType<JsonObject>())
            {
                var name = Text(filter, "name");
                if (!string.IsNullOrWhiteSpace(name) && !SupportedFilters.Contains(name))
                {
                    errors.Add($"Unsupported Cardigann filter: {name}");
                }
            }
        });
        Visit(document, node =>
        {
            if (node is not JsonObject obj)
            {
                return;
            }

        });
        Visit(document, node =>
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                return;
            }

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(
                    text,
                    "\\{\\{\\s*([^{}]+?)\\s*\\}\\}",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)
                ))
            {
                var expression = match.Groups[1].Value.Trim();
                if (expression.StartsWith("if ", StringComparison.Ordinal))
                {
                    expression = expression[3..].TrimStart();
                }
                else if (
                    expression.StartsWith("range ", StringComparison.Ordinal)
                    || expression is "else" or "end"
                )
                {
                    continue;
                }

                var first = expression.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                if (
                    expression.Contains(" ", StringComparison.Ordinal)
                    && !first.StartsWith(".", StringComparison.Ordinal)
                    && !first.StartsWith('"')
                    && !SupportedTemplateFunctions.Contains(first)
                )
                {
                    errors.Add($"Unsupported Cardigann template function: {first}");
                }
            }
        });
        return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Visit(JsonNode? node, Action<JsonNode> visitor)
    {
        if (node is null)
        {
            return;
        }

        visitor(node);
        if (node is JsonObject obj)
        {
            foreach (var child in obj.Select(pair => pair.Value))
            {
                Visit(child, visitor);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                Visit(child, visitor);
            }
        }
    }

    internal static string? Text(JsonObject? obj, string property) =>
        obj?[property] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : obj?[property]?.ToJsonString().Trim('"');

    internal static double? Number(JsonObject obj, string property) =>
        obj[property] is JsonValue value && value.TryGetValue<double>(out var number)
            ? number
            : null;

    private static string? NormalizeCertificateFingerprint(string? value)
    {
        var normalized = (value ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim();
        return normalized.Length == 40
            && normalized.All(character => Uri.IsHexDigit(character))
                ? normalized.ToLowerInvariant()
                : null;
    }

    private static IndexerDefinitionRecord Invalid(
        string id,
        string sourcePath,
        string error
    )
    {
        return new(
            id,
            id,
            string.Empty,
            sourcePath,
            null,
            false,
            false,
            error
        );
    }
}
