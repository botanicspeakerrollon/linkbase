using Linkbase.Core.History;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Models;

// Every write here runs inside WriteAsync (ModelEditor.cs), which writes the audit trail through ChangeRecorder in the
// same transaction.
public sealed partial class ModelEditor
{
    public async Task AddRelationshipAsync(
        string from, string rel, string to, bool required = false, int? max = null, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        rel = rel.Trim();
        CheckRelType(rel);
        CheckMax(max);
        await WriteAsync(reason, async (db, changes) =>
        {
            await EditableTypeAsync(db, from, cancellationToken);
            await EditableTypeAsync(db, to, cancellationToken);
            if (await db.LinkDefinitions.AnyAsync(l => l.SourceType == from && l.RelType == rel && l.TargetType == to, cancellationToken))
            {
                throw new LinkbaseValidationException($"There is already a relationship {from} {rel} {to}.");
            }

            if (required && await db.Nodes.AnyAsync(n => n.Type == from, cancellationToken))
            {
                throw new LinkbaseValidationException($"{from} already has records with no {rel} link, so this can't start out required. Add it optional, link them, then make it required.");
            }

            await ExecuteAsync(db, "INSERT INTO link_definition (source_type, rel_type, target_type, required, max_links) VALUES (@from, @rel, @to, @required, @max)",
                [Text("from", from), Text("rel", rel), Text("to", to), new Param("required", required, NpgsqlDbType.Boolean), new Param("max", max, NpgsqlDbType.Integer)], cancellationToken);
            changes.Add(Defined(RelEntity(from, rel, to), ChangeOp.Create, to: Json(new { from, rel, to, required, max })));
        }, cancellationToken);
    }

    // Renames a relationship. Every link of it keeps its ends and values; only its name changes.
    public async Task RenameRelationshipAsync(string from, string rel, string to, string newRel, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        newRel = newRel.Trim();
        CheckRelType(newRel);
        await WriteAsync(reason, async (db, changes) =>
        {
            await RelationshipAsync(db, from, rel, to, cancellationToken);
            if (newRel == rel)
            {
                throw new LinkbaseValidationException("That relationship already has that name.");
            }

            if (await db.LinkDefinitions.AnyAsync(l => l.SourceType == from && l.RelType == newRel && l.TargetType == to, cancellationToken))
            {
                throw new LinkbaseValidationException($"There is already a relationship {from} {newRel} {to}.");
            }

            await ExecuteAsync(db, "UPDATE link_definition SET rel_type = @newRel WHERE source_type = @from AND rel_type = @rel AND target_type = @to",
                [Text("from", from), Text("rel", rel), Text("to", to), Text("newRel", newRel)], cancellationToken);
            await ExecuteAsync(db, "UPDATE node_link SET rel_type = @newRel WHERE rel_type = @rel AND source_id IN (SELECT id FROM node WHERE type = @from) AND target_id IN (SELECT id FROM node WHERE type = @to)",
                [Text("from", from), Text("rel", rel), Text("to", to), Text("newRel", newRel)], cancellationToken);
            if (from == to)
            {
                await ExecuteAsync(db, "UPDATE node_type SET hierarchy_rel = @newRel WHERE name = @from AND hierarchy_rel = @rel",
                    [Text("from", from), Text("rel", rel), Text("newRel", newRel)], cancellationToken);
            }

            changes.Add(Defined(RelEntity(from, rel, to), ChangeOp.Update, "rel_type", rel, newRel));
        }, cancellationToken);
    }

