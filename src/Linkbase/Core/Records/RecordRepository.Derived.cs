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
    // Replaces every record of a type a domain module derives, and every link to them, in one transaction. Only the
    // module that owns the type can, and no hooks run. Each link needs one end among the new records, and a relationship
    // the model has. Returns how many records there are now.
    public async Task<int> ReplaceDerivedAsync(
        string domain, string type, IReadOnlyList<DerivedRecord> records, IReadOnlyList<DerivedLink> links, CancellationToken cancellationToken = default)
    {
        var definition = await TypeAsync(type, cancellationToken) ?? throw new InvalidOperationException($"There's no type called {type}.");
        if (definition.Owner != domain)
        {
            throw new InvalidOperationException($"{type} isn't derived by {domain}, so it can't replace them.");
        }

        var prefix = $"{type.ToLowerInvariant()}:";
        var nodes = records.Select(r => new Node
        {
            Id = prefix + r.Key,
            UserId = AppUser.DefaultId,
            Type = type,
            Key = r.Key,
            Name = Name(new RecordInput(r.Name, r.Values)),
            Props = Props(definition, new RecordInput(r.Name, r.Values), includeKept: true),
        }).ToList();
        if (nodes.GroupBy(n => n.Key).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new InvalidOperationException($"{type} {repeated.Key} is there more than once.");
        }

        var ids = nodes.Select(n => n.Id).ToHashSet();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var others = links.SelectMany(l => new[] { l.SourceId, l.TargetId }).Where(id => !ids.Contains(id)).Distinct().ToList();
        var types = await db.Nodes.AsNoTracking().Where(n => others.Contains(n.Id)).Select(n => new { n.Id, n.Type }).ToDictionaryAsync(n => n.Id, n => n.Type, cancellationToken);
        foreach (var node in nodes)
        {
            types[node.Id] = type;
        }

        var relationships = (await db.LinkDefinitions.AsNoTracking().Select(d => new { d.SourceType, d.RelType, d.TargetType }).ToListAsync(cancellationToken))
            .Select(d => (d.SourceType, d.RelType, d.TargetType)).ToHashSet();
        foreach (var link in links)
        {
            if (!ids.Contains(link.SourceId) && !ids.Contains(link.TargetId))
            {
                throw new InvalidOperationException($"The link {link.SourceId} {link.RelType} {link.TargetId} has neither end among the {type} records.");
            }

            if (!types.TryGetValue(link.SourceId, out var from) || !types.TryGetValue(link.TargetId, out var to))
            {
                throw new InvalidOperationException($"The link {link.SourceId} {link.RelType} {link.TargetId} has an end that isn't there.");
            }

            if (!relationships.Contains((from, link.RelType, to)))
            {
                throw new InvalidOperationException($"The model has no {from} {link.RelType} {to} relationship.");
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // What the rebuild changes, for the audit trail: records that came, went or differ. The links are the module's own
        // and follow from the records, so they aren't listed.
        var settings = await ChangeRecorder.SettingsAsync(db, cancellationToken);
        var logged = new List<PendingChange>();
        if (settings.Record)
        {
            var existing = await db.Nodes.AsNoTracking().Where(n => n.Type == type).Select(n => new { n.Id, n.Name, n.Props }).ToDictionaryAsync(n => n.Id, cancellationToken);
            foreach (var node in nodes)
            {
                if (existing.Remove(node.Id, out var was))
                {
                    logged.AddRange(ChangeRecorder.NodeUpdated(node.Id, was.Name, Values(was.Props), node.Name, Values(node.Props)));
                }
                else
                {
                    logged.Add(ChangeRecorder.NodeCreated(node.Id, node.Name, Values(node.Props)));
                }
            }

            logged.AddRange(existing.Values.Select(gone => ChangeRecorder.NodeDeleted(gone.Id, gone.Name, Values(gone.Props))));
        }

        var old = db.NodeLinks.Where(l => l.Source.Type == type || l.Target.Type == type);
        foreach (var group in await old.GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
                     .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
                     .ToListAsync(cancellationToken))
        {
            await CountAsync(db, group.SourceType, group.RelType, group.TargetType, -group.Count, cancellationToken);
        }

        await old.ExecuteDeleteAsync(cancellationToken);

        // A record that comes back keeps its refs, because its id is the same. The others' refs go with them.
        var kept = ids.ToList();
        await ExternalRefRepository.RemoveForNodesAsync(db, db.Nodes.Where(n => n.Type == type && !kept.Contains(n.Id)).Select(n => n.Id), cancellationToken);
        await db.Nodes.Where(n => n.Type == type).ExecuteDeleteAsync(cancellationToken);
        db.Nodes.AddRange(nodes);
        db.NodeLinks.AddRange(links.Distinct().Select(l => new NodeLink
        {
            SourceId = l.SourceId,
            RelType = l.RelType,
            TargetId = l.TargetId,
            Props = l.Props?.ToJsonString() ?? "{}",
        }));
        await db.SaveChangesAsync(cancellationToken);
        foreach (var group in links.Distinct().GroupBy(l => (From: types[l.SourceId], l.RelType, To: types[l.TargetId])))
        {
            await CountAsync(db, group.Key.From, group.Key.RelType, group.Key.To, group.Count(), cancellationToken);
        }

        if (settings.Record)
        {
            await ChangeRecorder.WriteAsync(db, JustificationKind.Derived, new ChangeReason($"{domain} rebuilt its {type} records"), logged,
                causeChangeSetId: ChangeRecorder.Cause, cancellationToken: cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return nodes.Count;
    }

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private static List<RecordChange> LinkChanges(
        RecordChangeKind kind, string sourceType, string sourceId, string relType, string targetType, string targetId,
        IReadOnlyDictionary<string, string>? before = null, IReadOnlyDictionary<string, string>? after = null) =>
    [
        new(kind, sourceType, sourceId, before ?? NoValues, after ?? NoValues, relType, targetId),
        new(kind, targetType, targetId, before ?? NoValues, after ?? NoValues, relType, sourceId),
    ];

    private static void RefuseDerived(RecordType type)
    {
        if (type.Owner is not null)
        {
            throw Derived(type.Name, type.Owner);
        }
    }

    private static LinkbaseValidationException Derived(string type, string owner) =>
        new($"{type} records are derived by {owner} from other records, so they can't be changed here.");
}
