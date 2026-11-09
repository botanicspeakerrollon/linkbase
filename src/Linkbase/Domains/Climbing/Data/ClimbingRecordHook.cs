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

    public async Task AfterChangeAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken)
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

        await trips.RebuildAsync(Corrections.Load(path).TripMerges, cancellationToken);
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
