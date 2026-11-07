using System.Globalization;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;

namespace Linkbase.Domains.Climbing.Import;

public static class UkcParser
{
    public static List<ParsedAscent> Parse(TextReader reader)
    {
        var ascents = new List<ParsedAscent>();
        var occurrences = new Dictionary<string, int>();

        foreach (var row in ExportText.ReadRows(reader))
        {
            var crag = ExportText.Clean(row["Crag name"]);
            var name = ExportText.Clean(row["Climb name"]);
            var dateText = row["Date"].Trim();
            var (date, precision) = ParseDate(dateText);
            var (grade, _) = SplitGrade(row["Grade"]);
            var styleRaw = ExportText.CleanOrNull(row["Style"]);
            var routeKey = ExportText.ExternalKey($"{crag}|{name}");

            ascents.Add(new ParsedAscent
            {
                Source = DataSource.Ukc,
                AreaPath = [ExportText.Clean(row["Country"])],
                CragName = crag,
                RouteName = name,
                RouteExternalId = routeKey,
                Grade = grade,
                GradeSystem = MapGradeSystem(row["Grade System"].Trim()),
                Discipline = MapDiscipline(row["Grade Type"].Trim()),
                AscentExternalId = ExportText.NextOccurrence(occurrences, $"{routeKey}|{dateText}|{ExportText.ExternalKey(styleRaw ?? "")}"),
                Date = date,
                DatePrecision = precision,
                Style = MapStyle(styleRaw),
                StyleRaw = styleRaw,
                Notes = ExportText.CleanOrNull(row["Notes"]),
                Partners = SplitPartners(row["Partner(s)"]),
                Raw = row,
            });
        }

        return ascents;
    }

    // Dates look like 20/Aug/26, ??/May/25 (day unknown) or ???/2015 (year only).
    public static (DateOnly Date, DatePrecision Precision) ParseDate(string text)
    {
        var parts = text.Split('/');
        if (parts is [_, var yearOnly] && int.TryParse(yearOnly, out var fullYear))
        {
            return (new DateOnly(fullYear, 1, 1), DatePrecision.Year);
        }

        if (parts is [var day, var month, var shortYear]
            && DateTime.TryParseExact(month, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedMonth)
            && int.TryParse(shortYear, out var year))
        {
            if (day == "??")
            {
                return (new DateOnly(2000 + year, parsedMonth.Month, 1), DatePrecision.Month);
            }

            if (int.TryParse(day, out var parsedDay))
            {
                return (new DateOnly(2000 + year, parsedMonth.Month, parsedDay), DatePrecision.Day);
            }
        }

        throw new FormatException($"Unrecognised UKC date '{text}'.");
    }

    // Stars are UKC's community rating, not the climber's, so they are separated and dropped.
    public static (string? Grade, int Stars) SplitGrade(string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var grade = string.Join(' ', parts.Where(p => !p.StartsWith('*')));
        return (grade.Length == 0 ? null : grade, raw.Count(c => c == '*'));
    }

    public static IReadOnlyList<string> SplitPartners(string raw) =>
        raw.Replace(" and ", ",").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ExportText.Clean)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static AscentStyle MapStyle(string? raw)
    {
        if (raw is null)
        {
            return AscentStyle.Other;
        }

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outcome = parts.Length > 1 ? parts[1] : "";
        if (outcome == "dnf")
        {
            return AscentStyle.Dnf;
        }

        return parts[0] switch
        {
            "Lead" or "AltLd" => outcome switch
            {
                "O/S" => AscentStyle.Onsight,
                "RP" => AscentStyle.Redpoint,
                "β" => AscentStyle.Flash,
                "dog" => AscentStyle.Hangdog,
                _ => AscentStyle.Lead,
            },
            "Sent" => outcome switch
            {
                "O/S" => AscentStyle.Onsight,
                "β" => AscentStyle.Flash,
                _ => AscentStyle.Send,
            },
            "2nd" => AscentStyle.Second,
            "TR" => AscentStyle.TopRope,
            "Solo" or "DWS" => AscentStyle.Solo,
            _ => AscentStyle.Other,
        };
    }

    public static GradeSystem MapGradeSystem(string system) => system switch
    {
        "British" => Linkbase.Data.GradeSystem.UkTrad,
        "French" => Linkbase.Data.GradeSystem.French,
        // UKC's V-grades rows actually hold Font grades such as f6C.
        "Font" or "V-grades" => Linkbase.Data.GradeSystem.Font,
        "USA" => Linkbase.Data.GradeSystem.Yds,
        "UIAA" => Linkbase.Data.GradeSystem.Uiaa,
        "Australian" => Linkbase.Data.GradeSystem.Australian,
        "Nordic" => Linkbase.Data.GradeSystem.Nordic,
        "Alpine" => Linkbase.Data.GradeSystem.Alpine,
        "Scrambling" => Linkbase.Data.GradeSystem.Scrambling,
        "Via Ferrata" => Linkbase.Data.GradeSystem.ViaFerrata,
        "Ice" => Linkbase.Data.GradeSystem.Ice,
        _ => Linkbase.Data.GradeSystem.Other,
    };

    public static Discipline MapDiscipline(string type) => type switch
    {
        "Trad" => Linkbase.Data.Discipline.Trad,
        "Sport" => Linkbase.Data.Discipline.Sport,
        "Bouldering" => Linkbase.Data.Discipline.Boulder,
        "Alpine" => Linkbase.Data.Discipline.Alpine,
        "Ice" => Linkbase.Data.Discipline.Winter,
        _ => Linkbase.Data.Discipline.Other,
    };
}
