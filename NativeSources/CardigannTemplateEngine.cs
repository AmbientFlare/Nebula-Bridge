using System.Text;
using System.Text.RegularExpressions;

namespace NebulaBridge.NativeSources;

public sealed record CardigannTemplateContext(
    string Keywords,
    IReadOnlyDictionary<string, object?> Query,
    IReadOnlyDictionary<string, object?> Config,
    IReadOnlyDictionary<string, string> Result,
    IReadOnlyList<string> Categories,
    object? Current = null
);

public sealed class CardigannTemplateEngine
{
    public string Render(string? template, CardigannTemplateContext context)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal))
        {
            return template ?? string.Empty;
        }

        var tokens = Tokenize(template);
        var index = 0;
        var nodes = ParseNodes(tokens, ref index, out var terminator);
        if (terminator is not null)
        {
            throw new InvalidOperationException(
                $"Unexpected Cardigann template token '{{{{ {terminator} }}}}'."
            );
        }

        var output = new StringBuilder(template.Length);
        RenderNodes(nodes, context, output);
        return output.ToString();
    }

    private static IReadOnlyList<TemplateNode> ParseNodes(
        IReadOnlyList<TemplateToken> tokens,
        ref int index,
        out string? terminator
    )
    {
        var nodes = new List<TemplateNode>();
        terminator = null;
        while (index < tokens.Count)
        {
            var token = tokens[index++];
            if (!token.Expression)
            {
                nodes.Add(new TextNode(token.Value));
                continue;
            }

            var expression = token.Value.Trim();
            if (expression is "else" or "end")
            {
                terminator = expression;
                return nodes;
            }

            if (expression.StartsWith("if ", StringComparison.Ordinal) || expression == "if")
            {
                var condition = expression.Length > 2 ? expression[3..].Trim() : string.Empty;
                var whenTrue = ParseNodes(tokens, ref index, out var branchEnd);
                IReadOnlyList<TemplateNode> whenFalse = [];
                if (branchEnd == "else")
                {
                    whenFalse = ParseNodes(tokens, ref index, out branchEnd);
                }

                if (branchEnd != "end")
                {
                    throw new InvalidOperationException("Cardigann template if block has no end.");
                }

                nodes.Add(new IfNode(condition, whenTrue, whenFalse));
                continue;
            }

            if (expression.StartsWith("range ", StringComparison.Ordinal))
            {
                var rangeExpression = expression[6..].Trim();
                var body = ParseNodes(tokens, ref index, out var rangeEnd);
                if (rangeEnd != "end")
                {
                    throw new InvalidOperationException("Cardigann template range block has no end.");
                }

                nodes.Add(new RangeNode(rangeExpression, body));
                continue;
            }

            nodes.Add(new ValueNode(expression));
        }

        return nodes;
    }

    private void RenderNodes(
        IReadOnlyList<TemplateNode> nodes,
        CardigannTemplateContext context,
        StringBuilder output
    )
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    output.Append(text.Value);
                    break;
                case ValueNode value:
                    output.Append(ToText(Evaluate(value.Expression, context)));
                    break;
                case IfNode conditional:
                    RenderNodes(
                        Truthy(Evaluate(conditional.Condition, context))
                            ? conditional.WhenTrue
                            : conditional.WhenFalse,
                        context,
                        output
                    );
                    break;
                case RangeNode range:
                    foreach (var item in AsSequence(Evaluate(range.Expression, context)))
                    {
                        RenderNodes(range.Body, context with { Current = item }, output);
                    }
                    break;
            }
        }
    }

    private object? Evaluate(string expression, CardigannTemplateContext context)
    {
        expression = StripOuterParentheses(expression.Trim());
        var arguments = SplitArguments(expression);
        if (arguments.Count == 0)
        {
            return null;
        }

        if (arguments.Count == 1)
        {
            return ResolveValue(arguments[0], context);
        }

        return arguments[0] switch
        {
            "and" => arguments.Skip(1).All(argument => Truthy(Evaluate(argument, context))),
            "or" => arguments.Skip(1).Any(argument => Truthy(Evaluate(argument, context))),
            "not" => !Truthy(Evaluate(arguments[1], context)),
            "eq" => arguments.Skip(1).Select(argument => ToText(Evaluate(argument, context)))
                .Distinct(StringComparer.Ordinal).Count() == 1,
            "ne" => !string.Equals(
                ToText(Evaluate(arguments[1], context)),
                ToText(Evaluate(arguments[2], context)),
                StringComparison.Ordinal
            ),
            "join" => string.Join(
                ToText(Evaluate(arguments[2], context)),
                AsSequence(Evaluate(arguments[1], context)).Select(ToText)
            ),
            "re_replace" => Regex.Replace(
                ToText(Evaluate(arguments[1], context)),
                ToText(Evaluate(arguments[2], context)),
                ToText(Evaluate(arguments[3], context)),
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported Cardigann template function '{arguments[0]}'."
            ),
        };
    }

    private static object? ResolveValue(string token, CardigannTemplateContext context)
    {
        token = StripOuterParentheses(token.Trim());
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
        {
            return Regex.Unescape(token[1..^1]);
        }

        if (bool.TryParse(token, out var boolean))
        {
            return boolean;
        }

        if (decimal.TryParse(token, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        if (token == ".")
        {
            return context.Current;
        }

        if (token == ".Keywords")
        {
            return context.Keywords;
        }

        if (token == ".Categories")
        {
            return context.Categories;
        }

        if (token == ".Today.Year")
        {
            return DateTime.UtcNow.Year;
        }

        if (token is ".False" or "nil")
        {
            return false;
        }

        if (token == ".True")
        {
            return true;
        }

        if (token.StartsWith(".Query.", StringComparison.Ordinal))
        {
            return Lookup(context.Query, token[7..]);
        }

        if (token.StartsWith(".Config.", StringComparison.Ordinal))
        {
            return Lookup(context.Config, token[8..]);
        }

        if (token.StartsWith(".Result.", StringComparison.Ordinal))
        {
            return Lookup(context.Result, token[8..]);
        }

        return token;
    }

    private static object? Lookup<T>(IReadOnlyDictionary<string, T> values, string key)
    {
        if (values.TryGetValue(key, out var direct))
        {
            return direct;
        }

        var match = values.FirstOrDefault(pair =>
            pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
        );
        return match.Key is null ? null : match.Value;
    }

    private static IReadOnlyList<object?> AsSequence(object? value) =>
        value switch
        {
            null => [],
            IEnumerable<string> strings => strings.Cast<object?>().ToList(),
            System.Collections.IEnumerable items when value is not string => items
                .Cast<object?>()
                .ToList(),
            _ => [value],
        };

    private static bool Truthy(object? value) =>
        value switch
        {
            null => false,
            bool boolean => boolean,
            string text => !string.IsNullOrEmpty(text) && !text.Equals("false", StringComparison.OrdinalIgnoreCase),
            decimal number => number != 0,
            int number => number != 0,
            long number => number != 0,
            System.Collections.ICollection collection => collection.Count > 0,
            _ => true,
        };

    private static string ToText(object? value) =>
        value switch
        {
            null => string.Empty,
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(
                null,
                System.Globalization.CultureInfo.InvariantCulture
            ),
            _ => value.ToString() ?? string.Empty,
        };

    private static List<TemplateToken> Tokenize(string template)
    {
        var tokens = new List<TemplateToken>();
        var offset = 0;
        foreach (Match match in Regex.Matches(template, "{{(.*?)}}", RegexOptions.Singleline))
        {
            if (match.Index > offset)
            {
                tokens.Add(new(false, template[offset..match.Index]));
            }

            tokens.Add(new(true, match.Groups[1].Value));
            offset = match.Index + match.Length;
        }

        if (offset < template.Length)
        {
            tokens.Add(new(false, template[offset..]));
        }

        return tokens;
    }

    private static List<string> SplitArguments(string expression)
    {
        var arguments = new List<string>();
        var start = -1;
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < expression.Length; index++)
        {
            var character = expression[index];
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    quoted = false;
                }

                continue;
            }

            if (character == '"')
            {
                quoted = true;
                if (start < 0)
                {
                    start = index;
                }
            }
            else if (character == '(')
            {
                depth++;
                if (start < 0)
                {
                    start = index;
                }
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (char.IsWhiteSpace(character) && depth == 0)
            {
                if (start >= 0)
                {
                    arguments.Add(expression[start..index]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = index;
            }
        }

        if (start >= 0)
        {
            arguments.Add(expression[start..]);
        }

        return arguments;
    }

    private static string StripOuterParentheses(string expression)
    {
        while (expression.Length >= 2 && expression[0] == '(' && expression[^1] == ')')
        {
            var depth = 0;
            var wrapsAll = true;
            for (var index = 0; index < expression.Length; index++)
            {
                if (expression[index] == '(')
                {
                    depth++;
                }
                else if (expression[index] == ')')
                {
                    depth--;
                    if (depth == 0 && index != expression.Length - 1)
                    {
                        wrapsAll = false;
                        break;
                    }
                }
            }

            if (!wrapsAll)
            {
                break;
            }

            expression = expression[1..^1].Trim();
        }

        return expression;
    }

    private sealed record TemplateToken(bool Expression, string Value);

    private abstract record TemplateNode;

    private sealed record TextNode(string Value) : TemplateNode;

    private sealed record ValueNode(string Expression) : TemplateNode;

    private sealed record IfNode(
        string Condition,
        IReadOnlyList<TemplateNode> WhenTrue,
        IReadOnlyList<TemplateNode> WhenFalse
    ) : TemplateNode;

    private sealed record RangeNode(string Expression, IReadOnlyList<TemplateNode> Body)
        : TemplateNode;
}
