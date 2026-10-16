using System.Globalization;

namespace SGuard.AspNetCore;

/// <summary>
/// Turns the expression SGuard captured at a guard's call site into the path of the member it checked.
/// </summary>
internal static class MemberPath
{
    // C#'s reserved keywords: without '@' they are a literal, an operator or a type, never a member.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    };

    /// <summary>
    /// Returns the member path of <paramref name="expression"/> without its first part, which is the variable or
    /// lambda parameter that holds the object: <c>request.Address.City</c> and <c>r =&gt; r.Address!.City</c> become
    /// <c>Address.City</c>, and <c>name</c> stays <c>name</c>. Returns the empty string for anything that isn't a plain
    /// member access, such as a method call, an indexer, a literal or a cast.
    /// </summary>
    public static string FromExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return string.Empty;
        }

        var text = expression.AsSpan().Trim();
        var arrow = text.IndexOf("=>".AsSpan(), StringComparison.Ordinal);

        if (arrow >= 0)
        {
            // A selector: "r => r.Address.City". The body must start with the lambda's parameter.
            if (!TryGetLambdaParameter(text[..arrow], out var parameter) ||
                !TryParse(text[(arrow + 2)..], out var bodyParts) || bodyParts.Count < 2 ||
                bodyParts[0] != parameter)
            {
                return string.Empty;
            }

            return string.Join(".", bodyParts.Skip(1));
        }

        if (!TryParse(text, out var parts))
        {
            return string.Empty;
        }

        // "this.Name" and "base.Name" are members of the current object; "this" alone is not a member.
        if (parts.Count == 1)
        {
            return parts[0] is "this" or "base" ? string.Empty : parts[0];
        }

        return string.Join(".", parts.Skip(1));
    }

    // The parameter of "r", "@r", "(r)", "static r", "(Order r)" or "static (Order r)": exactly one parameter.
    private static bool TryGetLambdaParameter(ReadOnlySpan<char> text, out string parameter)
    {
        parameter = string.Empty;
        text = text.Trim();

        if (text.StartsWith("static".AsSpan(), StringComparison.Ordinal) && text.Length > 6 &&
            (char.IsWhiteSpace(text[6]) || text[6] == '('))
        {
            text = text[6..].Trim();
        }

        if (text.Length > 1 && text[0] == '(' && text[^1] == ')')
        {
            text = text[1..^1].Trim();

            if (text.IndexOf(',') >= 0)
            {
                return false;
            }

            // A typed parameter, "Order r": the name is the last word.
            var space = text.LastIndexOfAny(" \t\r\n".AsSpan());

            if (space >= 0)
            {
                text = text[(space + 1)..];
            }
        }

        var verbatim = !text.IsEmpty && text[0] == '@';

        if (verbatim)
        {
            text = text[1..];
        }

        if (!IsIdentifier(text))
        {
            return false;
        }

        parameter = text.ToString();
        return verbatim || !Keywords.Contains(parameter);
    }

    // Splits "a.b?.c!.d" into identifiers, without the '@' of verbatim identifiers, allowing whitespace around the
    // separators (a chain split over several lines). Fails on anything else, and on a keyword after the first part.
    private static bool TryParse(ReadOnlySpan<char> text, out List<string> parts)
    {
        parts = [];
        text = text.Trim();

        while (true)
        {
            var verbatim = !text.IsEmpty && text[0] == '@';

            if (verbatim)
            {
                text = text[1..];
            }

            var length = 0;

            while (length < text.Length && IsIdentifierChar(text[length], length == 0))
            {
                length++;
            }

            if (length == 0)
            {
                return false;
            }

            var part = text[..length].ToString();

            // Only the first part may be "this" or "base"; any other keyword makes this a literal or an operator.
            if (!verbatim && Keywords.Contains(part) && !(parts.Count == 0 && part is "this" or "base"))
            {
                return false;
            }

            parts.Add(part);
            text = text[length..].TrimStart();

            if (!text.IsEmpty && text[0] == '!')
            {
                text = text[1..].TrimStart();
            }

            if (text.IsEmpty)
            {
                return true;
            }

            if (text.StartsWith("?.".AsSpan(), StringComparison.Ordinal))
            {
                text = text[2..].TrimStart();
            }
            else if (text[0] == '.')
            {
                text = text[1..].TrimStart();
            }
            else
            {
                return false;
            }
        }
    }

    private static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (!IsIdentifierChar(text[i], i == 0))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierChar(char c, bool first)
        => c == '_' || char.IsLetter(c) || (!first && (char.IsDigit(c) || char.GetUnicodeCategory(c) is
                                                           UnicodeCategory.NonSpacingMark or
                                                           UnicodeCategory.SpacingCombiningMark or
                                                           UnicodeCategory.ConnectorPunctuation));
}