    // Turns a relationship required, or takes that away; sets or clears its max links per record.
    public async Task SetRelationshipRulesAsync(string from, string rel, string to, bool required, int? max, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        CheckMax(max);
        await WriteAsync(reason, async (db, changes) =>
        {
            var current = await RelationshipAsync(db, from, rel, to, cancellationToken);
            RefuseWatched(from, $"change {from} {rel} {to}");
            RefuseWatched(to, $"change {from} {rel} {to}");
            if (required && !current.Required
                && await CountAsync(db, "SELECT count(*) FROM node n WHERE n.type = @from AND NOT EXISTS (SELECT 1 FROM node_link l JOIN node t ON t.id = l.target_id WHERE l.source_id = n.id AND l.rel_type = @rel AND t.type = @to)",
                    [Text("from", from), Text("rel", rel), Text("to", to)], cancellationToken) is > 0 and var missing)
            {
                throw new LinkbaseValidationException($"{missing:N0} {from} record(s) have no {rel} {to} link, so this can't be required yet. Link them first.");
            }

            if (max is not null
                && await CountAsync(db,
                    """
                    SELECT count(*) FROM (
                        SELECT n.id FROM node n JOIN node_link l ON l.source_id = n.id AND l.rel_type = @rel JOIN node t ON t.id = l.target_id AND t.type = @to
                        WHERE n.type = @from GROUP BY n.id HAVING count(*) > @max
                    ) s
                    """, [Text("from", from), Text("rel", rel), Text("to", to), new Param("max", max, NpgsqlDbType.Integer)], cancellationToken) is > 0 and var over)
            {
                throw new LinkbaseValidationException($"{over:N0} {from} record(s) already have more than {max} {rel} {to} link(s). Remove some first.");
            }

            Change(changes, RelEntity(from, rel, to), "required", current.Required.ToString().ToLowerInvariant(), required.ToString().ToLowerInvariant());
            Change(changes, RelEntity(from, rel, to), "max_links", current.MaxLinks?.ToString(System.Globalization.CultureInfo.InvariantCulture), max?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await ExecuteAsync(db, "UPDATE link_definition SET required = @required, max_links = @max WHERE source_type = @from AND rel_type = @rel AND target_type = @to",
                [Text("from", from), Text("rel", rel), Text("to", to), new Param("required", required, NpgsqlDbType.Boolean), new Param("max", max, NpgsqlDbType.Integer)], cancellationToken);
        }, cancellationToken);
    }

    // Removes a relationship that has no links. Links carry history as the record's own, so the relationship is cleared
    // of them first, by hand, rather than deleting them here.
    public async Task DeleteRelationshipAsync(string from, string rel, string to, ChangeReason? reason = null, CancellationToken cancellationToken = default)
    {
        await WriteAsync(reason, async (db, changes) =>
        {
            await RelationshipAsync(db, from, rel, to, cancellationToken);
            RefuseWatched(from, $"delete {from} {rel} {to}");
            RefuseWatched(to, $"delete {from} {rel} {to}");
            if (from == to && await db.NodeTypes.AsNoTracking().AnyAsync(t => t.Name == from && t.HierarchyRel == rel, cancellationToken))
            {
                throw new LinkbaseValidationException($"{from}'s hierarchy goes via {rel}. Take the hierarchy away first.");
            }

            if (await db.NodeLinks.AnyAsync(l => l.RelType == rel && l.Source.Type == from && l.Target.Type == to, cancellationToken))
            {
                throw new LinkbaseValidationException($"{from} {rel} {to} still has links. Remove them first.");
            }

            await ExecuteAsync(db, "DELETE FROM link_attribute WHERE link_definition_id = (SELECT id FROM link_definition WHERE source_type = @from AND rel_type = @rel AND target_type = @to)",
                [Text("from", from), Text("rel", rel), Text("to", to)], cancellationToken);
            await ExecuteAsync(db, "DELETE FROM link_definition WHERE source_type = @from AND rel_type = @rel AND target_type = @to",
                [Text("from", from), Text("rel", rel), Text("to", to)], cancellationToken);
            changes.Add(Defined(RelEntity(from, rel, to), ChangeOp.Delete, from: Json(new { from, rel, to })));
        }, cancellationToken);
    }

    private static string RelEntity(string from, string rel, string to) => $"relationship:{from} {rel} {to}";

    private static void CheckRelType(string rel)
    {
        if (!ModelFile.RelType().IsMatch(rel))
        {
            throw new LinkbaseValidationException("A relationship's name is capitals, digits and underscores, such as SUPPLIES.");
        }
    }

    private static void CheckMax(int? max)
    {
        if (max is < 1)
        {
            throw new LinkbaseValidationException("A max of 1 or more, or leave it with no limit.");
        }
    }

    private static async Task<LinkDefinition> RelationshipAsync(LinkbaseDbContext db, string from, string rel, string to, CancellationToken cancellationToken) =>
        await db.LinkDefinitions.AsNoTracking().SingleOrDefaultAsync(l => l.SourceType == from && l.RelType == rel && l.TargetType == to, cancellationToken)
        ?? throw new LinkbaseValidationException($"There is no relationship {from} {rel} {to}.");
}
