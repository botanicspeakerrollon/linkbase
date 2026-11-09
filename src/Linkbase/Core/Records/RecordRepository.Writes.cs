using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task<string> CreateAsync(string type, string? key, RecordInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changes = new List<RecordChange>();
        var created = await CreateInAsync(db, type, key, input, changes, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _hooks.AfterChangeAsync(changes, cancellationToken);
        return created;
    }

    private async Task<string> CreateInAsync(
        LinkbaseDbContext db, string type, string? key, RecordInput input, List<RecordChange> changes, CancellationToken cancellationToken)
    {
        var definition = await TypeAsync(type, cancellationToken) ?? throw new LinkbaseValidationException($"There's no type called {type}.");
        RefuseDerived(definition);
        var name = Name(input);
        var props = Props(definition, input);
        props = Hooked(definition, name, props);

        // The records it creates go first, so they can be linked to.
        var requested = input.Links.ToList();
        foreach (var create in input.Creates)
        {
            var targetKey = await CreateInAsync(db, create.Type, create.Key, create.Input, changes, cancellationToken);
            requested.Add(new NewLink(create.RelType, $"{create.Type.ToLowerInvariant()}:{targetKey}"));
        }

        var links = await NewLinksAsync(db, definition, requested, cancellationToken);
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
        db.NodeLinks.AddRange(links.Select(l => new NodeLink { SourceId = id, RelType = l.RelType, TargetId = l.TargetId }));
        await db.SaveChangesAsync(cancellationToken);
        foreach (var group in links.GroupBy(l => (l.RelType, l.TargetType)))
        {
            await CountAsync(db, type, group.Key.RelType, group.Key.TargetType, group.Count(), cancellationToken);
        }

        changes.Add(new RecordChange(RecordChangeKind.Created, type, id, NoValues, Values(props)));
        changes.AddRange(links.SelectMany(l => LinkChanges(RecordChangeKind.Linked, type, id, l.RelType, l.TargetType, l.TargetId)));
        return key;
    }

    // A new record's links, checked: each goes where the model has a relationship, and every required one is there.
    private static async Task<List<(string RelType, string TargetId, string TargetType)>> NewLinksAsync(
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

        var links = new List<(string RelType, string TargetId, string TargetType)>();
        foreach (var link in requested.Distinct())
        {
            var targetType = targetTypes.GetValueOrDefault(link.TargetId)
                ?? throw new LinkbaseValidationException("The record to link to no longer exists.");
            if (!defined.Any(d => d.RelType == link.RelType && d.TargetType == targetType))
            {
                throw new LinkbaseValidationException($"The model has no {type.Name} {link.RelType} {targetType} relationship.");
            }

            links.Add((link.RelType, link.TargetId, targetType));
        }

        foreach (var needed in type.RequiredLinks.Where(r => !links.Any(l => l.RelType == r.RelType && l.TargetType == r.OtherType)))
        {
            throw new LinkbaseValidationException($"Each {type.Name} needs at least one {type.Name} {needed.RelType} {needed.OtherType} link.");
        }

        return links;
    }

    public async Task UpdateAsync(string id, RecordInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == id && n.UserId == AppUser.DefaultId, cancellationToken)
            ?? throw new LinkbaseValidationException("This record no longer exists.");
        var definition = await TypeAsync(node.Type, cancellationToken) ?? throw new LinkbaseValidationException($"There's no type called {node.Type}.");
        RefuseDerived(definition);

        var before = Values(node.Props);
        node.Name = Name(input);
        node.Props = Hooked(definition, node.Name, Props(definition, input, node.Props));
        await db.SaveChangesAsync(cancellationToken);
        await _hooks.AfterChangeAsync([new RecordChange(RecordChangeKind.Updated, node.Type, id, before, Values(node.Props))], cancellationToken);
    }

    // Like everything else, a record that still has links can't be deleted. Remove its links first. The links its type
    // requires can't be removed on their own, so they go with it. So do links to a module's derived records, which are the
    // module's, and its hooks hear of the delete to derive them again.
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.AsNoTracking()
            .Where(n => n.Id == id && n.UserId == AppUser.DefaultId)
            .Select(n => new { n.Type, n.Props, n.NodeType.Owner })
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
        try
        {
            foreach (var (relType, targetType, _) in own)
            {
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
            await ExternalRefRepository.RemoveForNodesAsync(db, db.Nodes.Where(n => n.Id == id && n.UserId == AppUser.DefaultId).Select(n => n.Id), cancellationToken);
            await db.Nodes.Where(n => n.Id == id && n.UserId == AppUser.DefaultId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new LinkbaseValidationException("This record was linked to something else while deleting it. Remove its links first.");
        }

        await _hooks.AfterChangeAsync([new RecordChange(RecordChangeKind.Deleted, node.Type, id, Values(node.Props), NoValues)], cancellationToken);
    }

    // Links only go where the model has a relationship, either way round, and never to a module's derived records.
    public async Task AddLinkAsync(string sourceId, string relType, string targetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
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

        if (await db.NodeLinks.AnyAsync(l => l.SourceId == sourceId && l.RelType == relType && l.TargetId == targetId, cancellationToken))
        {
            throw new LinkbaseValidationException("Those two are already linked that way.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.NodeLinks.Add(new NodeLink { SourceId = sourceId, RelType = relType, TargetId = targetId });
        await db.SaveChangesAsync(cancellationToken);
        await CountAsync(db, source.Type, relType, target.Type, 1, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await _hooks.AfterChangeAsync(LinkChanges(RecordChangeKind.Linked, source.Type, sourceId, relType, target.Type, targetId), cancellationToken);
    }

    public async Task RemoveLinkAsync(long linkId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
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
        await transaction.CommitAsync(cancellationToken);
        if (removed > 0)
        {
            await _hooks.AfterChangeAsync(
                LinkChanges(RecordChangeKind.Unlinked, link.SourceType, link.SourceId, link.RelType, link.TargetType, link.TargetId), cancellationToken);
        }
    }
}
