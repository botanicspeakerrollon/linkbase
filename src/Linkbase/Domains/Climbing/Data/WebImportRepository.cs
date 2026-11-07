using Linkbase.Data;
using System.Text.Json;
using Linkbase.Domains.Climbing.Import;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

public enum StagedMatch { New, Existing, Ambiguous }

public sealed record MatchCandidate(long Id, string Name, string? Detail, double Similarity);

// Editable rows on the staging screen. Choice says what committing does with a row.
public abstract class StagedRow
{
    public const long Undecided = -1;
    public const long CreateNew = 0;

    public bool Include { get; set; } = true;
    public StagedMatch Match { get; set; }
    public List<MatchCandidate> Candidates { get; set; } = [];

    // Undecided, CreateNew, or the id of the existing row to use.
    public long Choice { get; set; } = CreateNew;
}

public sealed class StagedCrag : StagedRow
{
    public required string Name { get; set; }
    public string? RockType { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string? ExtractedArea { get; set; }

    // Where a new crag goes: an existing area, or a new one with this name under ParentAreaId.
    public long? AreaId { get; set; }
    public string? AreaLabel { get; set; }
    public string? NewAreaName { get; set; }
    public long? NewAreaParentId { get; set; }
    public AreaKind? NewAreaKind { get; set; }

    public List<StagedRoute> Routes { get; set; } = [];
}

public sealed class StagedRoute : StagedRow
{
    public required string Name { get; set; }
    public string? Grade { get; set; }
    public GradeSystem? GradeSystem { get; set; }
    public Discipline? Discipline { get; set; }
    public int? LengthM { get; set; }
    public int? Pitches { get; set; }
    public string? Summary { get; set; }
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

// The staged web import: match extracted rows against the logbook, write the confirmed ones in one
// transaction, and undo a batch later.
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
            var areas = await db.Areas.AsNoTracking()
                .Select(a => new AreaLookup(a.Id, a.ParentId, a.Name))
                .ToListAsync(cancellationToken);
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
                ProposeArea(crag, areas, kinds);

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
                        Summary = r.Summary,
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

        foreach (var staged in included)
        {
            long cragId;
            if (staged.Choice > 0)
            {
                cragId = staged.Choice;
                cragsMatched++;
            }
            else
            {
                var (areaId, created) = await ResolveAreaAsync(db, staged, batchId, cancellationToken);
                areasCreated += created ? 1 : 0;
                var crag = new Crag
                {
                    UserId = AppUser.DefaultId,
                    AreaId = areaId,
                    Name = staged.Name.Trim(),
                    RockType = Clean(staged.RockType),
                    Lat = staged.Lat,
                    Lon = staged.Lon,
                    ImportBatchId = batchId,
                };
                db.Crags.Add(crag);
                await db.SaveChangesAsync(cancellationToken);
                cragId = crag.Id;
                cragsCreated++;
            }

            await AddRefAsync(db, ExternalEntityType.Crag, cragId, CragKey(refBase, staged.Name), batch.SourceUrl, batchId, cancellationToken);

            foreach (var stagedRoute in staged.Routes.Where(r => r.Include))
            {
                long routeId;
                if (stagedRoute.Choice > 0)
                {
                    routeId = stagedRoute.Choice;
                    routesMatched++;
                }
                else
                {
                    var route = new Route
                    {
                        UserId = AppUser.DefaultId,
                        CragId = cragId,
                        Name = stagedRoute.Name.Trim(),
                        Grade = Clean(stagedRoute.Grade),
                        GradeSystem = stagedRoute.GradeSystem,
                        Discipline = stagedRoute.Discipline,
                        LengthM = stagedRoute.LengthM,
                        Pitches = stagedRoute.Pitches,
                        Description = Clean(stagedRoute.Summary),
                        ImportBatchId = batchId,
                    };
                    db.Routes.Add(route);
                    await db.SaveChangesAsync(cancellationToken);
                    routeId = route.Id;
                    routesCreated++;
                }

                await AddRefAsync(db, ExternalEntityType.Route, routeId, RouteKey(refBase, staged.Name, stagedRoute.Name), batch.SourceUrl, batchId, cancellationToken);
            }
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

        var routes = await db.Routes.Where(r => r.ImportBatchId == batchId)
            .Select(r => new { r.Id, r.Name, Edited = r.UpdatedAt != r.CreatedAt, Ascents = r.Ascents.Count })
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
                await EntityLinks.RemoveAsync(db, ExternalEntityType.Route, route.Id, cancellationToken);
                await db.Routes.Where(r => r.Id == route.Id).ExecuteDeleteAsync(cancellationToken);
                routesDeleted++;
            }
        }

