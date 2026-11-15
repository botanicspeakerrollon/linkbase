using System.Text.Json;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.History;

// Puts back what one edit changed, from the old values the audit trail kept. A revert is itself an edit: it goes through the
// same repository methods, so it is checked, logged with the reason "Reverted change #n", and runs the modules' hooks.
//
// It only goes ahead when things are still as the edit left them. If a value has been changed again since, or the record or
// link has gone, it stops and says what is different, so a revert never overwrites later work. A change that touched several
// records can't be reverted this way. Imports are undone from the imports page, and a module's derived records follow from the
// change that caused them. See docs/history/audit-history-plan.md.
public sealed class ChangeReverter(IDbContextFactory<LinkbaseDbContext> contextFactory, IRecordRepository records)
{
    private const int NoteShown = 400;

    // Returns notes about anything it couldn't put back, such as the ids a deleted record had in other systems.
    public async Task<IReadOnlyList<string>> RevertAsync(long changeSetId, string? note = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var set = await db.ChangeSets.AsNoTracking().Where(s => s.Id == changeSetId)
            .Select(s => new { s.Justification.Kind }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new LinkbaseValidationException("There's no such change.");
        if (set.Kind != JustificationKind.Manual)
        {
            throw new LinkbaseValidationException(set.Kind switch
            {
                JustificationKind.Import => "An import is undone from the imports page.",
                JustificationKind.Derived => "A derived record follows from the change that caused it, so revert that change instead.",
                _ => "A correction can't be reverted here.",
            });
        }

        var changes = await db.Changes.AsNoTracking().Where(c => c.ChangeSetId == changeSetId).OrderBy(c => c.Id).ToListAsync(cancellationToken);
        var reason = new ChangeReason($"Reverted change #{changeSetId}" + (string.IsNullOrWhiteSpace(note) ? "" : $": {Clip(note.Trim())}"));
        var nodes = changes.Where(c => c.EntityKind == ChangeEntityKind.Node).ToList();
        var links = changes.Where(c => c.EntityKind == ChangeEntityKind.Link).ToList();
        var refs = changes.Where(c => c.EntityKind == ChangeEntityKind.ExternalRef).ToList();
        if (changes.Count == 0 || changes.Any(c => c.EntityKind is ChangeEntityKind.Definition or ChangeEntityKind.ImportBatch or ChangeEntityKind.Label or ChangeEntityKind.PropertyLabel))
        {
            throw new LinkbaseValidationException("This change can't be reverted automatically.");
        }

        var several = new LinkbaseValidationException("This change touched several records, so it can't be reverted automatically.");
        var updates = nodes.Where(n => n.Op == ChangeOp.Update).ToList();
        var created = nodes.Where(n => n.Op == ChangeOp.Create).ToList();
        var deleted = nodes.Where(n => n.Op == ChangeOp.Delete).ToList();
        if (updates.Count > 0)
        {
            if (created.Count + deleted.Count + links.Count + refs.Count > 0 || updates.Select(u => u.EntityId).Distinct().Count() > 1)
            {
                throw several;
            }

            await RevertUpdateAsync(db, updates, reason, cancellationToken);
        }
        else if (created.Count == 1)
        {
            var id = created[0].EntityId;
            if (deleted.Count + refs.Count > 0 || links.Any(l => l.Op != ChangeOp.Create || l.EntityId != id))
            {
                throw several;
            }

            await RevertCreateAsync(db, created[0], reason, cancellationToken);
        }
        else if (deleted.Count == 1)
        {
            var id = deleted[0].EntityId;
            if (created.Count > 0 || links.Any(l => l.Op != ChangeOp.Delete || l.EntityId != id))
            {
                throw several;
            }

            return await RevertDeleteAsync(db, deleted[0], links, refs, reason, cancellationToken);
        }
        else if (nodes.Count == 0 && refs.Count == 0 && links.Count > 0)
        {
            if (links.Select(l => (l.EntityId, l.RelType, l.RelatedId)).Distinct().Count() > 1)
            {
                throw several;
            }

            await RevertLinkAsync(db, links, reason, cancellationToken);
        }
        else
        {
            throw several;
        }

        return [];
    }

    private static string Clip(string note) => note.Length <= NoteShown ? note : note[..NoteShown];

    private static bool Same(string? a, string? b) => (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) || a == b;

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "empty" : $"'{value.Replace('\n', ',')}'";

    private static bool SameValues(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string?> b) =>
        a.Keys.Union(b.Keys).All(k => Same(a.GetValueOrDefault(k), b.GetValueOrDefault(k)));

    // Values in the trail's JSON: a link's are a plain object, a record's are {name, values}.
    private static Dictionary<string, string?> LinkValues(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];

    private static (string Name, Dictionary<string, string?> Values) Snapshot(string? json)
    {
        using var document = JsonDocument.Parse(json ?? "{}");
        var root = document.RootElement;
        var values = root.TryGetProperty("values", out var found) && found.ValueKind == JsonValueKind.Object
            ? found.EnumerateObject().ToDictionary(p => p.Name, p => (string?)p.Value.GetString())
            : [];
        return (root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "", values);
    }

    private static async Task<(string Type, string Key, string Name, string Props)?> NodeAsync(LinkbaseDbContext db, string id, CancellationToken cancellationToken)
    {
        var node = await db.Nodes.AsNoTracking().Where(n => n.Id == id).Select(n => new { n.Type, n.Key, n.Name, n.Props }).SingleOrDefaultAsync(cancellationToken);
        return node is null ? null : (node.Type, node.Key, node.Name, node.Props);
    }

    private async Task RevertUpdateAsync(LinkbaseDbContext db, List<Change> updates, ChangeReason reason, CancellationToken cancellationToken)
    {
        var id = updates[0].EntityId;
        var node = await NodeAsync(db, id, cancellationToken) ?? throw new LinkbaseValidationException("This record no longer exists.");
        var values = RecordRepository.Values(node.Props).ToDictionary(v => v.Key, v => (string?)v.Value);
        var name = node.Name;
        var changed = false;
        var conflicts = new List<string>();
        foreach (var update in updates)
        {
            var key = update.FieldKey ?? "name";
            var now = key == "name" ? name : values.GetValueOrDefault(key);
            if (Same(now, update.NewValue))
            {
                if (key == "name")
                {
                    name = update.OldValue ?? name;
                }
                else
                {
                    values[key] = update.OldValue;
                }

                changed = true;
            }
            else if (!Same(now, update.OldValue))
            {
                conflicts.Add($"{RecordRepository.Title(key)} is now {Show(now)}");
            }
        }

        if (conflicts.Count > 0)
        {
            throw new LinkbaseValidationException($"This can't be reverted, because it was changed again since: {string.Join("; ", conflicts)}.");
        }

        if (!changed)
        {
            throw new LinkbaseValidationException("There's nothing to revert: those values are already back.");
        }

        await records.UpdateAsync(id, new RecordInput(name, values), reason, cancellationToken);
    }

    // A record that was created goes again, as long as nothing has been done to it since.
    private async Task RevertCreateAsync(LinkbaseDbContext db, Change created, ChangeReason reason, CancellationToken cancellationToken)
    {
        var node = await NodeAsync(db, created.EntityId, cancellationToken) ?? throw new LinkbaseValidationException("This record is already gone.");
        var (name, values) = Snapshot(created.NewValue);
        if (node.Name != name || !SameValues(RecordRepository.Values(node.Props), values))
        {
            throw new LinkbaseValidationException("This can't be reverted, because the record was edited since it was created.");
        }

        await records.DeleteAsync(created.EntityId, reason, cancellationToken);
    }

    // A record that was deleted comes back with the values and links it had, as a new record with the same id.
    private async Task<IReadOnlyList<string>> RevertDeleteAsync(
        LinkbaseDbContext db, Change deleted, List<Change> links, List<Change> refs, ChangeReason reason, CancellationToken cancellationToken)
    {
        if (await NodeAsync(db, deleted.EntityId, cancellationToken) is not null)
        {
            throw new LinkbaseValidationException("There's a record with that id again already.");
        }

        var colon = deleted.EntityId.IndexOf(':');
        var prefix = deleted.EntityId[..colon];
        var type = await db.NodeTypes.AsNoTracking().Where(t => t.Name.ToLower() == prefix).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken)
            ?? throw new LinkbaseValidationException($"The model has no {prefix} type any more.");
        var (name, values) = Snapshot(deleted.OldValue);
        var input = new RecordInput(name, values)
        {
            Links = links.Select(l => new NewLink(l.RelType!, l.RelatedId!, LinkValues(l.OldValue))).ToList(),
        };
        await records.CreateAsync(type, deleted.EntityId[(colon + 1)..], input, reason, cancellationToken);
        return refs.Count == 0 ? [] : [$"Its ids in other systems ({string.Join(", ", refs.Select(r => r.FieldKey).Distinct())}) weren't put back."];
    }

