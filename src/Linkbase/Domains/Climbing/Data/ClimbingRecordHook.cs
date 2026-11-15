using System.Text.Json.Nodes;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

// What the climbing module does when records in the generic store change (see docs/models.md): keeps each route's
// grade_rank, names each ascent for its route, and rebuilds the trips when an ascent's date or place changes.
public sealed class ClimbingRecordHook(
    IDbContextFactory<LinkbaseDbContext> contextFactory,
    GenericTripService trips,
    Func<string> correctionsPath,
    ILogger<ClimbingRecordHook> logger) : IRecordHook
{
    public string Domain => "Climbing";

    public IReadOnlyCollection<string> Types { get; } = ["Area", "Crag", "Route", "Ascent"];

    // Relationships that decide where an ascent was, and so which country and trip it belongs to.
    private static readonly HashSet<string> PlaceLinks = ["IN", "AT", "OF"];

    public string? ExtractionNotes =>
        """
        Climbing notes:
        - A route's grade is copied exactly as written, without star ratings or other quality marks.
        - grade_system is the system the grade is written in: uk_trad for a British adjectival grade such as "E2 5c", uk_tech for a British technical grade such as "5c" beside one, french for "6b+", font for "f6A", yds for "5.10a", v for "V4". Leave it out if there is no grade.
        - length_m is the length in metres. Convert only if the text gives the length in feet.
        - An area's kind is country for a nation, region for a large division of a country, and otherwise area. The text seldom says so, so judge it from the place.
        """;

    // Values the generic checks can't judge: ranges, and a grade system that means nothing without its grade.
    public IReadOnlyList<string> CheckExtracted(string type, string name, JsonObject props, string sourceText)
    {
        var warnings = new List<string>();

        void Drop(string key, string why)
        {
            props.Remove(key);
            warnings.Add($"Removed {name}'s {key}: {why}.");
        }

        if (type == "Route")
        {
            if (props["length_m"] is { } length && !(Number(length) is > 0 and <= MaxLengthM))
            {
                Drop("length_m", "it isn't a believable length");
            }

            if (props["pitches"] is { } pitches && !(Number(pitches) is > 0 and <= MaxPitches))
            {
                Drop("pitches", "it isn't a believable number of pitches");
            }

            if (props["grade_system"] is not null && string.IsNullOrWhiteSpace((string?)props["grade"]))
            {
                Drop("grade_system", "there is no grade");
            }
        }
        else if (type == "Crag" && (props["lat"] is not null || props["lon"] is not null))
        {
            var lat = Number(props["lat"]);
            var lon = Number(props["lon"]);
            var valid = lat is >= -90 and <= 90 && lon is >= -180 and <= 180 && NumberInText(lat!.Value, sourceText) && NumberInText(lon!.Value, sourceText);
            if (!valid)
            {
                props.Remove("lat");
                props.Remove("lon");
                warnings.Add($"Removed the coordinates of {name}: they aren't both in the text.");
            }
        }

        return warnings;
    }

    private const double MaxLengthM = 3000;
    private const double MaxPitches = 100;

    private static double? Number(JsonNode? node) =>
        node is JsonValue && double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    // The sign isn't compared, because a text may write -2.5 as 2.5W. The digits must match as a whole number.
    private static bool NumberInText(double value, string text)
    {
        var digits = Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![\d.]){System.Text.RegularExpressions.Regex.Escape(digits)}(?!\d|\.\d)");
    }

    public void BeforeSave(RecordSaving record)
    {
        if (record.Type != "Route")
        {
            return;
        }

        var grade = (string?)record.Value("grade");
        GradeSystem? system = SnakeCaseEnumConverter<GradeSystem>.TryFromStore((string?)record.Value("grade_system"), out var found) ? found : null;
        record.Set("grade_rank", GradeRanking.Rank(system, grade) is { } rank ? JsonValue.Create(rank) : null);
    }

    public async Task AfterChangeAsync(IReadOnlyList<RecordChange> changes, long? changeSetId, CancellationToken cancellationToken)
    {
        if (changes.Any(NamesAscents))
        {
            await NameAscentsAfterTheirRoutesAsync(cancellationToken);
        }

        if (!changes.Any(MovesTrips))
        {
            return;
        }

        var path = correctionsPath();
        if (!File.Exists(path))
        {
            // Without the corrections file a rebuild would dissolve the multi-country trips.
            logger.LogWarning("Trips weren't recalculated because {Path} is missing", path);
            return;
        }

        await trips.RebuildAsync(Corrections.Load(path).TripMerges, changeSetId, cancellationToken);
    }

    // A new or edited ascent, a route that was renamed, or an ascent that now belongs to another route.
    private static bool NamesAscents(RecordChange change) => change switch
    {
        { Type: "Ascent", Kind: RecordChangeKind.Created or RecordChangeKind.Updated } => true,
        { Type: "Ascent", Kind: RecordChangeKind.Linked or RecordChangeKind.Unlinked, RelType: "OF" } => true,
        { Type: "Route", Kind: RecordChangeKind.Created or RecordChangeKind.Updated } => true,
        _ => false,
    };

    // Trips come from each ascent's date and country, so those changing, or an ascent coming or going, moves them. An
    // area renamed changes a country's name, and so the trips' names and keys.
    private static bool MovesTrips(RecordChange change) => change switch
    {
        { Type: "Ascent", Kind: RecordChangeKind.Created or RecordChangeKind.Deleted } => true,
        { Type: "Ascent", Kind: RecordChangeKind.Updated } =>
            Differs(change, "date") || Differs(change, "date_precision"),
        { Kind: RecordChangeKind.Linked or RecordChangeKind.Unlinked, RelType: { } rel } => PlaceLinks.Contains(rel),
        { Type: "Area", Kind: RecordChangeKind.Created or RecordChangeKind.Updated or RecordChangeKind.Deleted } => true,
        _ => false,
    };

    private static bool Differs(RecordChange change, string field) =>
        change.Before.GetValueOrDefault(field) != change.After.GetValueOrDefault(field);

    // An ascent's name is its route's. This leaves updated_at alone, because it isn't an edit to the ascent.
    private async Task NameAscentsAfterTheirRoutesAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE node a SET name = r.name
            FROM node_link l JOIN node r ON r.id = l.target_id
            WHERE a.type = 'Ascent' AND l.source_id = a.id AND l.rel_type = 'OF' AND r.type = 'Route' AND a.name <> r.name
            """, cancellationToken);
    }
}
