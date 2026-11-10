using System.Globalization;
using System.Net;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;

namespace Linkbase.Domains.Climbing.Import;

// One row of an export, in the shape the importer understands regardless of source.
public sealed record ParsedAscent
{
    public required DataSource Source { get; init; }
    public required IReadOnlyList<string> AreaPath { get; init; }
    public required string CragName { get; init; }
    public required string RouteName { get; init; }
    public required string RouteExternalId { get; init; }
    public string? RouteUrl { get; init; }
    public string? Grade { get; init; }
    public GradeSystem? GradeSystem { get; init; }
    public Discipline? Discipline { get; init; }
    public int? LengthM { get; init; }
    public int? Pitches { get; init; }
    public required string AscentExternalId { get; init; }
    public required DateOnly Date { get; init; }
    public required DatePrecision DatePrecision { get; init; }
    public required AscentStyle Style { get; init; }
    public string? StyleRaw { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> Partners { get; init; } = [];
    public IReadOnlyDictionary<string, string> Raw { get; init; } = new Dictionary<string, string>();
}

internal static class ExportText
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture) { MissingFieldFound = null };

    public static IEnumerable<IReadOnlyDictionary<string, string>> ReadRows(TextReader reader)
    {
        using var csv = new CsvReader(reader, CsvConfig);
        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? throw new FormatException("The file has no header row.");
        while (csv.Read())
        {
            yield return headers.ToDictionary(h => h, h => csv.GetField(h) ?? "");
        }
    }

    // Exports contain HTML entities such as &beta; and &#39;.
    public static string Clean(string? value) => WebUtility.HtmlDecode(value ?? "").Trim();

    public static string? CleanOrNull(string? value)
    {
        var cleaned = Clean(value);
        return cleaned.Length == 0 ? null : cleaned;
    }

    // Stable text for external_ref, unaffected by case or stray whitespace.
    public static string ExternalKey(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static string NextOccurrence(Dictionary<string, int> occurrences, string key)
    {
        var occurrence = occurrences.GetValueOrDefault(key) + 1;
        occurrences[key] = occurrence;
        return $"{key}|{occurrence}";
    }
}
