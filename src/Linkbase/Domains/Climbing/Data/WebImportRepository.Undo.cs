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
}
