using System.Globalization;
using Linkbase.Core.Graph;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Timeline;

// A date field a model has, such as an ascent's date, which a timeline can be drawn from.
public sealed record DateFieldOption(string Type, string Key);

public sealed record TimelineEntry(string Id, string Key, string Name, DateOnly Date);

// How many records have a date in a month, as yyyy-MM.
public sealed record TimelineMonth(string Month, int Count);

// Records with a date in the range, newest first, and how many per month. Total counts the whole range even when Entries was
// cut short. Undated is how many records of the type have no value for the field at all.
public sealed record TimelineData(IReadOnlyList<TimelineEntry> Entries, IReadOnlyList<TimelineMonth> Months, int Total, int Undated);

// A record as it was at a time, from the audit trail: its name and values, rewound by undoing every edit made since.
public sealed record RewoundNode(string Name, IReadOnlyList<GraphProperty> Properties);

// What the graph looked like at a time, for the records and links on the canvas. A record or link that didn't exist yet is in
// Missing. Rewound has the records whose name or values were different then.
public sealed record AsOfView(
    IReadOnlySet<string> MissingNodes, IReadOnlySet<string> MissingEdges, IReadOnlyDictionary<string, RewoundNode> Rewound)
{
    public static readonly AsOfView None = new(new HashSet<string>(), new HashSet<string>(), new Dictionary<string, RewoundNode>());
}

