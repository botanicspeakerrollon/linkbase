using System.Text.RegularExpressions;

namespace Linkbase.Core.Models;

// A record's name made from its fields: "{name_first} {name_last}". Used by the loader (in SQL) and the record repository.
public static partial class NameTemplates
{
    // The field keys the template uses, in order.
    public static List<string> Tokens(string template) => Token().Matches(template).Select(m => m.Groups[1].Value).ToList();

    public readonly record struct Match(string Key, int Index, int Length);

    // Where each {field} is in the template.
    public static List<Match> Matches(string template) =>
        Token().Matches(template).Select(m => new Match(m.Groups[1].Value, m.Index, m.Length)).ToList();

    public static bool IsBalanced(string template) => !Token().Replace(template, "").AsSpan().ContainsAny('{', '}');

    // Fills in the values. A field without one drops out, and the space around it collapses. Null if nothing is left.
    public static string? Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var text = Token().Replace(template, m => values.GetValueOrDefault(m.Groups[1].Value)?.Trim() ?? "");
        text = Spaces().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"\{([a-z][a-z0-9_]{0,62})\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
