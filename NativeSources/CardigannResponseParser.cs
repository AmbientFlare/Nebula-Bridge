using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.XPath;

namespace NebulaBridge.NativeSources;

public sealed class CardigannResponseParser(
    CardigannTemplateEngine templates,
    CardigannValueFilters filters
)
{
    public IReadOnlyList<NativeReleaseCandidate> Parse(
        IndexerDefinition definition,
        string responseType,
        string content,
        Uri responseUri,
        CardigannTemplateContext searchContext
    )
    {
        var search = definition.Document["search"]?.AsObject()
            ?? throw new InvalidDataException("Cardigann search block is missing.");
        var rowsDefinition = search["rows"]?.AsObject()
            ?? throw new InvalidDataException("Cardigann rows block is missing.");
        var fieldsDefinition = search["fields"]?.AsObject()
            ?? throw new InvalidDataException("Cardigann fields block is missing.");
        var rowSelector = templates.Render(
            CardigannDefinitionParser.Text(rowsDefinition, "selector") ?? string.Empty,
            searchContext
        );
        var rows = CreateRows(responseType, content, rowSelector, rowsDefinition);
        var output = new List<NativeReleaseCandidate>();
        foreach (var row in rows.Take(200))
        {
            if (!MatchesRowFilters(row, rowsDefinition["filters"]?.AsArray(), searchContext))
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missingRequiredField = false;
            foreach (var (fieldName, node) in fieldsDefinition)
            {
                if (node is not JsonObject field)
                {
                    continue;
                }

                var fieldContext = searchContext with { Result = values };
                var extraction = ExtractField(row, field, fieldContext);
                if (!extraction.Found && field["optional"]?.GetValue<bool>() != true)
                {
                    missingRequiredField = true;
                    break;
                }

                var normalizedName = fieldName.Split('|', 2)[0];
                if (fieldName.EndsWith("|append", StringComparison.OrdinalIgnoreCase))
                {
                    values[normalizedName] = values.GetValueOrDefault(normalizedName) + extraction.Value;
                }
                else
                {
                    values[normalizedName] = extraction.Value;
                }
            }

            if (missingRequiredField)
            {
                continue;
            }

            var candidate = CardigannResultNormalizer.Normalize(definition, values, responseUri);
            if (candidate is not null)
            {
                output.Add(candidate);
            }
        }

        return output;
    }

    internal DownloadInfoHashResolution? ParseDownloadInfoHash(
        JsonObject infoHashDefinition,
        string content,
        CardigannTemplateContext context
    )
    {
        var row = HtmlResponseRow.Select(content, "html").FirstOrDefault();
        if (row is null || infoHashDefinition["hash"] is not JsonObject hashField)
        {
            return null;
        }

        var hash = ExtractField(row, hashField, context);
        if (!hash.Found)
        {
            return null;
        }

        var normalizedHash = CardigannResultNormalizer.NormalizeInfoHash(hash.Value);
        if (normalizedHash is null)
        {
            return null;
        }

        string? title = null;
        if (infoHashDefinition["title"] is JsonObject titleField)
        {
            var extractedTitle = ExtractField(row, titleField, context);
            if (extractedTitle.Found && !string.IsNullOrWhiteSpace(extractedTitle.Value))
            {
                title = extractedTitle.Value.Trim();
            }
        }

        return new DownloadInfoHashResolution(normalizedHash, title);
    }

    private FieldValue ExtractField(
        IResponseRow row,
        JsonObject field,
        CardigannTemplateContext context
    )
    {
        var textNode = field["text"];
        FieldValue selected;
        if (textNode is not null)
        {
            selected = new(true, templates.Render(CardigannValueFilters.Scalar(textNode), context));
        }
        else
        {
            var selector = templates.Render(
                CardigannDefinitionParser.Text(field, "selector") ?? string.Empty,
                context
            );
            var attribute = CardigannDefinitionParser.Text(field, "attribute");
            var remove = CardigannDefinitionParser.Text(field, "remove");
            selected = row.Extract(selector, attribute, remove);
        }

        if (!selected.Found && field["optional"]?.GetValue<bool>() == true)
        {
            selected = new(
                true,
                templates.Render(CardigannValueFilters.Scalar(field["default"]), context)
            );
        }

        if (selected.Found && field["case"] is JsonObject cases)
        {
            selected = new(true, ApplyCase(selected.Value, cases, context));
        }

        return selected.Found
            ? new(
                true,
                filters.Apply(selected.Value, field["filters"]?.AsArray(), context)
            )
            : selected;
    }

    private string ApplyCase(
        string value,
        JsonObject cases,
        CardigannTemplateContext context
    )
    {
        string? fallback = null;
        foreach (var (condition, output) in cases)
        {
            if (condition == "*")
            {
                fallback = CardigannValueFilters.Scalar(output);
                continue;
            }

            if (value.Equals(condition, StringComparison.OrdinalIgnoreCase))
            {
                return templates.Render(CardigannValueFilters.Scalar(output), context);
            }

            var contains = System.Text.RegularExpressions.Regex.Match(
                condition,
                "^:contains\\(\\\"(.*)\\\"\\)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            );
            if (
                contains.Success
                && value.Contains(contains.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
            )
            {
                return templates.Render(CardigannValueFilters.Scalar(output), context);
            }
        }

        return templates.Render(fallback ?? value, context);
    }

    private static bool MatchesRowFilters(
        IResponseRow row,
        JsonArray? rowFilters,
        CardigannTemplateContext context
    )
    {
        if (
            rowFilters is null
            || !rowFilters.OfType<JsonObject>().Any(filter =>
                CardigannDefinitionParser.Text(filter, "name")?.Equals(
                    "andmatch",
                    StringComparison.OrdinalIgnoreCase
                ) == true
            )
            || string.IsNullOrWhiteSpace(context.Keywords)
        )
        {
            return true;
        }

        var words = context.Keywords.Split(
            [' ', '.', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return words.All(word => row.Text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<IResponseRow> CreateRows(
        string responseType,
        string content,
        string selector,
        JsonObject rowsDefinition
    ) =>
        responseType.ToLowerInvariant() switch
        {
            "json" => JsonResponseRow.Select(content, selector, rowsDefinition),
            "xml" => XmlResponseRow.Select(content, selector),
            "html" or "" => HtmlResponseRow.Select(
                content,
                selector,
                rowsDefinition["after"]?.GetValue<int>() ?? 0
            ),
            _ => throw new InvalidDataException(
                $"Unsupported Cardigann response type '{responseType}'."
            ),
        };

    private interface IResponseRow
    {
        string Text { get; }

        FieldValue Extract(string selector, string? attribute, string? remove);
    }

    private sealed class HtmlResponseRow(IElement element) : IResponseRow
    {
        public string Text => element.TextContent;

        public FieldValue Extract(string selector, string? attribute, string? remove)
        {
            IElement? selected;
            try
            {
                selected = selector.StartsWith("/", StringComparison.Ordinal)
                    ? element.SelectSingleNode(selector) as IElement
                    : SelectCardigannElement(element, selector);
            }
            catch (Exception ex) when (
                ex is DomException or ArgumentException or XPathException
            )
            {
                throw new InvalidDataException($"Invalid HTML selector '{selector}'.", ex);
            }

            if (selected is null)
            {
                return new(false, string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(attribute))
            {
                var attributeValue = selected.GetAttribute(attribute);
                return attributeValue is null
                    ? new(false, string.Empty)
                    : new(true, attributeValue);
            }

            if (!string.IsNullOrWhiteSpace(remove))
            {
                selected = selected.Clone(deep: true) as IElement ?? selected;
                foreach (var removed in selected.QuerySelectorAll(remove).ToList())
                {
                    removed.Remove();
                }
            }

            return new(true, selected.TextContent);
        }

        private static IElement? SelectCardigannElement(IElement root, string selector)
        {
            var contains = System.Text.RegularExpressions.Regex.Match(
                selector,
                "^(?<base>.+?):contains\\((?:\\\"(?<double>.*?)\\\"|'(?<single>.*?)')\\)(?:\\s*~\\s*(?<sibling>.+))?$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            );
            if (!contains.Success)
            {
                return root.QuerySelector(selector);
            }

            var text = contains.Groups["double"].Success
                ? contains.Groups["double"].Value
                : contains.Groups["single"].Value;
            var source = root.QuerySelectorAll(contains.Groups["base"].Value)
                .FirstOrDefault(candidate =>
                    candidate.TextContent.Contains(text, StringComparison.OrdinalIgnoreCase)
                );
            if (source is null || !contains.Groups["sibling"].Success)
            {
                return source;
            }

            var siblingSelector = contains.Groups["sibling"].Value.Trim();
            for (var sibling = source.NextElementSibling; sibling is not null; sibling = sibling.NextElementSibling)
            {
                if (sibling.Matches(siblingSelector))
                {
                    return sibling;
                }
            }

            return null;
        }

        public static IReadOnlyList<IResponseRow> Select(
            string content,
            string selector,
            int after = 0
        )
        {
            var document = new HtmlParser().ParseDocument(content);
            try
            {
                var elements = selector.StartsWith("/", StringComparison.Ordinal)
                    ? document.DocumentElement
                        ?.SelectNodes(selector)
                        .OfType<IElement>() ?? []
                    : document.QuerySelectorAll(selector);
                var selected = elements.ToList();
                if (after <= 0)
                {
                    return selected.Select(element => (IResponseRow)new HtmlResponseRow(element)).ToList();
                }

                var merged = new List<IResponseRow>();
                for (var index = 0; index < selected.Count; index += after + 1)
                {
                    var current = selected[index].Clone(deep: true) as IElement
                        ?? selected[index];
                    for (
                        var offset = 1;
                        offset <= after && index + offset < selected.Count;
                        offset++
                    )
                    {
                        var nodes = selected[index + offset].ChildNodes
                            .Select(node => node.Clone(deep: true))
                            .ToArray();
                        current.Append(nodes);
                    }

                    merged.Add(new HtmlResponseRow(current));
                }

                return merged;
            }
            catch (Exception ex) when (
                ex is DomException or ArgumentException or XPathException
            )
            {
                throw new InvalidDataException($"Invalid HTML row selector '{selector}'.", ex);
            }
        }
    }

    private sealed class JsonResponseRow(JsonNode node, JsonNode? parentContext = null)
        : IResponseRow
    {
        public string Text => CardigannValueFilters.Scalar(node);

        public FieldValue Extract(string selector, string? attribute, string? remove)
        {
            var selected = selector.StartsWith("..", StringComparison.Ordinal)
                && parentContext is not null
                    ? Resolve(parentContext, selector[2..].TrimStart('.'))
                    : Resolve(node, selector);
            return selected is null
                ? new(false, string.Empty)
                : new(true, CardigannValueFilters.Scalar(selected));
        }

        public static IReadOnlyList<IResponseRow> Select(
            string content,
            string selector,
            JsonObject rowsDefinition
        )
        {
            var root = JsonNode.Parse(content)
                ?? throw new InvalidDataException("JSON indexer response is empty.");
            var selected = Resolve(root, selector);
            var baseRows = selected switch
            {
                JsonArray array => array
                    .Where(node => node is not null)
                    .Select(node => node!)
                    .ToList(),
                null => [],
                _ => [selected],
            };
            var attribute = CardigannDefinitionParser.Text(rowsDefinition, "attribute");
            var multiple = rowsDefinition["multiple"]?.GetValue<bool>() == true;
            var missingAttributeEqualsNoResults =
                rowsDefinition["missingAttributeEqualsNoResults"]?.GetValue<bool>() == true;
            var output = new List<IResponseRow>();
            foreach (var baseRow in baseRows)
            {
                var value = string.IsNullOrWhiteSpace(attribute)
                    ? baseRow
                    : Resolve(baseRow, attribute);
                if (value is null)
                {
                    if (missingAttributeEqualsNoResults)
                    {
                        continue;
                    }

                    continue;
                }

                if (multiple && value is JsonArray values)
                {
                    output.AddRange(values
                        .Where(child => child is not null)
                        .Select(child => (IResponseRow)new JsonResponseRow(child!, baseRow)));
                }
                else
                {
                    output.Add(new JsonResponseRow(
                        value,
                        string.IsNullOrWhiteSpace(attribute) ? null : baseRow
                    ));
                }
            }

            return output;
        }

        private static JsonNode? Resolve(JsonNode start, string selector)
        {
            var current = start;
            var remaining = selector.Trim();
            while (remaining.StartsWith("..", StringComparison.Ordinal))
            {
                current = current.Parent;
                remaining = remaining[2..].TrimStart('.');
                if (current is null)
                {
                    return null;
                }
            }

            if (remaining is "" or "$" or ".")
            {
                return current;
            }

            foreach (var part in remaining.TrimStart('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current switch
                {
                    JsonObject obj when obj.TryGetPropertyValue(part, out var child) => child,
                    JsonArray array when int.TryParse(part, out var index)
                        && index >= 0
                        && index < array.Count => array[index],
                    _ => null,
                };
                if (current is null)
                {
                    return null;
                }
            }

            return current;
        }
    }

    private sealed class XmlResponseRow(XElement element) : IResponseRow
    {
        public string Text => element.Value;

        public FieldValue Extract(string selector, string? attribute, string? remove)
        {
            XElement? selected;
            if (selector.StartsWith("/", StringComparison.Ordinal))
            {
                selected = element.XPathSelectElement(selector);
            }
            else if (selector.StartsWith("[name=", StringComparison.Ordinal))
            {
                var name = selector[6..].TrimEnd(']', '"', '\'');
                selected = element.Descendants().FirstOrDefault(child =>
                    child.Attribute("name")?.Value.Equals(name, StringComparison.OrdinalIgnoreCase)
                    == true
                );
            }
            else
            {
                selected = SelectPath([element], selector).FirstOrDefault();
            }

            if (selected is null)
            {
                return new(false, string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(attribute))
            {
                var attributeValue = selected.Attributes().FirstOrDefault(item =>
                    item.Name.LocalName.Equals(attribute, StringComparison.OrdinalIgnoreCase)
                )?.Value;
                return attributeValue is null
                    ? new(false, string.Empty)
                    : new(true, attributeValue);
            }

            return new(true, selected.Value);
        }

        public static IReadOnlyList<IResponseRow> Select(string content, string selector)
        {
            using var textReader = new StringReader(content);
            using var xmlReader = XmlReader.Create(
                textReader,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 2 * 1024 * 1024,
                }
            );
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            if (document.Root is null)
            {
                return [];
            }

            var selected = selector.StartsWith("/", StringComparison.Ordinal)
                ? document.XPathSelectElements(selector)
                : SelectPath([document.Root], selector);
            return selected.Select(element => (IResponseRow)new XmlResponseRow(element)).ToList();
        }

        private static IEnumerable<XElement> SelectPath(
            IEnumerable<XElement> starts,
            string selector
        )
        {
            var current = starts;
            var first = true;
            foreach (var part in selector.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = part.Contains(':', StringComparison.Ordinal)
                    ? part[(part.IndexOf(':') + 1)..]
                    : part;
                current = first
                    ? current.SelectMany(element =>
                        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
                            ? [element]
                            : element.Elements().Where(child =>
                                child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
                            )
                    )
                    : current.SelectMany(element =>
                        element.Elements().Where(child =>
                            child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
                        )
                    );
                first = false;
            }

            return current;
        }
    }

    private readonly record struct FieldValue(bool Found, string Value);
}

internal sealed record DownloadInfoHashResolution(string InfoHash, string? Title);
