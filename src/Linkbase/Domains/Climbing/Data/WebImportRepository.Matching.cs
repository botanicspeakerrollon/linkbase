using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;
public sealed partial class WebImportRepository
{
    private static async Task MatchCragAsync(
        LinkbaseDbContext db,
        StagedCrag crag,
        string refBase,
        Dictionary<(string NodeType, string ExternalId), string> refs,
        CancellationToken cancellationToken)
    {
        var name = crag.Name;
        var found = await db.Nodes.AsNoTracking()
            .Where(n => n.Type == "Crag" && EF.Functions.TrigramsSimilarity(n.Name, name) >= SimilarityThreshold)
            .OrderByDescending(n => EF.Functions.TrigramsSimilarity(n.Name, name))
            .Take(MaxCandidates * 2)
            .Select(n => new { n.Id, n.Name, Similarity = EF.Functions.TrigramsSimilarity(n.Name, name) })
            .ToListAsync(cancellationToken);

        if (refs.TryGetValue(("Crag", CragKey(refBase, name)), out var refId))
        {
            var known = await db.Nodes.AsNoTracking().Where(n => n.Id == refId && n.Type == "Crag").Select(n => new { n.Id, n.Name }).SingleOrDefaultAsync(cancellationToken);
            if (known is not null)
            {
                Apply(crag, [new MatchCandidate(known.Id, known.Name, await AreaNameOfAsync(db, known.Id, cancellationToken), 1)]);
                return;
            }
        }

        var areaNames = await db.NodeLinks.AsNoTracking()
            .Where(l => l.RelType == "IN" && found.Select(f => f.Id).Contains(l.SourceId))
            .Select(l => new { l.SourceId, AreaName = l.Target.Name })
            .ToListAsync(cancellationToken);
        Classify(crag, name, found
            .Select(f => new MatchCandidate(f.Id, f.Name, areaNames.FirstOrDefault(a => a.SourceId == f.Id)?.AreaName, f.Similarity))
            .ToList());
    }

    private static Task<string?> AreaNameOfAsync(LinkbaseDbContext db, string cragId, CancellationToken cancellationToken) =>
        db.NodeLinks.AsNoTracking().Where(l => l.RelType == "IN" && l.SourceId == cragId).Select(l => l.Target.Name).FirstOrDefaultAsync(cancellationToken);

    private static async Task MatchRoutesAsync(
        LinkbaseDbContext db,
        StagedCrag crag,
        string refBase,
        Dictionary<(string NodeType, string ExternalId), string> refs,
        CancellationToken cancellationToken)
    {
        foreach (var route in crag.Routes)
        {
            // Only a chosen existing crag has routes to match against.
            if (!crag.UsesExisting)
            {
                route.Match = StagedMatch.New;
                route.Candidates = [];
                route.Choice = StagedRow.CreateNew;
                continue;
            }

            var cragId = crag.Choice;
            var name = route.Name;
            if (refs.TryGetValue(("Route", RouteKey(refBase, crag.Name, name)), out var refId))
            {
                var known = await db.NodeLinks.AsNoTracking()
                    .Where(l => l.RelType == "AT" && l.TargetId == cragId && l.SourceId == refId)
                    .Select(l => new { l.Source.Id, l.Source.Name, l.Source.Props })
                    .SingleOrDefaultAsync(cancellationToken);
                if (known is not null)
                {
                    Apply(route, [new MatchCandidate(known.Id, known.Name, Grade(known.Props), 1)]);
                    continue;
                }
            }

            var candidates = (await db.NodeLinks.AsNoTracking()
                    .Where(l => l.RelType == "AT" && l.TargetId == cragId && EF.Functions.TrigramsSimilarity(l.Source.Name, name) >= SimilarityThreshold)
                    .OrderByDescending(l => EF.Functions.TrigramsSimilarity(l.Source.Name, name))
                    .Take(MaxCandidates * 2)
                    .Select(l => new { l.Source.Id, l.Source.Name, l.Source.Props, Similarity = EF.Functions.TrigramsSimilarity(l.Source.Name, name) })
                    .ToListAsync(cancellationToken))
                .Select(r => new MatchCandidate(r.Id, r.Name, Grade(r.Props), r.Similarity))
                .ToList();
            Classify(route, name, candidates);
        }
    }

