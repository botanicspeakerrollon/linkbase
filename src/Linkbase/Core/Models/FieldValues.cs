namespace Linkbase.Core.Models;

// Rules for field values that more than one place applies: the edit form, the CSV import and Explore.
public static class FieldValues
{
    // The most options a choice field can have. More than this is a list of values, not a choice.
    public const int MaxOptions = 50;

    // The option a value means, ignoring case, in the option's own spelling. Null when it isn't one of them.
    public static string? MatchChoice(string text, IReadOnlyList<string> options) =>
        options.FirstOrDefault(o => string.Equals(o, text.Trim(), StringComparison.OrdinalIgnoreCase));

    // Options in a readable list for messages, such as "lead, second or top rope".
    public static string Listed(IReadOnlyList<string> options) => options.Count switch
    {
        0 => "nothing",
        1 => options[0],
        _ => $"{string.Join(", ", options.Take(options.Count - 1))} or {options[^1]}",
    };
}
