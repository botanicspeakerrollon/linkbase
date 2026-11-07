using System.Globalization;
using Linkbase.Data;

namespace Linkbase.Domains.Climbing.Features;

// How stored values read on screen.
public static class Labels
{
    public static string Of(AscentStyle style) => style switch
    {
        AscentStyle.TopRope => "Top rope",
        AscentStyle.Dnf => "DNF",
        _ => style.ToString(),
    };

    public static string Of(GradeSystem? system) => system switch
    {
        null => "",
        GradeSystem.UkTrad => "UK trad",
        GradeSystem.UkTech => "UK tech",
        GradeSystem.Yds => "YDS",
        GradeSystem.V => "V grade",
        GradeSystem.Uiaa => "UIAA",
        GradeSystem.ViaFerrata => "Via ferrata",
        _ => system.Value.ToString(),
    };

    public static string Of(Discipline? discipline) => discipline?.ToString() ?? "";

    public static string Of(DatePrecision precision) => precision switch
    {
        DatePrecision.Day => "Exact day",
        DatePrecision.Month => "Month only",
        _ => "Year only",
    };

    public static string Date(DateOnly date, DatePrecision precision) => precision switch
    {
        DatePrecision.Day => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        DatePrecision.Month => date.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        _ => date.ToString("yyyy", CultureInfo.InvariantCulture),
    };

    public static string Count(int count, string singular) => count == 1 ? $"1 {singular}" : $"{count} {singular}s";

    public static string Stars(short? rating) => rating is > 0 ? new string('★', rating.Value) : "";
}
