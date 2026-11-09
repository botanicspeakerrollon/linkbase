using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public enum StagedMatch { New, Existing, Ambiguous }

// Ids are node ids, such as crag:12.
public sealed record MatchCandidate(string Id, string Name, string? Detail, double Similarity);

// An area to put a new crag in, with the areas above it.
public sealed record AreaChoice(string Id, string Name, string Path);

// Editable rows on the staging screen. Choice says what committing does with a row.
public abstract class StagedRow
{
    public const string Undecided = "?";
    public const string CreateNew = "";

    public bool Include { get; set; } = true;
    public StagedMatch Match { get; set; }
    public List<MatchCandidate> Candidates { get; set; } = [];

    // Undecided, CreateNew, or the node id of the existing row to use.
    public string Choice { get; set; } = CreateNew;

    public bool UsesExisting => Choice is { Length: > 0 } && Choice != Undecided;
}

public sealed class StagedCrag : StagedRow
{
    public required string Name { get; set; }
    public string? RockType { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string? ExtractedArea { get; set; }

    // Where a new crag goes: an existing area, or a new one with this name under NewAreaParentId.
    public string? AreaId { get; set; }
    public string? AreaLabel { get; set; }
    public string? NewAreaName { get; set; }
    public string? NewAreaParentId { get; set; }
    public AreaKind? NewAreaKind { get; set; }

    public List<StagedRoute> Routes { get; set; } = [];
}

// A route's summary is shown in the extraction but not kept: the model has no description field, because no route had one.
public sealed class StagedRoute : StagedRow
{
    public required string Name { get; set; }
    public string? Grade { get; set; }
    public GradeSystem? GradeSystem { get; set; }
    public Discipline? Discipline { get; set; }
    public int? LengthM { get; set; }
    public int? Pitches { get; set; }
}

public sealed record StagedImport(
    long BatchId,
    ImportStatus Status,
    string? SourceUrl,
    string? Title,
    IReadOnlyList<string> Warnings,
    string? Error,
    List<StagedCrag> Crags);

public sealed record ImportBatchSummary(
    long Id,
    string? SourceUrl,
    string? Title,
    ImportStatus Status,
    DateTimeOffset CreatedAt,
    int Crags,
    int Routes,
    string? Error);

public sealed record CommitSummary(int AreasCreated, int CragsCreated, int CragsMatched, int RoutesCreated, int RoutesMatched);

public sealed record UndoSummary(int RoutesDeleted, int CragsDeleted, int AreasDeleted, IReadOnlyList<string> Skipped);

// The staged web import over the generic store: match extracted rows against the Area, Crag and Route records, write the
// confirmed ones in one transaction, tagged with the batch, and undo a batch later. See docs/history/climbing-to-generic-plan.md.
public sealed class WebImportRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    private const double SimilarityThreshold = 0.5;
    private const int MaxCandidates = 5;

    private sealed record StoredPayload(ImportExtraction? Extraction, string? Error);