    private async Task RevertLinkAsync(LinkbaseDbContext db, List<Change> changes, ChangeReason reason, CancellationToken cancellationToken)
    {
        var first = changes[0];
        var (source, rel, target) = (first.EntityId, first.RelType!, first.RelatedId!);
        if (first.Op == ChangeOp.Delete)
        {
            if (changes.Count > 1)
            {
                throw new LinkbaseValidationException("This change touched several links, so it can't be reverted automatically.");
            }

            await records.AddLinkAsync(source, rel, target, LinkValues(first.OldValue), reason, cancellationToken);
            return;
        }

        var there = (await db.NodeLinks.AsNoTracking().Where(l => l.SourceId == source && l.RelType == rel && l.TargetId == target).Select(l => new { l.Id, l.Props }).ToListAsync(cancellationToken))
            .Select(l => (l.Id, Values: RecordRepository.Values(l.Props)))
            .ToList();
        if (first.Op == ChangeOp.Create)
        {
            var expected = LinkValues(first.NewValue);
            var match = there.FirstOrDefault(l => SameValues(l.Values, expected));
            if (match.Id == 0)
            {
                throw new LinkbaseValidationException("This link is already gone, or its values were changed since.");
            }

            await records.RemoveLinkAsync(match.Id, reason, cancellationToken);
            return;
        }

        var edited = there.Where(l => changes.All(c => Same(l.Values.GetValueOrDefault(c.FieldKey ?? ""), c.NewValue))).ToList();
        if (edited.Count != 1)
        {
            throw new LinkbaseValidationException(edited.Count == 0 && there.Any(l => changes.All(c => Same(l.Values.GetValueOrDefault(c.FieldKey ?? ""), c.OldValue)))
                ? "There's nothing to revert: those values are already back."
                : "This can't be reverted, because the link was changed again since, or can't be told from another one.");
        }

        var values = edited[0].Values.ToDictionary(v => v.Key, v => (string?)v.Value);
        foreach (var change in changes)
        {
            values[change.FieldKey ?? ""] = change.OldValue;
        }

        await records.UpdateLinkAsync(edited[0].Id, values, reason, cancellationToken);
    }
}
