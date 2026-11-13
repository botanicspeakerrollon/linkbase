using System.Text.Json.Nodes;
using Linkbase.Core.History;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Records;

// What a merge did, for telling the person.
public sealed record MergeResult(int LinksMoved, int LinksDropped, int ValuesFilled, int RefsMoved);

public sealed partial class RecordRepository
{
    // Folds one record into another of the same type: the one kept keeps its name and its own values, gains any value it
    // lacked (and any item of a list it lacked), and takes over every link and external id of the one dropped, which is then
    // deleted. A link the kept record already has, or that would join it to itself, is dropped rather than doubled. All of it
    // is saved together, or none of it, and the audit trail says what went where.
    public async Task<MergeResult> MergeAsync(string keepId, string dropId, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        if (keepId == dropId)
        {
            throw new LinkbaseValidationException("Pick two different records to merge.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var kept = ChangeRecorder.Check(settings, reason);
        var nodes = await db.Nodes.Where(n => (n.Id == keepId || n.Id == dropId) && n.UserId == AppUser.DefaultId).ToListAsync(cancellationToken);
        var keep = nodes.FirstOrDefault(n => n.Id == keepId) ?? throw new LinkbaseValidationException("One of the records no longer exists.");
        var drop = nodes.FirstOrDefault(n => n.Id == dropId) ?? throw new LinkbaseValidationException("One of the records no longer exists.");
        if (keep.Type != drop.Type)
        {
            throw new LinkbaseValidationException($"A {keep.Type} and a {drop.Type} can't be merged. Both have to be the same type.");
        }

        var definition = await TypeAsync(keep.Type, cancellationToken) ?? throw new LinkbaseValidationException($"There's no type called {keep.Type}.");
        RefuseDerived(definition);

        var logged = new List<PendingChange>();
        var changes = new List<RecordChange>();

        // Values: only gaps are filled, so the kept record's own never change.
        var before = Values(keep.Props);
        var oldName = keep.Name;
        var mine = JsonNode.Parse(keep.Props) as JsonObject ?? [];
        var theirs = JsonNode.Parse(drop.Props) as JsonObject ?? [];
        var filled = 0;
        foreach (var (key, value) in theirs.ToList())
        {
            if (IsBlank(value) || definition.Fields.FirstOrDefault(f => f.Key == key)?.Owner is not null)
            {
                continue;
            }

            if (IsBlank(mine[key]))
            {
                mine[key] = value!.DeepClone();
                filled++;
            }
            else if (mine[key] is JsonArray ours && value is JsonArray extra)
            {
                foreach (var item in extra.Where(item => !ours.Any(o => JsonNode.DeepEquals(o, item))).ToList())
                {
                    ours.Add(item?.DeepClone());
                    filled++;
                }
            }
        }

        keep.Props = Hooked(definition, keep.Name, mine.ToJsonString());

        // Links: each of the dropped record's goes to the kept one unless that would double or loop one.
        string Map(string id) => id == dropId ? keepId : id;
        var dropLinks = await db.NodeLinks.Where(l => l.SourceId == dropId || l.TargetId == dropId).ToListAsync(cancellationToken);
        var keepLinks = await db.NodeLinks.AsNoTracking().Where(l => l.SourceId == keepId || l.TargetId == keepId).ToListAsync(cancellationToken);
        var otherIds = dropLinks.SelectMany(l => new[] { l.SourceId, l.TargetId }).Where(id => id != keepId && id != dropId).Distinct().ToList();
        var typeOf = await db.Nodes.AsNoTracking().Where(n => otherIds.Contains(n.Id)).Select(n => new { n.Id, n.Type }).ToDictionaryAsync(n => n.Id, n => n.Type, cancellationToken);
        string TypeOf(string id) => id == keepId || id == dropId ? keep.Type : typeOf[id];

        var present = keepLinks.Select(l => (l.SourceId, l.RelType, l.TargetId, l.Props)).ToList();
        var moved = 0;
        var dropped = 0;
        var removedCounts = new List<(string SourceType, string RelType, string TargetType)>();
        foreach (var link in dropLinks)
        {
            var (oldSource, oldTarget) = (link.SourceId, link.TargetId);
            var (source, target) = (Map(oldSource), Map(oldTarget));
            var values = Values(link.Props);
            var loops = source == target && oldSource != oldTarget;
            var repeats = present.Any(p => p.SourceId == source && p.RelType == link.RelType && p.TargetId == target && JsonNode.DeepEquals(JsonNode.Parse(p.Props), JsonNode.Parse(link.Props)));
            var (sourceType, targetType) = (TypeOf(oldSource), TypeOf(oldTarget));
            logged.Add(ChangeRecorder.LinkDeleted(oldSource, link.RelType, oldTarget, values));
            changes.AddRange(LinkChanges(RecordChangeKind.Unlinked, sourceType, oldSource, link.RelType, targetType, oldTarget, before: values));
            if (loops || repeats)
            {
                db.NodeLinks.Remove(link);
                removedCounts.Add((sourceType, link.RelType, targetType));
                dropped++;
                continue;
            }

            link.SourceId = source;
            link.TargetId = target;
            present.Add((source, link.RelType, target, link.Props));
            logged.Add(ChangeRecorder.LinkCreated(source, link.RelType, target, values));
            changes.AddRange(LinkChanges(RecordChangeKind.Linked, sourceType, source, link.RelType, targetType, target, after: values));
            moved++;
        }

        // External ids follow, so a later import of either source id still finds the one record.
        var refs = await db.ExternalRefs.Where(r => r.NodeId == dropId).ToListAsync(cancellationToken);
        foreach (var external in refs)
        {
            var sourceName = SnakeCaseEnumConverter<DataSource>.ToStore(external.Source);
            logged.Add(new PendingChange(ChangeEntityKind.ExternalRef, dropId, ChangeOp.Delete, sourceName, OldValue: external.ExternalId));
            logged.Add(new PendingChange(ChangeEntityKind.ExternalRef, keepId, ChangeOp.Create, sourceName, NewValue: external.ExternalId));
            external.NodeId = keepId;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var group in removedCounts.GroupBy(c => c))
        {
            await CountAsync(db, group.Key.SourceType, group.Key.RelType, group.Key.TargetType, -group.Count(), cancellationToken);
        }

        // The merged record may now have more of a limited kind of link than the model allows.
        foreach (var limit in definition.LinkLimits)
        {
            var count = await db.NodeLinks.CountAsync(l => l.SourceId == keepId && l.RelType == limit.RelType && l.Target.Type == limit.OtherType, cancellationToken);
            if (count > limit.Max)
            {
                throw new LinkbaseValidationException(
                    $"Merging would give {keep.Name} {count} {limit.RelType} {limit.OtherType} links, and a {keep.Type} can have at most {limit.Max}. Remove one of them first.");
            }
        }

        var after = Values(keep.Props);
        logged.AddRange(ChangeRecorder.NodeUpdated(keepId, oldName, before, keep.Name, after));
        logged.Add(ChangeRecorder.NodeDeleted(dropId, drop.Name, Values(drop.Props)));
        changes.Add(new RecordChange(RecordChangeKind.Deleted, drop.Type, dropId, Values(drop.Props), NoValues));
        if (!before.SequenceEqual(after))
        {
            changes.Add(new RecordChange(RecordChangeKind.Updated, keep.Type, keepId, before, after));
        }

        await db.Nodes.Where(n => n.Id == dropId && n.UserId == AppUser.DefaultId).ExecuteDeleteAsync(cancellationToken);
        var changeSet = settings.Record ? await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, kept, logged, cancellationToken: cancellationToken) : null;
        await transaction.CommitAsync(cancellationToken);
        using (ChangeRecorder.Causing(changeSet))
        {
            await _hooks.AfterChangeAsync(changes, cancellationToken);
        }

        return new MergeResult(moved, dropped, filled, refs.Count);
    }

    private static bool IsBlank(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonValue text when text.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
        _ => false,
    };
}