    public async Task<long> CreateBatchAsync(string? sourceUrl, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = new ImportBatch
        {
            UserId = AppUser.DefaultId,
            Source = DataSource.WebImport,
            SourceUrl = sourceUrl,
            Status = ImportStatus.Pending,
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        return batch.Id;
    }

    public Task StageAsync(long batchId, ImportExtraction extraction, CancellationToken cancellationToken = default) =>
        UpdateBatchAsync(batchId, ImportStatus.Staged, new StoredPayload(extraction, null), cancellationToken);

    public Task MarkFailedAsync(long batchId, string error, CancellationToken cancellationToken = default) =>
        UpdateBatchAsync(batchId, ImportStatus.Failed, new StoredPayload(null, error), cancellationToken);

    private async Task UpdateBatchAsync(long batchId, ImportStatus status, StoredPayload payload, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleAsync(b => b.Id == batchId, cancellationToken);
        batch.Status = status;
        batch.RawPayload = JsonSerializer.Serialize(payload, ExtractedPage.JsonOptions);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImportBatchSummary>> RecentBatchesAsync(int limit = 30, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ImportBatches.AsNoTracking()
            .Where(b => b.UserId == AppUser.DefaultId && b.Source == DataSource.WebImport)
            .OrderByDescending(b => b.Id)
            .Take(limit)
            .Select(b => new { b.Id, b.SourceUrl, b.Status, b.CreatedAt, b.RawPayload })
            .ToListAsync(cancellationToken);

        return rows.Select(b =>
        {
            var payload = Parse(b.RawPayload);
            var extraction = payload?.Extraction;
            return new ImportBatchSummary(
                b.Id,
                b.SourceUrl,
                extraction?.Title,
                b.Status,
                b.CreatedAt,
                extraction?.Page.Crags.Count ?? 0,
                extraction?.Page.Routes.Count ?? 0,
                payload?.Error);
        }).ToList();
    }

    public async Task<StagedImport?> LoadStagedAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.AsNoTracking()
            .Where(b => b.Id == batchId && b.Source == DataSource.WebImport)
            .Select(b => new { b.Id, b.Status, b.SourceUrl, b.RawPayload })
            .SingleOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var payload = Parse(batch.RawPayload);
        var extraction = payload?.Extraction;
        var crags = new List<StagedCrag>();
        if (extraction is not null)
        {
            var refBase = RefBase(batch.SourceUrl, batch.Id);
            var refs = await LoadRefsAsync(db, cancellationToken);
            var areas = await LoadAreasAsync(db, cancellationToken);
            var kinds = extraction.Page.Areas;

            foreach (var extracted in extraction.Page.Crags)
            {
                var crag = new StagedCrag
                {
                    Name = extracted.Name,
                    RockType = extracted.RockType,
                    Lat = extracted.Lat,
                    Lon = extracted.Lon,
                    ExtractedArea = extracted.Area,
                };
                await MatchCragAsync(db, crag, refBase, refs, cancellationToken);
                ProposeArea(crag, areas.Values.ToList(), kinds);

                crag.Routes = extraction.Page.Routes
                    .Where(r => NameMatching.Normalise(r.Crag) == NameMatching.Normalise(extracted.Name))
                    .Select(r => new StagedRoute
                    {
                        Name = r.Name,
                        Grade = r.Grade,
                        GradeSystem = ParseEnum<GradeSystem>(r.GradeSystem),
                        Discipline = ParseEnum<Discipline>(r.Discipline),
                        LengthM = r.LengthM,
                        Pitches = r.Pitches,
                    })
                    .ToList();
                await MatchRoutesAsync(db, crag, refBase, refs, cancellationToken);
                crags.Add(crag);
            }
        }

        return new StagedImport(batch.Id, batch.Status, batch.SourceUrl, extraction?.Title, extraction?.Warnings ?? [], payload?.Error, crags);
    }

    // After choosing a different crag on the staging screen, its routes need matching against that crag.
    public async Task RematchRoutesAsync(long batchId, string? sourceUrl, StagedCrag crag, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await MatchRoutesAsync(db, crag, RefBase(sourceUrl, batchId), await LoadRefsAsync(db, cancellationToken), cancellationToken);
    }

    // Areas whose name contains the text, with the areas above each, for choosing where a new crag goes.
    public async Task<IReadOnlyList<AreaChoice>> SearchAreasAsync(string? text, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var areas = await LoadAreasAsync(db, cancellationToken);
        return areas.Values
            .Where(a => string.IsNullOrWhiteSpace(text) || a.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(a => new AreaChoice(a.Id, a.Name, string.Join(" > ", PathTo(areas, a.Id))))
            .OrderBy(a => a.Path.Length - a.Name.Length)
            .ThenBy(a => a.Name)
            .Take(20)
            .ToList();
    }

    public async Task<CommitSummary> CommitAsync(long batchId, IReadOnlyList<StagedCrag> crags, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == batchId, cancellationToken)
            ?? throw new LinkbaseValidationException("This import no longer exists.");
        if (batch.Status != ImportStatus.Staged)
        {
            throw new LinkbaseValidationException("This import has already been committed or undone.");
        }

        var included = crags.Where(c => c.Include).ToList();
        Validate(included);

        var (areasCreated, cragsCreated, cragsMatched, routesCreated, routesMatched) = (0, 0, 0, 0, 0);
        var refBase = RefBase(batch.SourceUrl, batchId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var areas = await LoadAreasAsync(db, cancellationToken);
        await CheckChosenRecordsExistAsync(db, included, areas, cancellationToken);
        var knownRefs = (await db.ExternalRefs.AsNoTracking()
                .Where(r => r.Source == DataSource.WebImport)
                .Select(r => new { r.NodeType, r.ExternalId })
                .ToListAsync(cancellationToken))
            .Select(r => (r.NodeType, r.ExternalId))
            .ToHashSet();
        var nextKey = new Dictionary<string, long>();
        foreach (var type in new[] { "Area", "Crag", "Route" })
        {
            var keys = await db.Nodes.AsNoTracking().Where(n => n.Type == type).Select(n => n.Key).ToListAsync(cancellationToken);
            nextKey[type] = keys.Select(k => long.TryParse(k, CultureInfo.InvariantCulture, out var number) ? number : 0).DefaultIfEmpty(0).Max();
        }

        var links = new List<(string Source, string SourceType, string Rel, string Target, string TargetType)>();

        string NewNode(string type, string name, JsonObject props)
        {
            var key = (++nextKey[type]).ToString(CultureInfo.InvariantCulture);
            var node = new Node { Id = $"{type.ToLowerInvariant()}:{key}", UserId = AppUser.DefaultId, Type = type, Key = key, Name = name, Props = props.ToJsonString(), ImportBatchId = batchId };
            db.Nodes.Add(node);
            return node.Id;
        }

        string ResolveArea(StagedCrag staged)
        {
            if (staged.AreaId is { } areaId)
            {
                return areaId;
            }

            var name = staged.NewAreaName!.Trim();
            var parentId = staged.NewAreaParentId;
            var existing = areas.Values.FirstOrDefault(a => a.ParentId == parentId && a.Name == name);
            if (existing is not null)
            {
                return existing.Id;
            }

            var kind = parentId is null ? AreaKind.Country : areas[parentId].Kind == AreaKind.Country ? AreaKind.Region : AreaKind.Area;
            var id = NewNode("Area", name, new JsonObject { ["kind"] = SnakeCaseEnumConverter<AreaKind>.ToStore(kind) });
            if (parentId is not null)
            {
                links.Add((id, "Area", "IN", parentId, "Area"));
            }

            areas[id] = new AreaRow(id, parentId, name, kind);
            areasCreated++;
            return id;
        }

        foreach (var staged in included)
        {
            string cragId;
            if (staged.UsesExisting)
            {
                cragId = staged.Choice;
                cragsMatched++;
            }
            else
            {
                var areaId = ResolveArea(staged);
                var props = new JsonObject();
                SetText(props, "rock_type", staged.RockType);
                if (staged.Lat is { } lat)
                {
                    props["lat"] = lat;
                }

                if (staged.Lon is { } lon)
                {
                    props["lon"] = lon;
                }

                cragId = NewNode("Crag", staged.Name.Trim(), props);
                links.Add((cragId, "Crag", "IN", areaId, "Area"));
                cragsCreated++;
            }

            AddRef(db, knownRefs, cragId, "Crag", CragKey(refBase, staged.Name), batch.SourceUrl, batchId);

            foreach (var stagedRoute in staged.Routes.Where(r => r.Include))
            {
                string routeId;
                if (stagedRoute.UsesExisting)
                {
                    routeId = stagedRoute.Choice;
                    routesMatched++;
                }
                else
                {
                    var props = new JsonObject();
                    SetText(props, "grade", stagedRoute.Grade);
                    if (stagedRoute.GradeSystem is { } system)
                    {
                        props["grade_system"] = SnakeCaseEnumConverter<GradeSystem>.ToStore(system);
                    }

                    if (stagedRoute.Discipline is { } discipline)
                    {
                        props["discipline"] = SnakeCaseEnumConverter<Discipline>.ToStore(discipline);
                    }

                    if (stagedRoute.LengthM is { } length)
                    {
                        props["length_m"] = length;
                    }

                    if (stagedRoute.Pitches is { } pitches)
                    {
                        props["pitches"] = pitches;
                    }

                    if (GradeRanking.Rank(stagedRoute.GradeSystem, Clean(stagedRoute.Grade)) is { } rank)
                    {
                        props["grade_rank"] = rank;
                    }

                    routeId = NewNode("Route", stagedRoute.Name.Trim(), props);
                    links.Add((routeId, "Route", "AT", cragId, "Crag"));
                    routesCreated++;
                }

                AddRef(db, knownRefs, routeId, "Route", RouteKey(refBase, staged.Name, stagedRoute.Name), batch.SourceUrl, batchId);
            }
        }

        // Nodes first, so the links' ends exist.
        await db.SaveChangesAsync(cancellationToken);
        foreach (var link in links)
        {
            db.NodeLinks.Add(new NodeLink { SourceId = link.Source, RelType = link.Rel, TargetId = link.Target, ImportBatchId = batchId });
        }

        await db.SaveChangesAsync(cancellationToken);

        // Each relationship's count is kept with its definition.
        foreach (var group in links.GroupBy(l => (l.SourceType, l.Rel, l.TargetType)))
        {
            var added = (long)group.Count();
            await db.LinkDefinitions
                .Where(d => d.SourceType == group.Key.SourceType && d.RelType == group.Key.Rel && d.TargetType == group.Key.TargetType)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links + added), cancellationToken);
        }

        batch.Status = ImportStatus.Committed;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CommitSummary(areasCreated, cragsCreated, cragsMatched, routesCreated, routesMatched);
    }

