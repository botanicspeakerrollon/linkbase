using Linkbase.Data;
using System.Text.RegularExpressions;

namespace Linkbase.Domains.Climbing.Data;

// Orders grades within one system, for "hardest grade" stats. Null means the grade can't be ranked.
public static partial class GradeRanking
{
    private static readonly string[] UkAdjectival =
        ["M", "MOD", "D", "HD", "VD", "HVD", "MS", "S", "HS", "MVS", "VS", "HVS"];

    private static readonly string[] Alpine = ["F", "PD", "AD", "D", "TD", "ED"];

    private static readonly string[] Roman = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII"];

    public static double? Rank(GradeSystem? system, string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade))
        {
            return null;
        }

        return system switch
        {
            GradeSystem.UkTrad => RankUkTrad(grade),
            GradeSystem.French => RankLettered(FrenchGrade().Match(grade)),
            GradeSystem.Font => RankLettered(FontGrade().Match(grade)),
            GradeSystem.Yds => RankYds(grade),
            GradeSystem.V => RankPlusMinus(VGrade().Match(grade)),
            GradeSystem.Nordic => RankPlusMinus(NordicGrade().Match(grade)),
            GradeSystem.Australian => RankPlusMinus(LeadingNumber().Match(grade)),
            GradeSystem.Uiaa => RankUiaa(grade),
            GradeSystem.Alpine => RankAlpine(grade),
            _ => null,
        };
    }

    // Adjectival grades, then E grades, with the technical grade breaking ties: "E2 5c" beats "E2 5b".
    private static double? RankUkTrad(string grade)
    {
        var parts = grade.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var adjectival = parts[0].ToUpperInvariant();
        double? rank = adjectival.StartsWith('E') && int.TryParse(adjectival[1..], out var e)
            ? UkAdjectival.Length + e
            : Array.IndexOf(UkAdjectival, adjectival) is >= 0 and var index ? index : null;
        if (rank is null)
        {
            return null;
        }

        var technical = parts.Length > 1 ? TechnicalGrade().Match(parts[1]) : Match.Empty;
        return technical.Success
            ? rank + (int.Parse(technical.Groups[1].Value) * 3 + (technical.Groups[2].Value[0] - 'a')) / 100.0
            : rank;
    }

    // 6a < 6a+ < 6b, and likewise for Font's f6A.
    private static double? RankLettered(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        var letter = match.Groups[2].Success ? char.ToLowerInvariant(match.Groups[2].Value[0]) - 'a' : 0;
        var plus = match.Groups[3].Success ? 0.5 : 0;
        return int.Parse(match.Groups[1].Value) * 10 + letter * 2 + plus;
    }

    // 5.10a < 5.10b. 5.10- means a or b, plain 5.10 is mid-range, and 5.10+ means c or d.
    // 5.11a/b sits between its letters.
    private static double? RankYds(string grade)
    {
        var match = YdsGrade().Match(grade);
        if (!match.Success)
        {
            return null;
        }

        var number = int.Parse(match.Groups[1].Value) * 10;
        var suffix = match.Groups[2].Value;
        double offset = suffix switch
        {
            "" => 1.5,
            "-" => 0.5,
            "+" => 2.5,
            _ => suffix[0] - 'a',
        };
        return number + offset + (match.Groups[3].Success ? 0.5 : 0);
    }

    private static double? RankPlusMinus(Match match)
    {
        if (!match.Success)
        {
            return null;
        }

        var suffix = match.Groups[2].Value;
        return int.Parse(match.Groups[1].Value) + (suffix == "+" ? 0.25 : suffix == "-" ? -0.25 : 0);
    }

    private static double? RankUiaa(string grade)
    {
        var match = UiaaGrade().Match(grade);
        if (!match.Success || Array.IndexOf(Roman, match.Groups[1].Value) is not (>= 0 and var index))
        {
            return null;
        }

        var suffix = match.Groups[2].Value;
        return index + (suffix == "+" ? 0.25 : suffix == "-" ? -0.25 : 0);
    }

    private static double? RankAlpine(string grade)
    {
        var match = AlpineGrade().Match(grade);
        if (!match.Success || Array.IndexOf(Alpine, match.Groups[1].Value) is not (>= 0 and var index))
        {
            return null;
        }

        var suffix = match.Groups[2].Value;
        return index + (suffix == "+" ? 0.25 : suffix == "-" ? -0.25 : 0);
    }

    [GeneratedRegex(@"^(\d)([abc])")]
    private static partial Regex TechnicalGrade();

    [GeneratedRegex(@"^(\d+)([abc])?(\+)?")]
    private static partial Regex FrenchGrade();

    [GeneratedRegex(@"^f?(\d+)([ABCabc])?(\+)?")]
    private static partial Regex FontGrade();

    [GeneratedRegex(@"^5\.(\d+)([abcd]|\+|-)?(/[abcd])?")]
    private static partial Regex YdsGrade();

    [GeneratedRegex(@"^V(\d+)(\+|-)?")]
    private static partial Regex VGrade();

    [GeneratedRegex(@"^n(\d+)(\+|-)?")]
    private static partial Regex NordicGrade();

    [GeneratedRegex(@"^(\d+)(\+|-)?")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^([IVX]+)(\+|-)?")]
    private static partial Regex UiaaGrade();

    [GeneratedRegex(@"^([A-Z]+)(\+|-)?")]
    private static partial Regex AlpineGrade();
}