// Dated records on a timeline, and the graph as it was. Dates are kept as text, yyyy-MM-dd, which sorts the same as dates do, so
// a malformed value can't make a whole query fail.
public sealed class TimelineRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public const int EntryLimit = 300;
    private const int MonthLimit = 600;

    // Single-valued date fields of the types people keep or a module derives alike: a trip's dates are worth seeing too.
    public async Task<IReadOnlyList<DateFieldOption>> DateFieldsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FieldDefinitions.AsNoTracking()
            .Where(f => f.Type == FieldType.Date && !f.Multiple)
            .OrderBy(f => f.NodeType.SortOrder).ThenBy(f => f.NodeTypeName).ThenBy(f => f.Id)
            .Select(f => new DateFieldOption(f.NodeTypeName, f.Key))
            .ToListAsync(cancellationToken);
    }

    // From and To are yyyy-MM-dd, either may be left out, and both are included.
    public async Task<TimelineData> LoadAsync(string type, string field, string? from, string? to, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = AppUser.DefaultId;
        var low = from ?? "";
        var high = to ?? "9999-99-99";
        var limit = EntryLimit;
        var months = MonthLimit;

        var entries = await db.Database.SqlQuery<EntryRow>(
            $"""
            SELECT n.id AS "Id", n.key AS "Key", n.name AS "Name", n.props ->> {field} AS "Date"
            FROM node n
            WHERE n.user_id = {user} AND n.type = {type}
              AND n.props ->> {field} ~ '^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]$'
              AND n.props ->> {field} >= {low} AND n.props ->> {field} <= {high}
            ORDER BY n.props ->> {field} DESC, n.name, n.id
            LIMIT {limit}
            """).ToListAsync(cancellationToken);

        var perMonth = await db.Database.SqlQuery<MonthRow>(
            $"""
            SELECT substr(n.props ->> {field}, 1, 7) AS "Month", count(*)::int AS "Count"
            FROM node n
            WHERE n.user_id = {user} AND n.type = {type}
              AND n.props ->> {field} ~ '^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]$'
              AND n.props ->> {field} >= {low} AND n.props ->> {field} <= {high}
            GROUP BY 1
            ORDER BY 1 DESC
            LIMIT {months}
            """).ToListAsync(cancellationToken);

        var undated = await db.Database.SqlQuery<int>(
            $"""
            SELECT count(*)::int AS "Value"
            FROM node n
            WHERE n.user_id = {user} AND n.type = {type}
              AND (n.props ->> {field} IS NULL OR NOT n.props ->> {field} ~ '^[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]$')
            """).SingleAsync(cancellationToken);

        return new TimelineData(
            entries.Select(e => new { Row = e, Parsed = DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null })
                .Where(e => e.Parsed is not null)
                .Select(e => new TimelineEntry(e.Row.Id, e.Row.Key, e.Row.Name, e.Parsed!.Value))
                .ToList(),
            perMonth.OrderBy(m => m.Month, StringComparer.Ordinal).Select(m => new TimelineMonth(m.Month, m.Count)).ToList(),
            perMonth.Sum(m => m.Count),
            undated);
    }

    // The day the history starts, for the "as of" slider: the first change or import there is a record of.
    public async Task<DateOnly?> EarliestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var change = await db.ChangeSets.AsNoTracking().OrderBy(s => s.Id).Select(s => (DateTimeOffset?)s.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var import = await db.ImportBatches.AsNoTracking().OrderBy(b => b.Id).Select(b => (DateTimeOffset?)b.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var first = new[] { change, import }.Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty().Min();
        return change is null && import is null ? null : DateOnly.FromDateTime(first.LocalDateTime);
    }

    // Works out which of the records and links shown didn't exist at the time, and how the others looked. A record is new if it
    // was created, by hand or by an import, after the time. A link is new if it was made by hand after the time, or came in
    // with an import after it. Links made before history was kept can't be told from old ones, so they are shown.
    public async Task<AsOfView> AsOfAsync(
        IReadOnlyCollection<GraphNode> nodes, IReadOnlyCollection<GraphEdge> edges, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        if (nodes.Count == 0)
        {
            return AsOfView.None;
        }

        // Npgsql only accepts offset 0 for timestamptz, and callers pass local-offset times.
        at = at.ToUniversalTime();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ids = nodes.Select(n => n.Id).ToList();

        var createdAfter = await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id) && n.CreatedAt > at).Select(n => n.Id).ToListAsync(cancellationToken);
        var createdByHand = await db.Changes.AsNoTracking()
            .Where(c => c.EntityKind == ChangeEntityKind.Node && c.Op == ChangeOp.Create && ids.Contains(c.EntityId) && c.ChangeSet.CreatedAt > at)
            .Select(c => c.EntityId)
            .ToListAsync(cancellationToken);
        var missingNodes = createdAfter.Concat(createdByHand).ToHashSet();

        // A link made by hand has a create entry. If the earliest is after the time, it didn't exist yet.
        var linkCreates = await db.Changes.AsNoTracking()
            .Where(c => c.EntityKind == ChangeEntityKind.Link && c.Op == ChangeOp.Create && ids.Contains(c.EntityId))
            .Select(c => new { c.EntityId, c.RelType, c.RelatedId, When = c.ChangeSet.CreatedAt })
            .ToListAsync(cancellationToken);
        var firstMade = linkCreates
            .GroupBy(c => (c.EntityId, c.RelType, c.RelatedId))
            .ToDictionary(g => g.Key, g => g.Min(c => c.When));
        var importedLater = await db.NodeLinks.AsNoTracking()
            .Where(l => ids.Contains(l.SourceId) && l.ImportBatchId != null && l.ImportBatch!.CreatedAt > at)
            .Select(l => new { l.SourceId, l.RelType, l.TargetId })
            .ToListAsync(cancellationToken);
        var laterImports = importedLater.Select(l => (l.SourceId, (string?)l.RelType, (string?)l.TargetId)).ToHashSet();

        var missingEdges = edges
            .Where(e => missingNodes.Contains(e.SourceId) || missingNodes.Contains(e.TargetId)
                || (firstMade.TryGetValue((e.SourceId, e.RelType, e.TargetId), out var made) && made > at)
                || laterImports.Contains((e.SourceId, e.RelType, e.TargetId)))
            .Select(e => e.Key)
            .ToHashSet();

        // Edits made since are undone, newest first, so each value ends as it was at the time.
        var edits = await db.Changes.AsNoTracking()
            .Where(c => c.EntityKind == ChangeEntityKind.Node && c.Op == ChangeOp.Update && ids.Contains(c.EntityId) && c.ChangeSet.CreatedAt > at)
            .OrderByDescending(c => c.Id)
            .Select(c => new { c.EntityId, c.FieldKey, c.OldValue })
            .ToListAsync(cancellationToken);
        var rewound = new Dictionary<string, RewoundNode>();
        foreach (var group in edits.GroupBy(e => e.EntityId))
        {
            var node = nodes.First(n => n.Id == group.Key);
            var name = node.Name;
            var values = node.Properties.ToDictionary(p => p.Key, p => p.Value);
            var order = node.Properties.Select(p => p.Key).ToList();
            foreach (var edit in group)
            {
                if (edit.FieldKey is null or "name")
                {
                    name = edit.OldValue ?? name;
                    continue;
                }

                if (string.IsNullOrEmpty(edit.OldValue))
                {
                    values.Remove(edit.FieldKey);
                }
                else
                {
                    values[edit.FieldKey] = edit.OldValue.Replace("\n", ", ");
                    if (!order.Contains(edit.FieldKey))
                    {
                        order.Add(edit.FieldKey);
                    }
                }
            }

            rewound[group.Key] = new RewoundNode(name, order.Where(values.ContainsKey).Select(k => new GraphProperty(k, values[k])).ToList());
        }

        return new AsOfView(missingNodes, missingEdges, rewound);
    }

    private sealed record EntryRow(string Id, string Key, string Name, string Date);

    private sealed record MonthRow(string Month, int Count);
}