        var crags = await db.Crags.Where(c => c.ImportBatchId == batchId)
            .Select(c => new { c.Id, c.Name, Edited = c.UpdatedAt != c.CreatedAt, Routes = c.Routes.Count })
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
                await EntityLinks.RemoveAsync(db, ExternalEntityType.Crag, crag.Id, cancellationToken);
                await db.Crags.Where(c => c.Id == crag.Id).ExecuteDeleteAsync(cancellationToken);
                cragsDeleted++;
            }
        }

        var areasDeleted = await DeleteUnusedAreasAsync(db, batchId, skipped, cancellationToken);

        // Refs this batch added to rows that were already in the logbook.
        var keptRoutes = await db.Routes.Where(r => r.ImportBatchId == batchId).Select(r => r.Id).ToListAsync(cancellationToken);
        var keptCrags = await db.Crags.Where(c => c.ImportBatchId == batchId).Select(c => c.Id).ToListAsync(cancellationToken);
        await db.ExternalRefs
            .Where(r => r.ImportBatchId == batchId
                && !(r.EntityType == ExternalEntityType.Route && keptRoutes.Contains(r.EntityId))
                && !(r.EntityType == ExternalEntityType.Crag && keptCrags.Contains(r.EntityId)))
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

    private static async Task<int> DeleteUnusedAreasAsync(LinkbaseDbContext db, long batchId, List<string> skipped, CancellationToken cancellationToken)
    {
        var areas = await db.Areas.Where(a => a.ImportBatchId == batchId)
            .Select(a => new { a.Id, a.Name, Edited = a.UpdatedAt != a.CreatedAt })
            .ToListAsync(cancellationToken);
        var deleted = 0;
        foreach (var area in areas)
        {
            var inUse = await db.Crags.AnyAsync(c => c.AreaId == area.Id, cancellationToken)
                || await db.Areas.AnyAsync(a => a.ParentId == area.Id, cancellationToken);
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
                await db.Areas.Where(a => a.Id == area.Id).ExecuteDeleteAsync(cancellationToken);
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

    private static async Task<(long Id, bool Created)> ResolveAreaAsync(LinkbaseDbContext db, StagedCrag crag, long batchId, CancellationToken cancellationToken)
    {
        if (crag.AreaId is { } areaId)
        {
            return (areaId, false);
        }

        var name = crag.NewAreaName!.Trim();
        var parentId = crag.NewAreaParentId;
        var existing = await db.Areas
            .Where(a => a.ParentId == parentId && a.Name == name)
            .Select(a => (long?)a.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is { } id)
        {
            return (id, false);
        }

        var parentKind = parentId is null
            ? (AreaKind?)null
            : await db.Areas.Where(a => a.Id == parentId).Select(a => (AreaKind?)a.Kind).SingleOrDefaultAsync(cancellationToken);
        var area = new Area
        {
            UserId = AppUser.DefaultId,
            ParentId = parentId,
            Name = name,
            Kind = parentKind switch
            {
                null => AreaKind.Country,
                AreaKind.Country => AreaKind.Region,
                _ => AreaKind.Area,
            },
            ImportBatchId = batchId,
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync(cancellationToken);
        return (area.Id, true);
    }

    private static async Task AddRefAsync(
        LinkbaseDbContext db,
        ExternalEntityType type,
        long entityId,
        string externalId,
        string? url,
        long batchId,
        CancellationToken cancellationToken)
    {
        var exists = await db.ExternalRefs.AnyAsync(
            r => r.Source == DataSource.WebImport && r.EntityType == type && r.ExternalId == externalId,
            cancellationToken);
        if (exists)
        {
            return;
        }

        db.ExternalRefs.Add(new ExternalRef
        {
            EntityType = type,
            EntityId = entityId,
            Source = DataSource.WebImport,
            ExternalId = externalId,
            Url = url,
            ImportBatchId = batchId,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<(ExternalEntityType, string), long>> LoadRefsAsync(LinkbaseDbContext db, CancellationToken cancellationToken)
    {
        var refs = await db.ExternalRefs.AsNoTracking()
            .Where(r => r.Source == DataSource.WebImport)
            .Select(r => new { r.EntityType, r.ExternalId, r.EntityId })
            .ToListAsync(cancellationToken);
        return refs.ToDictionary(r => (r.EntityType, r.ExternalId), r => r.EntityId);
    }

    private static async Task MatchCragAsync(
        LinkbaseDbContext db,
        StagedCrag crag,
        string refBase,
        Dictionary<(ExternalEntityType, string), long> refs,
        CancellationToken cancellationToken)
    {
        var name = crag.Name;
        var candidates = await db.Crags.AsNoTracking()
            .Where(c => EF.Functions.TrigramsSimilarity(c.Name, name) >= SimilarityThreshold)
            .OrderByDescending(c => EF.Functions.TrigramsSimilarity(c.Name, name))
            .Take(MaxCandidates * 2)
            .Select(c => new MatchCandidate(c.Id, c.Name, c.Area.Name, EF.Functions.TrigramsSimilarity(c.Name, name)))
            .ToListAsync(cancellationToken);

        if (refs.TryGetValue((ExternalEntityType.Crag, CragKey(refBase, name)), out var refId))
        {
            var known = await db.Crags.AsNoTracking()
                .Where(c => c.Id == refId)
                .Select(c => new MatchCandidate(c.Id, c.Name, c.Area.Name, 1))
                .SingleOrDefaultAsync(cancellationToken);
            if (known is not null)
            {
                Apply(crag, [known]);
                return;
            }
        }

        Classify(crag, name, candidates);
    }

    private static async Task MatchRoutesAsync(
        LinkbaseDbContext db,
        StagedCrag crag,
        string refBase,
        Dictionary<(ExternalEntityType, string), long> refs,
        CancellationToken cancellationToken)
    {
        foreach (var route in crag.Routes)
        {
            // Only a chosen existing crag has routes to match against.
            if (crag.Choice <= 0)
            {
                route.Match = StagedMatch.New;
                route.Candidates = [];
                route.Choice = StagedRow.CreateNew;
                continue;
            }

            var cragId = crag.Choice;
            var name = route.Name;
            if (refs.TryGetValue((ExternalEntityType.Route, RouteKey(refBase, crag.Name, name)), out var refId))
            {
                var known = await db.Routes.AsNoTracking()
                    .Where(r => r.Id == refId && r.CragId == cragId)
                    .Select(r => new MatchCandidate(r.Id, r.Name, r.Grade, 1))
                    .SingleOrDefaultAsync(cancellationToken);
                if (known is not null)
                {
                    Apply(route, [known]);
                    continue;
                }
            }

            var candidates = await db.Routes.AsNoTracking()
                .Where(r => r.CragId == cragId && EF.Functions.TrigramsSimilarity(r.Name, name) >= SimilarityThreshold)
                .OrderByDescending(r => EF.Functions.TrigramsSimilarity(r.Name, name))
                .Take(MaxCandidates * 2)
                .Select(r => new MatchCandidate(r.Id, r.Name, r.Grade, EF.Functions.TrigramsSimilarity(r.Name, name)))
                .ToListAsync(cancellationToken);
            Classify(route, name, candidates);
        }
    }

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

    private sealed record AreaLookup(long Id, long? ParentId, string Name);

    // Proposes where a new crag goes, from what the text said. A name it can't place is left for the user.
    private static void ProposeArea(StagedCrag crag, List<AreaLookup> areas, IReadOnlyList<ExtractedArea> extractedAreas)
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

    private static AreaLookup? FindArea(List<AreaLookup> areas, string name)
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

    private static string Plural(int count, string singular) => count == 1 ? $"1 {singular}" : $"{count} {singular}s";
}