    // Deletes what this batch created, except rows edited since or still in use. Those are reported and kept.
    public async Task<UndoSummary> UndoAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == batchId, cancellationToken)
            ?? throw new LinkbaseValidationException("This import no longer exists.");
        if (batch.Status != ImportStatus.Committed)
        {
            throw new LinkbaseValidationException("Only a committed import can be undone.");
        }

        var skipped = new List<string>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var routes = await db.Nodes.Where(n => n.Type == "Route" && n.ImportBatchId == batchId)
            .Select(n => new
            {
                n.Id,
                n.Name,
                Edited = n.UpdatedAt != n.CreatedAt,
                Ascents = db.NodeLinks.Count(l => l.RelType == "OF" && l.TargetId == n.Id),
            })
            .ToListAsync(cancellationToken);
        var routesDeleted = 0;
        foreach (var route in routes)
        {
            if (route.Ascents > 0)
            {
                skipped.Add($"Route '{route.Name}': {Plural(route.Ascents, "ascent")} logged on it.");
            }
            else if (route.Edited)
            {
                skipped.Add($"Route '{route.Name}': edited since the import.");
            }
            else
            {
                await DeleteNodeAsync(db, route.Id, cancellationToken);
                routesDeleted++;
            }
        }

        var crags = await db.Nodes.Where(n => n.Type == "Crag" && n.ImportBatchId == batchId)
            .Select(n => new
            {
                n.Id,
                n.Name,
                Edited = n.UpdatedAt != n.CreatedAt,
                Routes = db.NodeLinks.Count(l => l.RelType == "AT" && l.TargetId == n.Id),
            })
            .ToListAsync(cancellationToken);
        var cragsDeleted = 0;
        foreach (var crag in crags)
        {
            if (crag.Routes > 0)
            {
                skipped.Add($"Crag '{crag.Name}': {Plural(crag.Routes, "route")} still on it.");
            }
            else if (crag.Edited)
            {
                skipped.Add($"Crag '{crag.Name}': edited since the import.");
            }
            else
            {
                await DeleteNodeAsync(db, crag.Id, cancellationToken);
                cragsDeleted++;
            }
        }

        var areasDeleted = await DeleteUnusedAreasAsync(db, batchId, skipped, cancellationToken);

        // Refs this batch added to rows that were already there.
        var kept = db.Nodes.Where(n => n.ImportBatchId == batchId).Select(n => n.Id);
        await db.ExternalRefs
            .Where(r => r.ImportBatchId == batchId && !(r.NodeId != null && kept.Contains(r.NodeId)))
            .ExecuteDeleteAsync(cancellationToken);

        // With rows kept, it stays committed so it can be undone again once they're free.
        if (skipped.Count == 0)
        {
            batch.Status = ImportStatus.Undone;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new UndoSummary(routesDeleted, cragsDeleted, areasDeleted, skipped);
    }

    // The node, its links and its external ids. Each relationship's count goes down with its links.
    private static async Task DeleteNodeAsync(LinkbaseDbContext db, string id, CancellationToken cancellationToken)
    {
        var groups = await db.NodeLinks.Where(l => l.SourceId == id || l.TargetId == id)
            .GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
            .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var group in groups)
        {
            var removed = (long)group.Count;
            await db.LinkDefinitions
                .Where(d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links - removed), cancellationToken);
        }

        await db.NodeLinks.Where(l => l.SourceId == id || l.TargetId == id).ExecuteDeleteAsync(cancellationToken);
        await ExternalRefRepository.RemoveForNodesAsync(db, db.Nodes.Where(n => n.Id == id).Select(n => n.Id), cancellationToken);
        await db.Nodes.Where(n => n.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<int> DeleteUnusedAreasAsync(LinkbaseDbContext db, long batchId, List<string> skipped, CancellationToken cancellationToken)
    {
        var areas = await db.Nodes.Where(n => n.Type == "Area" && n.ImportBatchId == batchId)
            .Select(n => new { n.Id, n.Name, Edited = n.UpdatedAt != n.CreatedAt })
            .ToListAsync(cancellationToken);
        var deleted = 0;
        foreach (var area in areas)
        {
            var inUse = await db.NodeLinks.AnyAsync(l => l.RelType == "IN" && l.TargetId == area.Id, cancellationToken);
            if (inUse)
            {
                skipped.Add($"Area '{area.Name}': still has crags or sub-areas.");
            }
            else if (area.Edited)
            {
                skipped.Add($"Area '{area.Name}': edited since the import.");
            }
            else
            {
                await DeleteNodeAsync(db, area.Id, cancellationToken);
                deleted++;
            }
        }

        return deleted;
    }

    private static void Validate(IReadOnlyList<StagedCrag> crags)
    {
        foreach (var crag in crags)
        {
            if (string.IsNullOrWhiteSpace(crag.Name))
            {
                throw new LinkbaseValidationException("A crag has no name.");
            }

            if (crag.Choice == StagedRow.Undecided)
            {
                throw new LinkbaseValidationException($"Choose a match for crag '{crag.Name}', or untick it.");
            }

            if (crag.Choice == StagedRow.CreateNew)
            {
                var newArea = !string.IsNullOrWhiteSpace(crag.NewAreaName);
                if (crag.AreaId is null && !newArea)
                {
                    throw new LinkbaseValidationException($"Choose an area for the new crag '{crag.Name}'.");
                }

                if (crag.AreaId is null && crag.NewAreaParentId is null && crag.NewAreaKind != AreaKind.Country)
                {
                    throw new LinkbaseValidationException($"Choose which area '{crag.NewAreaName}' is in, or mark it as a country, for the new crag '{crag.Name}'.");
                }
            }

            foreach (var route in crag.Routes.Where(r => r.Include))
            {
                if (string.IsNullOrWhiteSpace(route.Name))
                {
                    throw new LinkbaseValidationException($"A route at '{crag.Name}' has no name.");
                }

                if (route.Choice == StagedRow.Undecided)
                {
                    throw new LinkbaseValidationException($"Choose a match for route '{route.Name}', or untick it.");
                }

                if (route.LengthM < 0 || route.Pitches < 0)
                {
                    throw new LinkbaseValidationException($"Length and pitches of '{route.Name}' can't be negative.");
                }
            }
        }
    }

    // A source's id is added once, whichever batch brought it first.
    private static void AddRef(
        LinkbaseDbContext db, HashSet<(string NodeType, string ExternalId)> known, string nodeId, string nodeType, string externalId, string? url, long batchId)
    {
        if (!known.Add((nodeType, externalId)))
        {
            return;
        }

        db.ExternalRefs.Add(new ExternalRef
        {
            NodeId = nodeId,
            NodeType = nodeType,
            Source = DataSource.WebImport,
            ExternalId = externalId,
            Url = url,
            ImportBatchId = batchId,
        });
    }

    // What the review chose may have gone, or never been a crag, route or area, since the review was loaded. Better a clear
    // message before anything is written than a foreign key failing part-way.
    private static async Task CheckChosenRecordsExistAsync(
        LinkbaseDbContext db, IReadOnlyList<StagedCrag> crags, Dictionary<string, AreaRow> areas, CancellationToken cancellationToken)
    {
        var chosen = new Dictionary<string, (string Type, string Name)>();
        foreach (var crag in crags)
        {
            if (crag.UsesExisting)
            {
                chosen[crag.Choice] = ("Crag", crag.Name);
            }

            foreach (var route in crag.Routes.Where(r => r.Include && r.UsesExisting))
            {
                chosen[route.Choice] = ("Route", route.Name);
            }

            if (crag.Choice == StagedRow.CreateNew)
            {
                foreach (var areaId in new[] { crag.AreaId, crag.NewAreaParentId }.OfType<string>())
                {
                    if (!areas.ContainsKey(areaId))
                    {
                        throw new LinkbaseValidationException($"The area chosen for '{crag.Name}' is no longer there. Choose another.");
                    }
                }
            }
        }

        var ids = chosen.Keys.ToList();
        var found = await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).Select(n => new { n.Id, n.Type }).ToDictionaryAsync(n => n.Id, n => n.Type, cancellationToken);
        foreach (var (id, (type, name)) in chosen)
        {
            if (!found.TryGetValue(id, out var actual) || actual != type)
            {
                throw new LinkbaseValidationException($"The {type.ToLowerInvariant()} chosen for '{name}' is no longer there. Review the import again.");
            }
        }
    }

    private static async Task<Dictionary<(string NodeType, string ExternalId), string>> LoadRefsAsync(LinkbaseDbContext db, CancellationToken cancellationToken)
    {
        var refs = await db.ExternalRefs.AsNoTracking()
            .Where(r => r.Source == DataSource.WebImport && r.NodeId != null)
            .Select(r => new { NodeType = r.NodeType!, r.ExternalId, NodeId = r.NodeId! })
            .ToListAsync(cancellationToken);
        return refs.ToDictionary(r => (r.NodeType, r.ExternalId), r => r.NodeId);
    }

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

    private static string RefBase(string? sourceUrl, long batchId) => sourceUrl ?? $"pasted:{batchId}";

    private static string CragKey(string refBase, string crag) => $"{refBase}#{ExportText.ExternalKey(crag)}";

    private static string RouteKey(string refBase, string crag, string route) =>
        $"{refBase}#{ExportText.ExternalKey(crag)}|{ExportText.ExternalKey(route)}";

    private static T? ParseEnum<T>(string? value) where T : struct, Enum
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return SnakeCaseEnumConverter<T>.FromStore(value);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static StoredPayload? Parse(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<StoredPayload>(json, ExtractedPage.JsonOptions);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SetText(JsonObject props, string key, string? value)
    {
        if (Clean(value) is { } text)
        {
            props[key] = text;
        }
    }

    private static string Plural(int count, string singular) => count == 1 ? $"1 {singular}" : $"{count} {singular}s";
}
