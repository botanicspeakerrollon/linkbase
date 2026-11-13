using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Staging;

public sealed partial class ImportStagingService
{
    // Removes what the import added: its links, then its records. A record edited since stays, and so does one that something
    // else now depends on: a link added since, or a record that stays and requires a link to it. They are reported, and the
    // import stays committed so it can be undone again once they're dealt with. Types, fields and relationships it added stay
    // in the model, unless nothing is left of them.
    public async Task<StagedUndo> UndoAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == batchId && b.UserId == AppUser.DefaultId && GenericSources.Contains(b.Source), cancellationToken)
            ?? throw new LinkbaseValidationException("There's no such import.");
        if (batch.Status != ImportStatus.Committed)
        {
            throw new LinkbaseValidationException("This import has already been undone.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (keep, kept) = await KeptAsync(db, batchId, cancellationToken);
        var changes = await ChangesAsync(db, batchId, removing: true, keep, cancellationToken);

        var leaving = LeavingLinks(db, batchId, keep);
        var links = await leaving
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

        var linksRemoved = await leaving.ExecuteDeleteAsync(cancellationToken);

        // The import's own records first, then their links one side at a time, so both lookups use an index. Joining every
        // link to its ends took minutes on the Swiss sample's 45M links.
        var removed = db.Nodes.Where(n => n.ImportBatchId == batchId && !keep.Contains(n.Id)).Select(n => n.Id);

        // Links from a module's derived records are the module's, so they go with the records, and its hooks hear of it.
        if (await db.NodeTypes.AnyAsync(t => t.Owner != null, cancellationToken))
        {
            var derived = db.NodeLinks.Where(l =>
                (removed.Contains(l.SourceId) && l.Target.NodeType.Owner != null) || (removed.Contains(l.TargetId) && l.Source.NodeType.Owner != null));
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

        await ExternalRefRepository.RemoveForNodesAsync(db, removed, cancellationToken);

        // Where records that were already there were read from.
        await db.ExternalRefs.Where(r => r.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);
        var recordsRemoved = await db.Nodes.Where(n => n.ImportBatchId == batchId && !keep.Contains(n.Id)).ExecuteDeleteAsync(cancellationToken);
        await RemoveWhatItCreatedAsync(db, batch.RawPayload, cancellationToken);
        var (restored, reverted) = await RevertUpdatesAsync(db, batchId, kept, cancellationToken);
        changes.AddRange(restored);
        if (kept.Count == 0)
        {
            batch.Status = ImportStatus.Undone;
            await db.SaveChangesAsync(cancellationToken);
        }

        long? changeSet = null;
        if ((await ChangeRecorder.SettingsAsync(db, cancellationToken)).Record)
        {
            List<PendingChange> undone =
            [
                new PendingChange(ChangeEntityKind.ImportBatch, batchId.ToString(System.Globalization.CultureInfo.InvariantCulture), ChangeOp.Delete,
                    OldValue: JsonSerializer.Serialize(new { records = recordsRemoved, links = linksRemoved, kept = kept.Count, reverted = reverted.Count })),
                .. reverted,
            ];
            changeSet = await ChangeRecorder.WriteAsync(db, JustificationKind.Import, new ChangeReason($"Import #{batchId} undone", batch.SourceUrl), undone,
                importBatchId: batchId, cancellationToken: cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        using (ChangeRecorder.Causing(changeSet))
        {
            await _hooks.AfterChangeAsync(changes, cancellationToken);
        }

        return new StagedUndo(recordsRemoved, linksRemoved, kept) { Reverted = restored.Count };
    }

    // Puts back what a committing import changed on records that were already there, field by field, as the trail logged it
    // (its old values). A field someone has changed since stays as it is, and is reported, like a record edited since. One that
    // is back at its old value already, from an earlier undo that left something, is skipped.
    private async Task<(List<RecordChange> Restored, List<PendingChange> Logged)> RevertUpdatesAsync(
        LinkbaseDbContext db, long batchId, List<string> kept, CancellationToken cancellationToken)
    {
        var restored = new List<RecordChange>();
        var logged = new List<PendingChange>();
        var updates = await db.Changes.AsNoTracking()
            .Where(c => c.EntityKind == ChangeEntityKind.Node && c.Op == ChangeOp.Update
                && c.ChangeSet.Justification.ImportBatchId == batchId && c.ChangeSet.Justification.Kind == JustificationKind.Import
                && db.Changes.Any(b => b.ChangeSetId == c.ChangeSetId && b.EntityKind == ChangeEntityKind.ImportBatch && b.Op == ChangeOp.Create))
            .OrderBy(c => c.Id)
            .Select(c => new { c.EntityId, c.FieldKey, c.OldValue, c.NewValue })
            .ToListAsync(cancellationToken);
        if (updates.Count == 0)
        {
            return (restored, logged);
        }

        var records = new RecordRepository(contextFactory);
        var types = new Dictionary<string, RecordType?>(StringComparer.Ordinal);
        var shown = 0;
        foreach (var group in updates.GroupBy(u => u.EntityId))
        {
            var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == group.Key, cancellationToken);
            if (node is null)
            {
                continue;
            }

            if (!types.TryGetValue(node.Type, out var type))
            {
                types[node.Type] = type = await records.TypeAsync(node.Type, cancellationToken);
            }

            var nameBefore = node.Name;
            var before = RecordRepository.Values(node.Props);
            var name = node.Name;
            var values = before.ToDictionary(v => v.Key, v => (string?)v.Value);
            foreach (var change in group)
            {
                var key = change.FieldKey ?? "name";
                var now = key == "name" ? name : values.GetValueOrDefault(key);
                if (now == change.NewValue || (string.IsNullOrEmpty(now) && string.IsNullOrEmpty(change.NewValue)))
                {
                    if (key == "name")
                    {
                        name = change.OldValue ?? name;
                    }
                    else
                    {
                        values[key] = change.OldValue;
                    }
                }
                else if (!(now == change.OldValue || (string.IsNullOrEmpty(now) && string.IsNullOrEmpty(change.OldValue))))
                {
                    if (++shown <= KeptShown)
                    {
                        kept.Add($"{node.Type} '{nameBefore}': {RecordRepository.Title(key)} was changed since the import, so it stays as it is.");
                    }
                }
            }

            if (type is null)
            {
                continue;
            }

            var props = RecordRepository.Props(type.Editable, values, node.Props);
            var after = RecordRepository.Values(props);
            if (name == nameBefore && after.OrderBy(v => v.Key).SequenceEqual(before.OrderBy(v => v.Key)))
            {
                continue;
            }

            node.Name = name;
            node.Props = props;
            await db.SaveChangesAsync(cancellationToken);
            logged.AddRange(ChangeRecorder.NodeUpdated(node.Id, nameBefore, before, node.Name, after));
            restored.Add(new RecordChange(RecordChangeKind.Updated, node.Type, node.Id, before, after));
        }

        if (shown > KeptShown)
        {
            kept.Add($"And {shown - KeptShown:N0} more values changed since the import.");
        }

        return (restored, logged);
    }

    // The batch's links that go. A link between two records that stay is kept with them, and so is a link a record that stays
    // requires.
    private static IQueryable<NodeLink> LeavingLinks(LinkbaseDbContext db, long batchId, IReadOnlyCollection<string> keep) =>
        db.NodeLinks.Where(l => l.ImportBatchId == batchId
            && !(keep.Contains(l.SourceId)
                && (keep.Contains(l.TargetId)
                    || db.LinkDefinitions.Any(d => d.SourceType == l.Source.Type && d.RelType == l.RelType && d.TargetType == l.Target.Type && d.Required))));

    private const int KeptShown = 20;

    // The records of a batch that stay, and why, for the people undoing it.
    private static async Task<(List<string> Ids, List<string> Why)> KeptAsync(LinkbaseDbContext db, long batchId, CancellationToken cancellationToken)
    {
        var added = db.Nodes.Where(n => n.ImportBatchId == batchId).Select(n => n.Id);
        var edited = await db.Nodes.Where(n => n.ImportBatchId == batchId && n.UpdatedAt != n.CreatedAt).Select(n => n.Id).ToListAsync(cancellationToken);
        // A module's derived records and their links go with the records they come from, so they don't count.
        var linked = (await db.NodeLinks.Where(l => l.ImportBatchId != batchId && added.Contains(l.SourceId) && l.Target.NodeType.Owner == null)
                .Select(l => l.SourceId).Distinct().ToListAsync(cancellationToken))
            .Concat(await db.NodeLinks.Where(l => l.ImportBatchId != batchId && added.Contains(l.TargetId) && l.Source.NodeType.Owner == null)
                .Select(l => l.TargetId).Distinct().ToListAsync(cancellationToken))
            .Except(edited)
            .Distinct()
            .ToList();

        // A record that stays still needs the records its required links point at.
        var keep = edited.Concat(linked).ToHashSet(StringComparer.Ordinal);
        while (keep.Count > 0)
        {
            var now = keep.ToList();
            var more = await db.NodeLinks
                .Where(l => l.ImportBatchId == batchId && now.Contains(l.SourceId) && !now.Contains(l.TargetId) && added.Contains(l.TargetId)
                    && db.LinkDefinitions.Any(d => d.SourceType == l.Source.Type && d.RelType == l.RelType && d.TargetType == l.Target.Type && d.Required))
                .Select(l => l.TargetId)
                .Distinct()
                .ToListAsync(cancellationToken);
            if (more.Count == 0)
            {
                break;
            }

            keep.UnionWith(more);
        }

        var ids = keep.ToList();
        var names = await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).Select(n => new { n.Id, n.Type, n.Name }).ToListAsync(cancellationToken);
        var editedSet = edited.ToHashSet(StringComparer.Ordinal);
        var linkedSet = linked.ToHashSet(StringComparer.Ordinal);
        var why = names.OrderBy(n => n.Type, StringComparer.Ordinal).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => $"{n.Type} '{n.Name}': {(editedSet.Contains(n.Id) ? "edited since the import" : linkedSet.Contains(n.Id) ? "linked to since the import" : "needed by a record that stays")}.")
            .ToList();
        if (why.Count > KeptShown)
        {
            why = why.Take(KeptShown).Append($"And {why.Count - KeptShown:N0} more.").ToList();
        }

        return (ids, why);
    }

    // What an import added or is about to remove, for the hooks of the types they watch. Nothing is read when no hook
    // watches anything, as for most models.
    private async Task<List<RecordChange>> ChangesAsync(LinkbaseDbContext db, long batch, bool removing, IReadOnlyCollection<string> keep, CancellationToken cancellationToken)
    {
        var watched = _hooks.Watched;
        var changes = new List<RecordChange>();
        if (watched.Count == 0)
        {
            return changes;
        }

        var none = new Dictionary<string, string>();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.ImportBatchId == batch && !keep.Contains(n.Id) && watched.Contains(n.Type))
            .Select(n => new { n.Id, n.Type, n.Props })
            .ToListAsync(cancellationToken);
        foreach (var node in nodes)
        {
            var values = RecordRepository.Values(node.Props);
            changes.Add(removing
                ? new RecordChange(RecordChangeKind.Deleted, node.Type, node.Id, values, none)
                : new RecordChange(RecordChangeKind.Created, node.Type, node.Id, none, values));
        }

        var links = await LeavingLinks(db, batch, keep).AsNoTracking()
            .Where(l => watched.Contains(l.Source.Type) || watched.Contains(l.Target.Type))
            .Select(l => new { l.SourceId, SourceType = l.Source.Type, l.RelType, l.TargetId, TargetType = l.Target.Type, l.Props })
            .ToListAsync(cancellationToken);
        var kind = removing ? RecordChangeKind.Unlinked : RecordChangeKind.Linked;
        foreach (var link in links)
        {
            var values = RecordRepository.Values(link.Props);
            var (before, after) = removing ? (values, none) : (none, values);
            changes.Add(new RecordChange(kind, link.SourceType, link.SourceId, before, after, link.RelType, link.TargetId));
            changes.Add(new RecordChange(kind, link.TargetType, link.TargetId, before, after, link.RelType, link.SourceId));
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
