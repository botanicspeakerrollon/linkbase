using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Records;
public sealed partial class RecordRepository
{
    // The key is made from the name when none is given, and can't change afterwards, because the record's id is made from it.
    // The record, the records it creates and all their links are saved together, so a record never exists without the links
    // its type requires.
    // The reason is kept in the audit trail, and is needed unless the model says otherwise.
    public async Task<string> CreateAsync(string type, string? key, RecordInput input, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var kept = ChangeRecorder.Check(settings, reason);
        var changes = new List<RecordChange>();
        var logged = new List<PendingChange>();
        var created = await CreateInAsync(db, type, key, input, changes, logged, cancellationToken);
        var changeSet = settings.Record ? await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, kept, logged, cancellationToken: cancellationToken) : null;
        await transaction.CommitAsync(cancellationToken);
        using (ChangeRecorder.Causing(changeSet))
        {
            await _hooks.AfterChangeAsync(changes, cancellationToken);
        }

        return created;
    }

    private async Task<string> CreateInAsync(
        LinkbaseDbContext db, string type, string? key, RecordInput input, List<RecordChange> changes, List<PendingChange> logged,
        CancellationToken cancellationToken)
    {
        var definition = await TypeAsync(type, cancellationToken) ?? throw new LinkbaseValidationException($"There's no type called {type}.");
        RefuseDerived(definition);
        var name = Name(definition, input);
        var props = Props(definition, input);
        props = Hooked(definition, name, props);

        // The records it creates go first, so they can be linked to.
        var requested = input.Links.ToList();
        foreach (var create in input.Creates)
        {
            var targetKey = await CreateInAsync(db, create.Type, create.Key, create.Input, changes, logged, cancellationToken);
            requested.Add(new NewLink(create.RelType, $"{create.Type.ToLowerInvariant()}:{targetKey}", create.Values));
        }

        var links = await NewLinksAsync(db, definition, requested, cancellationToken);
        await CheckNewHierarchyAsync(db, definition, props, links.Select(l => (l.RelType, l.TargetId, l.TargetType)).ToList(), cancellationToken);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = await FreeKeyAsync(db, type, Slug(name), cancellationToken);
        }
        else
        {
            key = key.Trim();
            if (await db.Nodes.AnyAsync(n => n.Type == type && n.Key == key, cancellationToken))
            {
                throw new LinkbaseValidationException($"There's already a {type} with the key {key}.");
            }
        }

        var id = $"{type.ToLowerInvariant()}:{key}";
        db.Nodes.Add(new Node
        {
            Id = id,
            UserId = AppUser.DefaultId,
            Type = type,
            Key = key,
            Name = name,
            Props = props,
        });
        db.NodeLinks.AddRange(links.Select(l => new NodeLink { SourceId = id, RelType = l.RelType, TargetId = l.TargetId, Props = l.Props }));
        await db.SaveChangesAsync(cancellationToken);
        foreach (var group in links.GroupBy(l => (l.RelType, l.TargetType)))
        {
            await CountAsync(db, type, group.Key.RelType, group.Key.TargetType, group.Count(), cancellationToken);
        }

        logged.Add(ChangeRecorder.NodeCreated(id, name, Values(props)));
        logged.AddRange(links.Select(l => ChangeRecorder.LinkCreated(id, l.RelType, l.TargetId, Values(l.Props))));
        changes.Add(new RecordChange(RecordChangeKind.Created, type, id, NoValues, Values(props)));
        changes.AddRange(links.SelectMany(l => LinkChanges(RecordChangeKind.Linked, type, id, l.RelType, l.TargetType, l.TargetId, after: Values(l.Props))));
        return key;
    }

    // A new record's links, checked: each goes where the model has a relationship, and every required one is there.
    private static async Task<List<(string RelType, string TargetId, string TargetType, string Props)>> NewLinksAsync(
        LinkbaseDbContext db, RecordType type, IReadOnlyList<NewLink> requested, CancellationToken cancellationToken)
    {
        var ids = requested.Select(l => l.TargetId).Distinct().ToList();
        var targets = await db.Nodes.AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.UserId == AppUser.DefaultId)
            .Select(n => new { n.Id, n.Type, n.NodeType.Owner })
            .ToListAsync(cancellationToken);
        if (targets.FirstOrDefault(t => t.Owner is not null) is { } derived)
        {
            throw Derived(derived.Type, derived.Owner!);
        }

        var targetTypes = targets.ToDictionary(t => t.Id, t => t.Type);
        var defined = await db.LinkDefinitions.AsNoTracking()
            .Where(d => d.SourceType == type.Name)
            .Select(d => new { d.RelType, d.TargetType })
            .ToListAsync(cancellationToken);

        // Each link's values are checked against its relationship's attributes, so a required one blocks the save. The same
        // two records can be linked twice only with different values.
        var links = new List<(string RelType, string TargetId, string TargetType, string Props)>();
        foreach (var link in requested)
        {
            var targetType = targetTypes.GetValueOrDefault(link.TargetId)
                ?? throw new LinkbaseValidationException("The record to link to no longer exists.");
            if (!defined.Any(d => d.RelType == link.RelType && d.TargetType == targetType))
            {
                throw new LinkbaseValidationException($"The model has no {type.Name} {link.RelType} {targetType} relationship.");
            }

            var attributes = await AttributesAsync(db, type.Name, link.RelType, targetType, cancellationToken);
            var props = Props(attributes, link.Values ?? new Dictionary<string, string?>());
            if (links.Any(l => l.RelType == link.RelType && l.TargetId == link.TargetId && JsonNode.DeepEquals(JsonNode.Parse(l.Props), JsonNode.Parse(props))))
            {
                continue;
            }

            links.Add((link.RelType, link.TargetId, targetType, props));
        }

        foreach (var limit in type.LinkLimits.Where(m => links.Count(l => l.RelType == m.RelType && l.TargetType == m.OtherType) > m.Max))
        {
            throw new LinkbaseValidationException($"A {type.Name} can have at most {limit.Max} {type.Name} {limit.RelType} {limit.OtherType} link(s).");
        }

        foreach (var needed in type.RequiredLinks.Where(r => !links.Any(l => l.RelType == r.RelType && l.TargetType == r.OtherType)))
        {
            throw new LinkbaseValidationException($"Each {type.Name} needs at least one {type.Name} {needed.RelType} {needed.OtherType} link.");
        }

        return links;
    }

    public async Task UpdateAsync(string id, RecordInput input, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var kept = ChangeRecorder.Check(settings, reason);
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id && n.UserId == AppUser.DefaultId, cancellationToken)
            ?? throw new LinkbaseValidationException("This record no longer exists.");
        var definition = await TypeAsync(node.Type, cancellationToken) ?? throw new LinkbaseValidationException($"There's no type called {node.Type}.");
        RefuseDerived(definition);

        var before = Values(node.Props);
        var oldProps = node.Props;
        var oldName = node.Name;
        node.Name = Name(definition, input);
        node.Props = Hooked(definition, node.Name, Props(definition, input, node.Props));
        await CheckKindChangeAsync(db, definition, id, oldProps, node.Props, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var after = Values(node.Props);
        var changeSet = settings.Record
            ? await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, kept, ChangeRecorder.NodeUpdated(id, oldName, before, node.Name, after).ToList(), cancellationToken: cancellationToken)
            : null;
        await transaction.CommitAsync(cancellationToken);
        using (ChangeRecorder.Causing(changeSet))
        {
            await _hooks.AfterChangeAsync([new RecordChange(RecordChangeKind.Updated, node.Type, id, before, after)], cancellationToken);
        }
    }

    // Like everything else, a record that still has links can't be deleted. Remove its links first. The links its type
    // requires can't be removed on their own, so they go with it. So do links to a module's derived records, which are the
    // module's, and its hooks hear of the delete to derive them again.
    public async Task DeleteAsync(string id, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var kept = ChangeRecorder.Check(settings, reason);
        var node = await db.Nodes.AsNoTracking()
            .Where(n => n.Id == id && n.UserId == AppUser.DefaultId)
            .Select(n => new { n.Type, n.Name, n.Props, n.NodeType.Owner })
            .SingleOrDefaultAsync(cancellationToken);
        if (node is null)
        {
            return;
        }

        if (node.Owner is not null)
        {
            throw Derived(node.Type, node.Owner);
        }

        var type = node.Type;
        var required = await db.LinkDefinitions.AsNoTracking()
            .Where(d => d.SourceType == type && d.Required && d.Target.Owner == null)
            .Select(d => new { d.RelType, d.TargetType })
            .ToListAsync(cancellationToken);
        var own = new List<(string RelType, string TargetType, int Count)>();
        foreach (var definition in required)
        {
            var count = await db.NodeLinks.CountAsync(l => l.SourceId == id && l.RelType == definition.RelType && l.Target.Type == definition.TargetType, cancellationToken);
            own.Add((definition.RelType, definition.TargetType, count));
        }

        var links = await db.NodeLinks.CountAsync(
            l => (l.SourceId == id && l.Target.NodeType.Owner == null) || (l.TargetId == id && l.Source.NodeType.Owner == null), cancellationToken)
            - own.Sum(o => o.Count);
        if (links > 0)
        {
            throw new LinkbaseValidationException($"This record still has {links:N0} link(s). Remove them first.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        long? changeSet = null;
        var logged = new List<PendingChange> { ChangeRecorder.NodeDeleted(id, node.Name, Values(node.Props)) };
        try
        {
            foreach (var (relType, targetType, _) in own)
            {
                if (settings.Record)
                {
                    var going = await db.NodeLinks.AsNoTracking()
                        .Where(l => l.SourceId == id && l.RelType == relType && l.Target.Type == targetType)
                        .Select(l => new { l.TargetId, l.Props })
                        .ToListAsync(cancellationToken);
                    logged.AddRange(going.Select(l => ChangeRecorder.LinkDeleted(id, relType, l.TargetId, Values(l.Props))));
                }

                var removed = await db.NodeLinks.Where(l => l.SourceId == id && l.RelType == relType && l.Target.Type == targetType).ExecuteDeleteAsync(cancellationToken);
                await CountAsync(db, type, relType, targetType, -removed, cancellationToken);
            }

            var derived = db.NodeLinks.Where(
                l => (l.SourceId == id && l.Target.NodeType.Owner != null) || (l.TargetId == id && l.Source.NodeType.Owner != null));
            foreach (var group in await derived.GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
                         .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
                         .ToListAsync(cancellationToken))
            {
                await CountAsync(db, group.SourceType, group.RelType, group.TargetType, -group.Count, cancellationToken);
            }

            await derived.ExecuteDeleteAsync(cancellationToken);
            if (settings.Record)
            {
                var refs = await db.ExternalRefs.AsNoTracking().Where(r => r.NodeId == id).Select(r => new { r.Source, r.ExternalId }).ToListAsync(cancellationToken);
                logged.AddRange(refs.Select(r => new PendingChange(
                    ChangeEntityKind.ExternalRef, id, ChangeOp.Delete, SnakeCaseEnumConverter<DataSource>.ToStore(r.Source), OldValue: r.ExternalId)));
            }

            await ExternalRefRepository.RemoveForNodesAsync(db, db.Nodes.Where(n => n.Id == id && n.UserId == AppUser.DefaultId).Select(n => n.Id), cancellationToken);
            await db.Nodes.Where(n => n.Id == id && n.UserId == AppUser.DefaultId).ExecuteDeleteAsync(cancellationToken);
            changeSet = settings.Record ? await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, kept, logged, cancellationToken: cancellationToken) : null;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new LinkbaseValidationException("This record was linked to something else while deleting it. Remove its links first.");
        }

        using (ChangeRecorder.Causing(changeSet))
        {
            await _hooks.AfterChangeAsync([new RecordChange(RecordChangeKind.Deleted, node.Type, id, Values(node.Props), NoValues)], cancellationToken);
        }
    }

    // Links only go where the model has a relationship, either way round, and never to a module's derived records. Values are
    // the link's attributes as a form sends them. The same two records can be linked the same way again only when an
    // attribute's value differs, so a relationship without attributes allows one link between two records.
    public async Task AddLinkAsync(
        string sourceId, string relType, string targetId, IReadOnlyDictionary<string, string?>? values = null, ChangeReason? reason = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var kept = ChangeRecorder.Check(settings, reason);
        var ends = await db.Nodes.AsNoTracking()
            .Where(n => (n.Id == sourceId || n.Id == targetId) && n.UserId == AppUser.DefaultId)
            .Select(n => new { n.Id, n.Type, n.NodeType.Owner })
            .ToListAsync(cancellationToken);
        var source = ends.FirstOrDefault(e => e.Id == sourceId) ?? throw new LinkbaseValidationException("One end of the link no longer exists.");
        var target = ends.FirstOrDefault(e => e.Id == targetId) ?? throw new LinkbaseValidationException("One end of the link no longer exists.");
        if (ends.FirstOrDefault(e => e.Owner is not null) is { } derived)
        {
            throw Derived(derived.Type, derived.Owner!);
        }

        if (!await db.LinkDefinitions.AnyAsync(l => l.SourceType == source.Type && l.RelType == relType && l.TargetType == target.Type, cancellationToken))
        {
            throw new LinkbaseValidationException($"The model has no {source.Type} {relType} {target.Type} relationship.");
        }

        await CheckLinkLimitAsync(db, sourceId, source.Type, relType, target.Type, cancellationToken);
        await CheckAddedParentAsync(db, sourceId, source.Type, relType, targetId, target.Type, cancellationToken);
        var attributes = await AttributesAsync(db, source.Type, relType, target.Type, cancellationToken);
        var props = Props(attributes, values ?? new Dictionary<string, string?>());
        await EnsureNotRepeatAsync(db, sourceId, relType, targetId, props, 0, attributes.Count > 0, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.NodeLinks.Add(new NodeLink { SourceId = sourceId, RelType = relType, TargetId = targetId, Props = props });
        await db.SaveChangesAsync(cancellationToken);
        await CountAsync(db, source.Type, relType, target.Type, 1, cancellationToken);
        var changeSet = settings.Record
            ? await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, kept, [ChangeRecorder.LinkCreated(sourceId, relType, targetId, Values(props))], cancellationToken: cancellationToken)
            : null;
        await transaction.CommitAsync(cancellationToken);
        using (ChangeRecorder.Causing(changeSet))
        {
            await _hooks.AfterChangeAsync(LinkChanges(RecordChangeKind.Linked, source.Type, sourceId, relType, target.Type, targetId, after: Values(props)), cancellationToken);
        }
    }

    // A relationship with a max stops at it: the record has to lose a link before it can have another.
    private static async Task CheckLinkLimitAsync(
        LinkbaseDbContext db, string sourceId, string sourceType, string relType, string targetType, CancellationToken cancellationToken)
    {
        var max = await db.LinkDefinitions.AsNoTracking()
            .Where(d => d.SourceType == sourceType && d.RelType == relType && d.TargetType == targetType)
            .Select(d => d.MaxLinks)
            .SingleOrDefaultAsync(cancellationToken);
        if (max is { } limit
            && await db.NodeLinks.CountAsync(l => l.SourceId == sourceId && l.RelType == relType && l.Target.Type == targetType, cancellationToken) >= limit)
        {
            throw new LinkbaseValidationException(
                $"A {sourceType} can have at most {limit} {sourceType} {relType} {targetType} link(s), and this one has {(limit == 1 ? "it" : "them")} already. Remove one first.");
        }
    }

    // Changes a link's attribute values in place. A required attribute can't be emptied, and the link can't become a repeat
    // of another between the same two records.
    public async Task UpdateLinkAsync(long linkId, IReadOnlyDictionary<string, string?> values, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var kept = ChangeRecorder.Check(settings, reason);
        var link = await db.NodeLinks.SingleOrDefaultAsync(l => l.Id == linkId, cancellationToken)
            ?? throw new LinkbaseValidationException("This link no longer exists.");
        var ends = await db.Nodes.AsNoTracking()
            .Where(n => n.Id == link.SourceId || n.Id == link.TargetId)
            .Select(n => new { n.Id, n.Type, n.NodeType.Owner })
            .ToListAsync(cancellationToken);
        if (ends.FirstOrDefault(e => e.Owner is not null) is { } derived)
        {
            throw Derived(derived.Type, derived.Owner!);
        }

        var sourceType = ends.First(e => e.Id == link.SourceId).Type;
        var targetType = ends.First(e => e.Id == link.TargetId).Type;
        var attributes = await AttributesAsync(db, sourceType, link.RelType, targetType, cancellationToken);
        if (attributes.Count == 0)
        {
            throw new LinkbaseValidationException($"A {link.RelType} link has no values to edit.");
        }

        var before = Values(link.Props);
        var props = Props(attributes, values, link.Props);
        await EnsureNotRepeatAsync(db, link.SourceId, link.RelType, link.TargetId, props, linkId, true, cancellationToken);
        link.Props = props;
        await db.SaveChangesAsync(cancellationToken);
        var after = Values(props);
        var changeSet = settings.Record
            ? await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, kept, ChangeRecorder.LinkUpdated(link.SourceId, link.RelType, link.TargetId, before, after).ToList(), cancellationToken: cancellationToken)
            : null;
        await transaction.CommitAsync(cancellationToken);
        if (!before.SequenceEqual(after))
        {
            using (ChangeRecorder.Causing(changeSet))
            {
                await _hooks.AfterChangeAsync(
                    LinkChanges(RecordChangeKind.LinkUpdated, sourceType, link.SourceId, link.RelType, targetType, link.TargetId, before, after), cancellationToken);
            }
        }
    }

    // A link is a repeat of another between the same two records when every attribute value is the same.
    private static async Task EnsureNotRepeatAsync(
        LinkbaseDbContext db, string sourceId, string relType, string targetId, string props, long exceptLinkId, bool hasAttributes, CancellationToken cancellationToken)
    {
        var others = await db.NodeLinks.AsNoTracking()
            .Where(l => l.SourceId == sourceId && l.RelType == relType && l.TargetId == targetId && l.Id != exceptLinkId)
            .Select(l => l.Props)
            .ToListAsync(cancellationToken);
        var mine = JsonNode.Parse(props);
        if (others.Any(other => JsonNode.DeepEquals(JsonNode.Parse(other), mine)))
        {
            throw new LinkbaseValidationException(hasAttributes
                ? "Those two are already linked that way with the same values. To link them again, change at least one value."
                : "Those two are already linked that way.");
        }
    }

    public async Task RemoveLinkAsync(long linkId, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var kept = ChangeRecorder.Check(settings, reason);
        var link = await db.NodeLinks.AsNoTracking()
            .Where(l => l.Id == linkId)
            .Select(l => new
            {
                l.SourceId,
                SourceName = l.Source.Name,
                SourceType = l.Source.Type,
                SourceOwner = l.Source.NodeType.Owner,
                l.RelType,
                l.TargetId,
                TargetType = l.Target.Type,
                TargetOwner = l.Target.NodeType.Owner,
                l.Props,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (link is null)
        {
            return;
        }

        if (link.SourceOwner is not null || link.TargetOwner is not null)
        {
            throw link.SourceOwner is not null ? Derived(link.SourceType, link.SourceOwner) : Derived(link.TargetType, link.TargetOwner!);
        }

        // The last of a required kind of link has to stay. Deleting the record takes it too.
        if (await db.LinkDefinitions.AnyAsync(d => d.SourceType == link.SourceType && d.RelType == link.RelType && d.TargetType == link.TargetType && d.Required, cancellationToken)
            && !await db.NodeLinks.AnyAsync(l => l.SourceId == link.SourceId && l.RelType == link.RelType && l.Target.Type == link.TargetType && l.Id != linkId, cancellationToken))
        {
            throw new LinkbaseValidationException(
                $"Each {link.SourceType} needs at least one {link.SourceType} {link.RelType} {link.TargetType} link, and this is {link.SourceName}'s only one. Add another first.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var removed = await db.NodeLinks.Where(l => l.Id == linkId).ExecuteDeleteAsync(cancellationToken);
        await CountAsync(db, link.SourceType, link.RelType, link.TargetType, -removed, cancellationToken);
        var changeSet = settings.Record && removed > 0
            ? await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, kept, [ChangeRecorder.LinkDeleted(link.SourceId, link.RelType, link.TargetId, Values(link.Props))], cancellationToken: cancellationToken)
            : null;
        await transaction.CommitAsync(cancellationToken);
        if (removed > 0)
        {
            using (ChangeRecorder.Causing(changeSet))
            {
                await _hooks.AfterChangeAsync(
                    LinkChanges(RecordChangeKind.Unlinked, link.SourceType, link.SourceId, link.RelType, link.TargetType, link.TargetId, before: Values(link.Props)), cancellationToken);
            }
        }
    }
}
