using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Cleanup;

public sealed record DuplicateRecord(string Id, string Key, string Name, int Links, DateTimeOffset UpdatedAt);

// Records of one type whose names match once case and spacing are ignored.
public sealed record DuplicateGroup(string Type, string Name, IReadOnlyList<DuplicateRecord> Records);

public sealed record DuplicateGroups(IReadOnlyList<DuplicateGroup> Groups, bool More);

public sealed record CleanupRecord(string Id, string Type, string Key, string Name);

// What is wrong with a record that has something missing, such as "No title" or "No Project WORKS_ON link".
public sealed record CleanupIssue(string Id, string Type, string Key, string Name, string Problem);

public sealed record CleanupList<T>(IReadOnlyList<T> Items, bool More);

// Finds records worth a second look. Import review protects the way in; this is for what is already there. It only looks at
// types people keep: a module's derived records are rebuilt from the others, so tidying them by hand would not last.
public sealed class CleanupRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public const int GroupLimit = 50;
    public const int ItemLimit = 200;

    // Names that differ only in case or spacing, biggest groups first. Type narrows it to one.
    public async Task<DuplicateGroups> DuplicatesAsync(string? type, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var kept = await KeptTypesAsync(db, type, cancellationToken);
        var user = AppUser.DefaultId;
        var limit = GroupLimit + 1;
        var rows = await db.Database.SqlQuery<DuplicateRow>(
            $"""
            SELECT n.type AS "Type", n.id AS "Id", n.key AS "Key", n.name AS "Name", n.updated_at AS "UpdatedAt",
                   (SELECT count(*) FROM node_link l WHERE l.source_id = n.id OR l.target_id = n.id)::int AS "Links",
                   d.norm AS "Norm"
            FROM (
                SELECT type, lower(regexp_replace(btrim(name), '\s+', ' ', 'g')) AS norm
                FROM node
                WHERE user_id = {user} AND type = ANY({kept})
                GROUP BY 1, 2
                HAVING count(*) > 1
                ORDER BY count(*) DESC, 1, 2
                LIMIT {limit}
            ) d
            JOIN node n ON n.type = d.type AND lower(regexp_replace(btrim(n.name), '\s+', ' ', 'g')) = d.norm AND n.user_id = {user}
            ORDER BY d.type, d.norm, n.created_at, n.id
            """).ToListAsync(cancellationToken);

        var groups = rows
            .GroupBy(r => (r.Type, r.Norm))
            .Select(g => new DuplicateGroup(g.Key.Type, g.First().Name, g.Select(r => new DuplicateRecord(r.Id, r.Key, r.Name, r.Links, r.UpdatedAt)).ToList()))
            .ToList();
        return new DuplicateGroups(groups.Take(GroupLimit).ToList(), groups.Count > GroupLimit);
    }

    // Records with no links at all, in either direction.
    public async Task<CleanupList<CleanupRecord>> UnlinkedAsync(string? type, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var kept = await KeptTypesAsync(db, type, cancellationToken);
        var user = AppUser.DefaultId;
        var limit = ItemLimit + 1;
        var rows = await db.Database.SqlQuery<RecordRow>(
            $"""
            SELECT n.id AS "Id", n.type AS "Type", n.key AS "Key", n.name AS "Name"
            FROM node n
            WHERE n.user_id = {user} AND n.type = ANY({kept})
              AND NOT EXISTS (SELECT 1 FROM node_link l WHERE l.source_id = n.id)
              AND NOT EXISTS (SELECT 1 FROM node_link l WHERE l.target_id = n.id)
            ORDER BY n.type, n.name, n.id
            LIMIT {limit}
            """).ToListAsync(cancellationToken);
        return new CleanupList<CleanupRecord>(rows.Take(ItemLimit).Select(r => new CleanupRecord(r.Id, r.Type, r.Key, r.Name)).ToList(), rows.Count > ItemLimit);
    }

    // Records without a value for a required field, or without a link their type requires. The model enforces both when a
    // record is saved, so these came in some other way, such as a model that gained a requirement after its data was loaded.
    public async Task<CleanupList<CleanupIssue>> MissingRequiredAsync(string? type, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var kept = await KeptTypesAsync(db, type, cancellationToken);
        var user = AppUser.DefaultId;
        var issues = new List<CleanupIssue>();

        var fields = await db.FieldDefinitions.AsNoTracking()
            .Where(f => f.Required && f.Owner == null && kept.Contains(f.NodeTypeName))
            .OrderBy(f => f.NodeTypeName).ThenBy(f => f.Id)
            .Select(f => new { f.NodeTypeName, f.Key })
            .ToListAsync(cancellationToken);
        foreach (var field in fields)
        {
            var limit = ItemLimit + 1 - issues.Count;
            if (limit <= 0)
            {
                break;
            }

            var rows = await db.Database.SqlQuery<RecordRow>(
                $"""
                SELECT n.id AS "Id", n.type AS "Type", n.key AS "Key", n.name AS "Name"
                FROM node n
                WHERE n.user_id = {user} AND n.type = {field.NodeTypeName}
                  AND (n.props -> {field.Key} IS NULL
                       OR jsonb_typeof(n.props -> {field.Key}) = 'null'
                       OR n.props -> {field.Key} = '[]'::jsonb
                       OR n.props ->> {field.Key} = '')
                ORDER BY n.name, n.id
                LIMIT {limit}
                """).ToListAsync(cancellationToken);
            issues.AddRange(rows.Select(r => new CleanupIssue(r.Id, r.Type, r.Key, r.Name, $"No {field.Key.Replace('_', ' ')}")));
        }

        var links = await db.LinkDefinitions.AsNoTracking()
            .Where(d => d.Required && kept.Contains(d.SourceType))
            .OrderBy(d => d.SourceType).ThenBy(d => d.RelType).ThenBy(d => d.TargetType)
            .Select(d => new { d.SourceType, d.RelType, d.TargetType })
            .ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            var limit = ItemLimit + 1 - issues.Count;
            if (limit <= 0)
            {
                break;
            }

            var rows = await db.Database.SqlQuery<RecordRow>(
                $"""
                SELECT n.id AS "Id", n.type AS "Type", n.key AS "Key", n.name AS "Name"
                FROM node n
                WHERE n.user_id = {user} AND n.type = {link.SourceType}
                  AND NOT EXISTS (
                      SELECT 1 FROM node_link l JOIN node o ON o.id = l.target_id
                      WHERE l.source_id = n.id AND l.rel_type = {link.RelType} AND o.type = {link.TargetType})
                ORDER BY n.name, n.id
                LIMIT {limit}
                """).ToListAsync(cancellationToken);
            issues.AddRange(rows.Select(r => new CleanupIssue(r.Id, r.Type, r.Key, r.Name, $"No {link.SourceType} {link.RelType} {link.TargetType} link")));
        }

        return new CleanupList<CleanupIssue>(issues.Take(ItemLimit).ToList(), issues.Count > ItemLimit);
    }

    // The types people keep, or just the one asked for if it is one of them.
    private static async Task<string[]> KeptTypesAsync(LinkbaseDbContext db, string? type, CancellationToken cancellationToken) =>
        await db.NodeTypes.AsNoTracking()
            .Where(t => t.Owner == null && (type == null || t.Name == type))
            .Select(t => t.Name)
            .ToArrayAsync(cancellationToken);

    private sealed record DuplicateRow(string Type, string Id, string Key, string Name, DateTimeOffset UpdatedAt, int Links, string Norm);

    private sealed record RecordRow(string Id, string Type, string Key, string Name);
}
