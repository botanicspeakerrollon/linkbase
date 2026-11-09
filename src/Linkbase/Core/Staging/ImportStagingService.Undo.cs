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
    // Removes everything the import added: its links, then its records, including changes made to them since. Types,
    // fields and relationships it added stay in the model, unless nothing is left of them.
    public async Task UndoAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == batchId && b.UserId == AppUser.DefaultId && GenericSources.Contains(b.Source), cancellationToken)
            ?? throw new LinkbaseValidationException("There's no such import.");
        if (batch.Status != ImportStatus.Committed)
        {
            throw new LinkbaseValidationException("This import has already been undone.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changes = await ChangesAsync(db, batchId, removing: true, cancellationToken);
        var links = await db.NodeLinks.Where(l => l.ImportBatchId == batchId)
            .GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
            .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // A record keeps at least one of each link its type requires, so undoing can't take away its last one. Records this
        // import added go with it, so they don't count.
        foreach (var group in links)
        {
            var required = await db.LinkDefinitions.AnyAsync(
                d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType && d.Required, cancellationToken);
            var stranded = !required ? 0 : await db.NodeLinks
                .Where(l => l.ImportBatchId == batchId && l.RelType == group.RelType && l.Source.Type == group.SourceType && l.Target.Type == group.TargetType
                    && l.Source.ImportBatchId != batchId)
                .Select(l => l.SourceId)
                .Distinct()
                .CountAsync(source => !db.NodeLinks.Any(o => o.SourceId == source && o.RelType == group.RelType && o.Target.Type == group.TargetType && o.ImportBatchId != batchId), cancellationToken);
            if (stranded > 0)
            {
                throw new LinkbaseValidationException(
                    $"Undoing would leave {stranded:N0} {group.SourceType} record(s) with no {group.SourceType} {group.RelType} {group.TargetType} link, which each needs. Link them another way first.");
            }
        }

        foreach (var group in links)
        {
            await db.LinkDefinitions
                .Where(d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links - group.Count), cancellationToken);
        }

        await db.NodeLinks.Where(l => l.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);

        // The import's own records first, then their links one side at a time, so both lookups use an index. Joining every
        // link to its ends took minutes on the Swiss sample's 45M links.
        var added = db.Nodes.Where(n => n.ImportBatchId == batchId).Select(n => n.Id);

        // Links from a module's derived records are the module's, so they go with the records, and its hooks hear of it.
        if (await db.NodeTypes.AnyAsync(t => t.Owner != null, cancellationToken))
        {
            var derived = db.NodeLinks.Where(l =>
                (added.Contains(l.SourceId) && l.Target.NodeType.Owner != null) || (added.Contains(l.TargetId) && l.Source.NodeType.Owner != null));
            foreach (var group in await derived.GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
                         .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
                         .ToListAsync(cancellationToken))
            {
                await db.LinkDefinitions
                    .Where(d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links - group.Count), cancellationToken);
            }

            await derived.ExecuteDeleteAsync(cancellationToken);
        }

        var linked = await db.NodeLinks.CountAsync(l => added.Contains(l.SourceId), cancellationToken)
            + await db.NodeLinks.CountAsync(l => added.Contains(l.TargetId), cancellationToken);
        if (linked > 0)
        {
            throw new LinkbaseValidationException($"Records this import added have {linked:N0} link(s) added since. Remove those first.");
        }

        await ExternalRefRepository.RemoveForNodesAsync(db, added, cancellationToken);
        await db.Nodes.Where(n => n.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);
        await RemoveWhatItCreatedAsync(db, batch.RawPayload, cancellationToken);
        batch.Status = ImportStatus.Undone;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _hooks.AfterChangeAsync(changes, cancellationToken);
    }

    // What an import added or is about to remove, for the hooks of the types they watch. Nothing is read when no hook
    // watches anything, as for most models.
    private async Task<List<RecordChange>> ChangesAsync(LinkbaseDbContext db, long batch, bool removing, CancellationToken cancellationToken)
    {
        var watched = _hooks.Watched;
        var changes = new List<RecordChange>();
        if (watched.Count == 0)
        {
            return changes;
        }

        var none = new Dictionary<string, string>();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.ImportBatchId == batch && watched.Contains(n.Type))
            .Select(n => new { n.Id, n.Type, n.Props })
            .ToListAsync(cancellationToken);
        foreach (var node in nodes)
        {
            var values = RecordRepository.Values(node.Props);
            changes.Add(removing
                ? new RecordChange(RecordChangeKind.Deleted, node.Type, node.Id, values, none)
                : new RecordChange(RecordChangeKind.Created, node.Type, node.Id, none, values));
        }

        var links = await db.NodeLinks.AsNoTracking()
            .Where(l => l.ImportBatchId == batch && (watched.Contains(l.Source.Type) || watched.Contains(l.Target.Type)))
            .Select(l => new { l.SourceId, SourceType = l.Source.Type, l.RelType, l.TargetId, TargetType = l.Target.Type })
            .ToListAsync(cancellationToken);
        var kind = removing ? RecordChangeKind.Unlinked : RecordChangeKind.Linked;
        foreach (var link in links)
        {
            changes.Add(new RecordChange(kind, link.SourceType, link.SourceId, none, none, link.RelType, link.TargetId));
            changes.Add(new RecordChange(kind, link.TargetType, link.TargetId, none, none, link.RelType, link.SourceId));
        }

        return changes;
    }

    // A type the import added goes too, with its fields, once nothing is left of it. So does a relationship it added, once
    // it has no links. Fields it added to a type that was already there stay. Reads the older single type and relationship
    // as well as lists.
    private static async Task RemoveWhatItCreatedAsync(LinkbaseDbContext db, string? summary, CancellationToken cancellationToken)
    {
        var created = (JsonNode.Parse(summary ?? "{}") as JsonObject)?["created"] as JsonObject;
        var types = ((created?["types"] as JsonArray)?.Select(t => (string?)t) ?? []).Append((string?)created?["type"]).OfType<string>().ToList();
        var relationships = ((created?["relationships"] as JsonArray)?.OfType<JsonObject>().Select(r => ((string?)r["from"], (string?)r["rel"], (string?)r["to"])) ?? [])
            .Append(((string?)created?["from"], (string?)created?["rel"], (string?)created?["to"]))
            .Where(r => r.Item1 is not null && r.Item2 is not null && r.Item3 is not null)
            .ToList();

        // Relationships first, so a type's last relationship doesn't keep it.
        foreach (var (from, rel, to) in relationships)
        {
            await db.LinkDefinitions.Where(d => d.SourceType == from && d.RelType == rel && d.TargetType == to && d.Links == 0).ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var type in types)
        {
            if (!await db.Nodes.AnyAsync(n => n.Type == type, cancellationToken)
                && !await db.LinkDefinitions.AnyAsync(d => (d.SourceType == type || d.TargetType == type) && d.Links > 0, cancellationToken))
            {
                await db.LinkDefinitions.Where(d => d.SourceType == type || d.TargetType == type).ExecuteDeleteAsync(cancellationToken);
                await db.FieldDefinitions.Where(f => f.NodeTypeName == type).ExecuteDeleteAsync(cancellationToken);
                await db.NodeTypes.Where(t => t.Name == type).ExecuteDeleteAsync(cancellationToken);
            }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value, NpgsqlDbType Type)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        foreach (var (name, value, type) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        }

        return await command.ExecuteNonQueryAsync();
    }
}
