using System.Text.Json;
using System.Text.RegularExpressions;
using Linkbase.Core.Extraction;

namespace Linkbase.Domains.Climbing.Import.Web;

// What the model returns, as named by the tool's JSON schema. Null means the text didn't say.
public sealed record ExtractedArea(string Name, string? Parent, string? Kind);

public sealed record ExtractedCrag(string Name, string? Area, string? RockType, double? Lat, double? Lon);

public sealed record ExtractedRoute(
    string Name,
    string Crag,
    string? Grade,
    string? GradeSystem,
    string? Discipline,
    int? LengthM,
    int? Pitches,
    string? Summary);

public sealed record ExtractedPage(
    IReadOnlyList<ExtractedArea> Areas,
    IReadOnlyList<ExtractedCrag> Crags,
    IReadOnlyList<ExtractedRoute> Routes)
{
    public static readonly ExtractedPage Empty = new([], [], []);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}

// What's kept in import_batch.raw_payload. The page's own text is deliberately not stored.
public sealed record ImportExtraction(string? Title, ExtractedPage Page, IReadOnlyList<string> Warnings);

public interface IClimbingExtractor
{
    Task<ExtractedPage> ExtractAsync(string text, IReadOnlyList<string> knownCrags, CancellationToken cancellationToken);
}

// Long pages come back in parts. The same crag or route can appear in several, so merge on the
// normalised name, keeping the first value of each field and filling gaps from later parts.
public static class ExtractionMerger
{
    public static ExtractedPage Merge(IEnumerable<ExtractedPage> pages)
    {
        var areas = new Dictionary<string, ExtractedArea>();
        var crags = new Dictionary<string, ExtractedCrag>();
        var routes = new Dictionary<string, ExtractedRoute>();

        foreach (var page in pages)
        {
            foreach (var area in page.Areas)
            {
                var key = NameMatching.Normalise(area.Name);
                areas[key] = areas.TryGetValue(key, out var existing)
                    ? existing with { Parent = existing.Parent ?? area.Parent, Kind = existing.Kind ?? area.Kind }
                    : area;
            }

            foreach (var crag in page.Crags)
            {
                var key = NameMatching.Normalise(crag.Name);
                crags[key] = crags.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        Area = existing.Area ?? crag.Area,
                        RockType = existing.RockType ?? crag.RockType,
                        Lat = existing.Lat ?? crag.Lat,
                        Lon = existing.Lon ?? crag.Lon,
                    }
                    : crag;
            }

            foreach (var route in page.Routes)
            {
                var key = $"{NameMatching.Normalise(route.Crag)}|{NameMatching.Normalise(route.Name)}";
                routes[key] = routes.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        Grade = existing.Grade ?? route.Grade,
                        GradeSystem = existing.GradeSystem ?? route.GradeSystem,
                        Discipline = existing.Discipline ?? route.Discipline,
                        LengthM = existing.LengthM ?? route.LengthM,
                        Pitches = existing.Pitches ?? route.Pitches,
                        Summary = existing.Summary ?? route.Summary,
                    }
                    : route;
            }
        }

        return new ExtractedPage(areas.Values.ToList(), crags.Values.ToList(), routes.Values.ToList());
    }
}

// Checks the model's answer against the source text before anything is staged. A name or grade the text
// doesn't contain was invented, and a summary the text does contain was copied, so neither is kept.
public static class ExtractionValidator
{
    public const int MaxSummaryLength = 200;
    private const int MaxLengthM = 3000;
    private const int MaxPitches = 100;

