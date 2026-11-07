using System.Globalization;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;

namespace Linkbase.Domains.Climbing.Import;

public sealed record ComparisonAscent(
    DataSource Source,
    string CragName,
    string RouteName,
    DateOnly Date,
    DatePrecision Precision,
    string? StyleRaw,
    string? Notes,
    string? Grade,
    int? LengthM,
    IReadOnlyList<string> Partners,
    string? TripName,
    bool TripInferred);

public sealed record ComparisonTrip(string Key, string Name, DateOnly? Start, DateOnly? End, IReadOnlyList<string> Countries);

public sealed record ComparisonResult(
    int AscentsCompared,
    int AscentsIdentical,
    int TripsCompared,
    int TripsIdentical,
    IReadOnlyList<string> Differences);

// Compares the import with the graph row by row. Ascents pair up on source, crag, route, date and
// style, because the graph's own ascent ids are line numbers from the export.
public static class Neo4jComparison
{
    public static ComparisonResult Compare(Neo4jExport export, IReadOnlyList<ComparisonAscent> ascents, IReadOnlyList<ComparisonTrip> trips)
    {
        var differences = new List<string>();

        var graph = export.Ascents.Select(ToComparison).ToLookup(Key);
        var app = ascents.ToLookup(Key);
        var identical = 0;
        foreach (var key in graph.Select(g => g.Key).Union(app.Select(g => g.Key)).Order(StringComparer.Ordinal))
        {
            var left = graph[key].OrderBy(a => a.Notes, StringComparer.Ordinal).ThenBy(Partners, StringComparer.Ordinal).ToList();
            var right = app[key].OrderBy(a => a.Notes, StringComparer.Ordinal).ThenBy(Partners, StringComparer.Ordinal).ToList();
            if (left.Count != right.Count)
            {
                differences.Add($"Ascent {key}: {left.Count} in the graph, {right.Count} in the import");
            }

            foreach (var (g, a) in left.Zip(right))
            {
                var fields = FieldDifferences(g, a).ToList();
                differences.AddRange(fields.Select(f => $"Ascent {key}: {f}"));
                identical += fields.Count == 0 ? 1 : 0;
            }
        }

        var appTrips = trips.ToDictionary(t => t.Key);
        var graphTrips = export.Trips.ToDictionary(t => t.Id);
        var tripsIdentical = 0;
        foreach (var trip in export.Trips)
        {
            if (!appTrips.TryGetValue(trip.Id, out var match))
            {
                differences.Add($"Trip {trip.Id}: only in the graph");
                continue;
            }

            var fields = TripDifferences(trip, match).ToList();
            differences.AddRange(fields.Select(f => $"Trip {trip.Id}: {f}"));
            tripsIdentical += fields.Count == 0 ? 1 : 0;
        }

        differences.AddRange(trips.Where(t => !graphTrips.ContainsKey(t.Key)).Select(t => $"Trip {t.Key}: only in the import"));

        return new ComparisonResult(export.Ascents.Count, identical, export.Trips.Count, tripsIdentical, differences);
    }

    private static ComparisonAscent ToComparison(Neo4jAscent a)
    {
        var isMountainProject = a.Source == "MountainProject";
        var (date, precision) = isMountainProject
            ? (DateOnly.ParseExact(a.DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture), DatePrecision.Day)
            : UkcParser.ParseDate(a.DateText);
        var styleRaw = isMountainProject
            ? (a.MpLeadStyle is null ? a.MpStyle : $"{a.MpStyle} / {a.MpLeadStyle}")
            : a.Style;
        var trip = a.Trips.Count > 0 ? a.Trips[0] : null;

        return new ComparisonAscent(
            isMountainProject ? DataSource.MountainProject : DataSource.Ukc,
            a.CragName,
            a.ClimbName,
            date,
            precision,
            styleRaw,
            a.Notes,
            a.Protection is null ? a.Grade : $"{a.Grade} {a.Protection}",
            a.LengthFt is { } feet ? (int)Math.Round(feet * 0.3048) : null,
            a.Partners,
            trip?.Name,
            trip?.Inferred ?? false);
    }

    private static string Key(ComparisonAscent a)
    {
        // The graph stores UKC's "-" style as nothing at all.
        var style = a.StyleRaw is null or "-" ? "" : a.StyleRaw;
        return $"{a.Source} | {a.CragName} | {a.RouteName} | {a.Date:yyyy-MM-dd} ({a.Precision}) | {style}";
    }

    private static string Partners(ComparisonAscent a) => string.Join(", ", a.Partners.Order(StringComparer.Ordinal));

    private static IEnumerable<string> FieldDifferences(ComparisonAscent graph, ComparisonAscent app)
    {
        if (graph.Notes != app.Notes)
        {
            yield return $"notes differ: graph [{graph.Notes}], import [{app.Notes}]";
        }

        if (graph.Grade != app.Grade)
        {
            yield return $"grade differs: graph [{graph.Grade}], import [{app.Grade}]";
        }

        if (graph.LengthM != app.LengthM)
        {
            yield return $"length differs: graph [{graph.LengthM}], import [{app.LengthM}]";
        }

        if (Partners(graph) != Partners(app))
        {
            yield return $"partners differ: graph [{Partners(graph)}], import [{Partners(app)}]";
        }

        if (graph.TripName != app.TripName || graph.TripInferred != app.TripInferred)
        {
            yield return $"trip differs: graph [{graph.TripName}, inferred {graph.TripInferred}], import [{app.TripName}, inferred {app.TripInferred}]";
        }
    }

    private static IEnumerable<string> TripDifferences(Neo4jTrip graph, ComparisonTrip app)
    {
        if (graph.Name != app.Name)
        {
            yield return $"name differs: graph [{graph.Name}], import [{app.Name}]";
        }

        if (graph.Start != app.Start?.ToString("yyyy-MM-dd") || graph.End != app.End?.ToString("yyyy-MM-dd"))
        {
            yield return $"dates differ: graph [{graph.Start} to {graph.End}], import [{app.Start:yyyy-MM-dd} to {app.End:yyyy-MM-dd}]";
        }

        var visited = string.Join(", ", graph.Visited.Order(StringComparer.Ordinal));
        var countries = string.Join(", ", app.Countries.Order(StringComparer.Ordinal));
        if (visited.Length > 0 && visited != countries)
        {
            yield return $"countries differ: graph VISITED [{visited}], import ascents [{countries}]";
        }
    }
}
