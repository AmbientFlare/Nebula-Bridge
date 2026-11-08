using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NebulaBridge.NativeSources;

public sealed class CardigannValueFilters(CardigannTemplateEngine templates)
{
    public string Apply(
        string value,
        JsonArray? filters,
        CardigannTemplateContext context
    )
    {
        foreach (var filterNode in filters ?? [])
        {
            if (filterNode is not JsonObject filter)
            {
                continue;
            }

            var name = CardigannDefinitionParser.Text(filter, "name")?.ToLowerInvariant();
            var arguments = Arguments(filter["args"], context);
            value = name switch
            {
                "andmatch" => value,
                "append" => value + arguments.FirstOrDefault(),
                "dateparse" => ParseExactDate(value, arguments.FirstOrDefault()),
                "fuzzytime" => ParseFuzzyTime(value),
                "prepend" => arguments.FirstOrDefault() + value,
                "querystring" => QueryStringValue(value, arguments.FirstOrDefault()),
                "re_replace" => ReplaceRegex(value, arguments),
                "regexp" => ExtractRegex(value, arguments.FirstOrDefault()),
                "replace" => value.Replace(
                    arguments.ElementAtOrDefault(0) ?? string.Empty,
                    arguments.ElementAtOrDefault(1) ?? string.Empty,
                    StringComparison.Ordinal
                ),
                "split" => Split(value, arguments),
                "timeago" => ParseRelativeTime(value),
                "tolower" => value.ToLowerInvariant(),
                "trim" => value.Trim(),
                "urldecode" => Uri.UnescapeDataString(value.Replace('+', ' ')),
                "validfilename" => SanitizeFileName(value),
                null or "" => value,
                _ => throw new InvalidOperationException(
                    $"Unsupported Cardigann filter '{name}'."
                ),
            };
        }

        return value.Trim();
    }

    private List<string> Arguments(JsonNode? node, CardigannTemplateContext context)
    {
        if (node is null)
        {
            return [];
        }

        return node is JsonArray array
            ? array.Select(value => templates.Render(Scalar(value), context)).ToList()
            : [templates.Render(Scalar(node), context)];
    }

    internal static string Scalar(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean ? "true" : "false";
            }

            if (value.TryGetValue<long>(out var integer))
            {
                return integer.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }
        }

        return node.ToJsonString().Trim('"');
    }

    private static string ReplaceRegex(string value, IReadOnlyList<string> arguments)
    {
        var pattern = arguments.ElementAtOrDefault(0) ?? string.Empty;
        var replacement = arguments.ElementAtOrDefault(1) ?? string.Empty;
        return Regex.Replace(
            value,
            pattern,
            replacement,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );
    }

    private static string ExtractRegex(string value, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return value;
        }

        var match = Regex.Match(
            value,
            pattern,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );
        return !match.Success
            ? string.Empty
            : match.Groups.Count > 1
                ? match.Groups[1].Value
                : match.Value;
    }

    private static string QueryStringValue(string value, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var question = value.IndexOf('?');
        var query = question >= 0 ? value[(question + 1)..] : value;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string Split(string value, IReadOnlyList<string> arguments)
    {
        var separator = arguments.ElementAtOrDefault(0) ?? string.Empty;
        if (
            string.IsNullOrEmpty(separator)
            || !int.TryParse(arguments.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
        )
        {
            return value;
        }

        var parts = value.Split(separator, StringSplitOptions.None);
        if (index < 0)
        {
            index = parts.Length + index;
        }

        return index >= 0 && index < parts.Length ? parts[index] : string.Empty;
    }

    private static string ParseExactDate(string value, string? format)
    {
        if (
            !string.IsNullOrWhiteSpace(format)
            && DateTimeOffset.TryParseExact(
                value.Trim(),
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var exact
            )
        )
        {
            return exact.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        return value;
    }

    private static string ParseRelativeTime(string value)
    {
        var match = Regex.Match(
            value,
            @"(?<amount>\d+(?:\.\d+)?)\s*(?<unit>seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|weeks?|months?|years?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );
        if (!match.Success || !double.TryParse(match.Groups["amount"].Value, CultureInfo.InvariantCulture, out var amount))
        {
            return value;
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        var duration = unit[0] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' when unit.StartsWith("min", StringComparison.Ordinal) => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(amount * 7),
            'm' => TimeSpan.FromDays(amount * 30),
            'y' => TimeSpan.FromDays(amount * 365),
            _ => TimeSpan.Zero,
        };
        return (DateTimeOffset.UtcNow - duration).ToString("O", CultureInfo.InvariantCulture);
    }

    private static string ParseFuzzyTime(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("today", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDayTime(trimmed[5..], DateTimeOffset.UtcNow.Date);
        }

        if (trimmed.StartsWith("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDayTime(trimmed[9..], DateTimeOffset.UtcNow.Date.AddDays(-1));
        }

        return ParseRelativeTime(value);
    }

    private static string ParseDayTime(string value, DateTime day)
    {
        return DateTime.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var time
        )
            ? new DateTimeOffset(day.Add(time.TimeOfDay), TimeSpan.Zero).ToString(
                "O",
                CultureInfo.InvariantCulture
            )
            : new DateTimeOffset(day, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