    public static (ExtractedPage Page, List<string> Warnings) Validate(ExtractedPage page, string sourceText)
    {
        var source = NameMatching.Normalise(sourceText);
        var warnings = new List<string>();
        bool InSource(string? value) => !string.IsNullOrWhiteSpace(value) && source.Contains(NameMatching.Normalise(value));

        var areas = new List<ExtractedArea>();
        foreach (var area in page.Areas)
        {
            if (InSource(area.Name))
            {
                areas.Add(area with { Name = area.Name.Trim(), Kind = area.Kind is "country" or "region" or "area" ? area.Kind : null });
            }
            else
            {
                warnings.Add($"Dropped area '{area.Name}': it isn't in the text.");
            }
        }

        var crags = new Dictionary<string, ExtractedCrag>();
        foreach (var crag in page.Crags)
        {
            if (!InSource(crag.Name))
            {
                warnings.Add($"Dropped crag '{crag.Name}': it isn't in the text.");
                continue;
            }

            var (lat, lon) = (crag.Lat, crag.Lon);
            if (lat is not null || lon is not null)
            {
                var valid = lat is >= -90 and <= 90 && lon is >= -180 and <= 180 && NumberInText(lat!.Value, sourceText) && NumberInText(lon!.Value, sourceText);
                if (!valid)
                {
                    warnings.Add($"Removed the coordinates of '{crag.Name}': they aren't in the text.");
                    (lat, lon) = (null, null);
                }
            }

            crags[NameMatching.Normalise(crag.Name)] = crag with
            {
                Name = crag.Name.Trim(),
                Area = InSource(crag.Area) ? crag.Area!.Trim() : null,
                RockType = InSource(crag.RockType) ? crag.RockType!.Trim() : null,
                Lat = lat,
                Lon = lon,
            };
        }

        var routes = new List<ExtractedRoute>();
        foreach (var route in page.Routes)
        {
            if (!InSource(route.Name))
            {
                warnings.Add($"Dropped route '{route.Name}': it isn't in the text.");
                continue;
            }

            if (!InSource(route.Crag))
            {
                warnings.Add($"Dropped route '{route.Name}': its crag '{route.Crag}' isn't in the text.");
                continue;
            }

            // A route can name a crag the model didn't list separately.
            var cragKey = NameMatching.Normalise(route.Crag);
            if (!crags.ContainsKey(cragKey))
            {
                crags[cragKey] = new ExtractedCrag(route.Crag.Trim(), null, null, null, null);
            }

            var grade = route.Grade?.Trim();
            if (!string.IsNullOrEmpty(grade) && !InSource(grade))
            {
                warnings.Add($"Removed the grade '{grade}' from '{route.Name}': it isn't in the text.");
                grade = null;
            }

            routes.Add(route with
            {
                Name = route.Name.Trim(),
                Crag = crags[cragKey].Name,
                Grade = string.IsNullOrEmpty(grade) ? null : grade,
                GradeSystem = grade is null ? null : route.GradeSystem,
                LengthM = route.LengthM is > 0 and <= MaxLengthM ? route.LengthM : null,
                Pitches = route.Pitches is > 0 and <= MaxPitches ? route.Pitches : null,
                Summary = CheckSummary(route, source, warnings),
            });
        }

        return (new ExtractedPage(areas, crags.Values.ToList(), routes), warnings);
    }

    private static string? CheckSummary(ExtractedRoute route, string source, List<string> warnings)
    {
        var summary = route.Summary?.Trim();
        if (string.IsNullOrEmpty(summary))
        {
            return null;
        }

        if (summary.Length > MaxSummaryLength)
        {
            warnings.Add($"Removed the summary of '{route.Name}': it's too long to be a brief summary.");
            return null;
        }

        if (source.Contains(NameMatching.Normalise(summary)))
        {
            warnings.Add($"Removed the summary of '{route.Name}': it was copied from the text.");
            return null;
        }

        return summary;
    }

    // The sign isn't compared, because a text may write -2.5 as 2.5W. The digits must match as a whole number.
    private static bool NumberInText(double value, string text)
    {
        var digits = Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Regex.IsMatch(text, $@"(?<![\d.]){Regex.Escape(digits)}(?!\d|\.\d)");
    }
}