    private static string? Grade(string props) => (JsonNode.Parse(props) as JsonObject)?["grade"]?.GetValue<string>();

    // One identical name is a match, several are ambiguous, and a similar name needs a decision. None is new.
    private static void Classify(StagedRow row, string name, List<MatchCandidate> found)
    {
        var normalised = NameMatching.Normalise(name);
        var exact = found.Where(c => NameMatching.Normalise(c.Name) == normalised).ToList();
        if (exact.Count == 1)
        {
            Apply(row, exact);
            return;
        }

        var similar = exact.Count > 1
            ? exact
            : found.Where(c => !NameMatching.OnlyNumberingDiffers(name, c.Name)).ToList();
        if (similar.Count == 0)
        {
            row.Match = StagedMatch.New;
            row.Candidates = [];
            row.Choice = StagedRow.CreateNew;
            return;
        }

        row.Match = StagedMatch.Ambiguous;
        row.Candidates = similar.Take(MaxCandidates).ToList();
        row.Choice = StagedRow.Undecided;
    }

    private static void Apply(StagedRow row, List<MatchCandidate> match)
    {
        row.Match = StagedMatch.Existing;
        row.Candidates = match;
        row.Choice = match[0].Id;
    }

    private sealed record AreaRow(string Id, string? ParentId, string Name, AreaKind Kind);

    private static async Task<Dictionary<string, AreaRow>> LoadAreasAsync(LinkbaseDbContext db, CancellationToken cancellationToken)
    {
        var parents = await db.NodeLinks.AsNoTracking()
            .Where(l => l.RelType == "IN" && l.Source.Type == "Area")
            .Select(l => new { l.SourceId, l.TargetId })
            .ToDictionaryAsync(l => l.SourceId, l => l.TargetId, cancellationToken);
        var nodes = await db.Nodes.AsNoTracking().Where(n => n.Type == "Area").Select(n => new { n.Id, n.Name, n.Props }).ToListAsync(cancellationToken);
        return nodes.ToDictionary(
            n => n.Id,
            n => new AreaRow(
                n.Id,
                parents.GetValueOrDefault(n.Id),
                n.Name,
                SnakeCaseEnumConverter<AreaKind>.TryFromStore((JsonNode.Parse(n.Props) as JsonObject)?["kind"]?.GetValue<string>(), out var kind) ? kind : AreaKind.Area));
    }

    private static List<string> PathTo(Dictionary<string, AreaRow> areas, string areaId)
    {
        var path = new List<string>();
        for (var current = areaId; current is not null && areas.TryGetValue(current, out var area) && path.Count < 20; current = area.ParentId)
        {
            path.Insert(0, area.Name);
        }

        return path;
    }

    // Proposes where a new crag goes, from what the text said. A name it can't place is left for the user.
    private static void ProposeArea(StagedCrag crag, List<AreaRow> areas, IReadOnlyList<ExtractedArea> extractedAreas)
    {
        if (crag.ExtractedArea is not { } areaName)
        {
            return;
        }

        var found = FindArea(areas, areaName);
        if (found is not null)
        {
            crag.AreaId = found.Id;
            crag.AreaLabel = found.Name;
            return;
        }

        var extracted = extractedAreas.FirstOrDefault(a => NameMatching.Normalise(a.Name) == NameMatching.Normalise(areaName));
        crag.NewAreaName = areaName;
        crag.NewAreaKind = extracted?.Kind == "country" ? AreaKind.Country : null;
        if (extracted?.Parent is { } parent && FindArea(areas, parent) is { } parentArea)
        {
            crag.NewAreaParentId = parentArea.Id;
            crag.AreaLabel = parentArea.Name;
        }
    }

    private static AreaRow? FindArea(List<AreaRow> areas, string name)
    {
        var matches = areas.Where(a => NameMatching.Normalise(a.Name) == NameMatching.Normalise(name)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
