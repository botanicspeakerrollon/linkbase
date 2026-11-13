using System.Globalization;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;

namespace Linkbase.Domains.Climbing.Import;

public static class MountainProjectParser
{
    // Mountain Project locations start at the state, so the country is added above them.
    private const string Country = "USA";
    private const double MetresPerFoot = 0.3048;
    private static readonly string[] ProtectionRatings = ["G", "PG", "PG13", "R", "X"];

    public static List<ParsedAscent> Parse(TextReader reader)
    {
        var rows = ExportText.ReadRows(reader).ToList();

        // A tick may cover only some of a route's pitches, so a route gets the highest seen.
        var pitchesByRoute = rows
            .GroupBy(RouteId)
            .ToDictionary(g => g.Key, g => g.Max(r => int.TryParse(r["Pitches"], out var pitches) ? pitches : (int?)null));

        var ascents = new List<ParsedAscent>();
        var occurrences = new Dictionary<string, int>();

        foreach (var row in rows)
        {
            var routeId = RouteId(row);
            var path = row["Location"].Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ExportText.Clean)
                .ToList();
            var routeType = row["Route Type"].Trim();
            var style = row["Style"].Trim();
            var leadStyle = row["Lead Style"].Trim();
            var isBoulder = routeType.Contains("Boulder") && style is "Send" or "Flash" or "Attempt";
            var date = DateOnly.ParseExact(row["Date"].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

            ascents.Add(new ParsedAscent
            {
                Source = DataSource.MountainProject,
                AreaPath = [Country, .. path[..^1]],
                CragName = path[^1],
                RouteName = ExportText.Clean(row["Route"]),
                RouteExternalId = routeId,
                RouteUrl = row["URL"].Trim(),
                Grade = ChooseGrade(row["Rating"], isBoulder),
                GradeSystem = ChooseGrade(row["Rating"], isBoulder) switch
                {
                    null => null,
                    var grade when grade.StartsWith('V') => GradeSystem.V,
                    _ => GradeSystem.Yds,
                },
                Discipline = isBoulder ? Discipline.Boulder : MapDiscipline(routeType),
                LengthM = int.TryParse(row["Length"], out var feet) ? (int)Math.Round(feet * MetresPerFoot) : null,
                Pitches = pitchesByRoute[routeId],
                AscentExternalId = ExportText.NextOccurrence(occurrences, $"{routeId}|{date:yyyy-MM-dd}"),
                Date = date,
                DatePrecision = DatePrecision.Day,
                Style = MapStyle(style, leadStyle),
                StyleRaw = leadStyle.Length > 0 ? $"{style} / {leadStyle}" : style,
                Notes = ExportText.CleanOrNull(row["Notes"]),
                Raw = row,
            });
        }

        return ascents;
    }

    private static string RouteId(IReadOnlyDictionary<string, string> row)
    {
        var segments = row["URL"].Trim().Split('/');
        return segments.Length > 4 && segments[3] == "route"
            ? segments[4]
            : throw new FormatException($"Unrecognised Mountain Project route URL '{row["URL"]}'.");
    }

    // A rating can hold both kinds of grade plus protection, such as "5.12a V5 R".
    public static string? ChooseGrade(string rating, bool isBoulder)
    {
        var parts = rating.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var v = parts.FirstOrDefault(p => p.StartsWith('V'));
        var yds = parts.FirstOrDefault(p => p.StartsWith("5.", StringComparison.Ordinal));
        var grade = isBoulder ? v ?? yds : yds ?? v;
        var protection = parts.FirstOrDefault(ProtectionRatings.Contains);
        return grade is null ? null : protection is null ? grade : $"{grade} {protection}";
    }

    public static AscentStyle MapStyle(string style, string leadStyle) => style switch
    {
        "Send" => AscentStyle.Send,
        "Flash" => AscentStyle.Flash,
        "Attempt" => AscentStyle.Dnf,
        "Follow" => AscentStyle.Second,
        "TR" => AscentStyle.TopRope,
        "Solo" => AscentStyle.Solo,
        "Lead" => leadStyle switch
        {
            "Onsight" => AscentStyle.Onsight,
            "Flash" => AscentStyle.Flash,
            "Redpoint" or "Pinkpoint" => AscentStyle.Redpoint,
            "Fell/Hung" => AscentStyle.Hangdog,
            _ => AscentStyle.Lead,
        },
        _ => AscentStyle.Other,
    };

    private static Discipline? MapDiscipline(string routeType) => routeType.Split(',')[0].Trim() switch
    {
        "Boulder" => Discipline.Boulder,
        "Trad" => Discipline.Trad,
        "Sport" => Discipline.Sport,
        "Alpine" => Discipline.Alpine,
        "Ice" or "Mixed" => Discipline.Winter,
        _ => Discipline.Other,
    };
}
