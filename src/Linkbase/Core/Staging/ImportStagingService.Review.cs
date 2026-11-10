using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Staging;

public sealed partial class ImportStagingService
{
    public async Task<StagedBatch?> GetAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.AsNoTracking()
            .Where(b => b.Id == batchId && b.UserId == AppUser.DefaultId)
            .Select(b => new { b.Id, b.Source, b.RawPayload, b.Status, b.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (batch is null || !IsGeneric(batch.Source))
        {
            return null;
        }

        var counts = await db.StagedItems.AsNoTracking()
            .Where(i => i.ImportBatchId == batchId)
            .GroupBy(i => i.Kind)
            .Select(g => new { Kind = g.Key, Total = g.Count(), Included = g.Count(i => i.Included) })
            .ToListAsync(cancellationToken);
        return Summarise(batch.Id, batch.Source, batch.RawPayload, batch.Status, batch.CreatedAt, counts.ToDictionary(c => c.Kind, c => (c.Total, c.Included)));
    }

    public async Task<IReadOnlyList<StagedBatch>> RecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batches = await db.ImportBatches.AsNoTracking()
            .Where(b => (b.Source == DataSource.Csv || b.Source == DataSource.Ai || b.Source == DataSource.WebImport) && b.UserId == AppUser.DefaultId)
            .OrderByDescending(b => b.Id)
            .Take(count)
            .Select(b => new { b.Id, b.Source, b.RawPayload, b.Status, b.CreatedAt })
            .ToListAsync(cancellationToken);
        var ids = batches.Where(b => b.Status == ImportStatus.Staged).Select(b => b.Id).ToList();
        var counts = ids.Count == 0
            ? []
            : await db.StagedItems.AsNoTracking()
                .Where(i => ids.Contains(i.ImportBatchId))
                .GroupBy(i => new { i.ImportBatchId, i.Kind })
                .Select(g => new { g.Key.ImportBatchId, g.Key.Kind, Total = g.Count(), Included = g.Count(i => i.Included) })
                .ToListAsync(cancellationToken);
        return batches.Select(b => Summarise(b.Id, b.Source, b.RawPayload, b.Status, b.CreatedAt,
            counts.Where(c => c.ImportBatchId == b.Id).ToDictionary(c => c.Kind, c => (c.Total, c.Included)))).ToList();
    }

    private static StagedBatch Summarise(
        long id, DataSource source, string? payload, ImportStatus status, DateTimeOffset at, Dictionary<StagedKind, (int Total, int Included)> counts)
    {
        var summary = JsonNode.Parse(payload ?? "{}") as JsonObject ?? [];
        var records = counts.GetValueOrDefault(StagedKind.Record);
        var links = counts.GetValueOrDefault(StagedKind.Link);
        var notes = (summary["notes"] as JsonArray)?.Select(n => (string?)n ?? "").ToList() ?? [];
        return new StagedBatch(id, source, (string?)summary["file"] ?? ExternalRefRepository.SourceName(source), (string?)summary["what"] ?? "records", status, records.Total, links.Total,
            records.Included + links.Included, (int?)summary["alreadyThere"] ?? 0, (int?)summary["skipped"] ?? 0, notes, (int?)summary["added"] ?? 0, at);
    }

    // A page of a batch's items for review: records in the order they were in the file, then links.
    public async Task<StagedPage> ReviewAsync(long batchId, StagedKind? kind, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.StagedItems.AsNoTracking().Where(i => i.ImportBatchId == batchId);
        if (kind is { } only)
        {
            query = query.Where(i => i.Kind == only);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(i => i.Kind).ThenBy(i => i.Id).Skip(page * pageSize).Take(pageSize)
            .Select(i => new { i.Id, i.Kind, i.Type, i.Key, i.Name, i.Props, i.RelType, i.SourceId, i.TargetId, i.TargetType, i.Included })
            .ToListAsync(cancellationToken);
        // A link is shown by the names of its ends: those staged in this batch, or else those already in the model.
        var ends = items.Where(i => i.Kind == StagedKind.Link).SelectMany(i => new[] { i.SourceId, i.TargetId }).OfType<string>().Distinct().ToList();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ends.Count > 0)
        {
            foreach (var node in await db.Nodes.AsNoTracking().Where(n => ends.Contains(n.Id)).Select(n => new { n.Id, n.Name }).ToListAsync(cancellationToken))
            {
                names[node.Id] = node.Name;
            }

            var staged = await db.StagedItems.AsNoTracking().Where(i => i.ImportBatchId == batchId && i.Kind == StagedKind.Record && i.Key != null)
                .Select(i => new { i.Type, i.Key, i.Name }).ToListAsync(cancellationToken);
            foreach (var record in staged)
            {
                names.TryAdd($"{record.Type.ToLowerInvariant()}:{record.Key}", record.Name ?? record.Key!);
            }
        }

        string End(string? id) => id is null ? "" : names.GetValueOrDefault(id, id);
        return new StagedPage(total, items.Select(i => i.Kind == StagedKind.Record
            ? new StagedRow(i.Id, i.Kind, i.Type, i.Name ?? i.Key ?? "", Detail(i.Key, i.Props), i.Included)
            : new StagedRow(i.Id, i.Kind, i.Type, $"{End(i.SourceId)} {i.RelType} {End(i.TargetId)}", $"{i.Type} {i.RelType} {i.TargetType}", i.Included) { TargetType = i.TargetType }).ToList());
    }

    // The key, then each value, such as "s1 · city: Bern · rating: 4".
    private static string Detail(string? key, string? props)
    {
        var values = JsonNode.Parse(props ?? "{}") as JsonObject ?? [];
        return string.Join(" · ", new[] { key ?? "" }.Concat(values.Select(v => $"{v.Key}: {Shown(v.Value)}")));
    }

    private static string Shown(JsonNode? value) => value switch
    {
        JsonArray list => string.Join(", ", list.Select(Shown)),
        JsonValue text when text.TryGetValue<string>(out var s) => s,
        _ => value?.ToJsonString() ?? "",
    };

    // Items left out are not imported. Only a staged batch can change.
    public async Task SetIncludedAsync(long batchId, IReadOnlyCollection<long> itemIds, bool included, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureStagedAsync(db, batchId, cancellationToken);
        await db.StagedItems.Where(i => i.ImportBatchId == batchId && itemIds.Contains(i.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Included, included), cancellationToken);
    }

    // Every item of a kind, or of both when kind is null.
    public async Task SetAllIncludedAsync(long batchId, StagedKind? kind, bool included, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureStagedAsync(db, batchId, cancellationToken);
        await db.StagedItems.Where(i => i.ImportBatchId == batchId && (kind == null || i.Kind == kind))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Included, included), cancellationToken);
    }

    private static async Task<ImportBatch> EnsureStagedAsync(LinkbaseDbContext db, long batchId, CancellationToken cancellationToken)
    {
        var batch = await db.ImportBatches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == batchId && b.UserId == AppUser.DefaultId && GenericSources.Contains(b.Source), cancellationToken)
            ?? throw new LinkbaseValidationException("There's no such import.");
        return batch.Status == ImportStatus.Staged ? batch : throw new LinkbaseValidationException("This import has already been committed or discarded.");
    }

    // Drops a staged import without adding anything.
    public async Task DiscardAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureStagedAsync(db, batchId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.StagedItems.Where(i => i.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);
        await db.ImportBatches.Where(b => b.Id == batchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, ImportStatus.Undone).SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
