using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;

// The staged web import over the generic store: match extracted rows against the Area, Crag and Route records, write the
// confirmed ones in one transaction, tagged with the batch, and undo a batch later. See docs/history/climbing-to-generic-plan.md.
public sealed partial class WebImportRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
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
