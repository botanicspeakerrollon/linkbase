using System.Text;

namespace Linkbase.Core.Records;

public static class NameMatching
{
    // Equal results mean "the same name": ignores case, accents, punctuation and spacing.
    public static string Normalise(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.Length > 0 ? builder.ToString() : name.Trim().ToLowerInvariant();
    }

    // True for names in a numbered or lettered series, such as "Red 13 Problem 3" and "Red 11 Problem 7",
    // or "Problem A" and "Problem C". These look alike to trigrams but are different climbs.
    public static bool OnlyNumberingDiffers(string first, string second) =>
        NumberingStripped(first).SequenceEqual(NumberingStripped(second));

    private static IEnumerable<string> NumberingStripped(string name) =>
        name.ToLowerInvariant()
            .Split(name.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries)
            .Select(token => new string(token.Where(c => !char.IsDigit(c)).ToArray()))
            .Where(token => token.Length > 1);
}
