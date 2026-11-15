using Linkbase.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Graph;

public sealed record GraphProperty(string Key, string Value);

public sealed record GraphNode(string Id, string Label, string Name, IReadOnlyList<GraphProperty> Properties)
{
    public long EntityId => long.Parse(Id[(Id.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    public string? Property(string key) => Properties.FirstOrDefault(p => p.Key == key)?.Value;

    // A node of the graph model stands for a whole type rather than a row.
    public bool IsModel => Id.StartsWith(ModelPrefix, StringComparison.Ordinal);

    public const string ModelPrefix = "model:";
}

// Caption, if given, is drawn on the link in place of its type. Values are the link's attributes, such as a role, or null
// for a link with none. The same two records can be linked the same way again when the values differ.
public sealed record GraphEdge(string SourceId, string TargetId, string RelType, string? Caption = null, IReadOnlyDictionary<string, string>? Values = null)
{
    // Tells links apart on the canvas: the ends and kind, and the values when it has any.
    public string Key => Values is null
        ? $"{SourceId}|{RelType}|{TargetId}"
        : $"{SourceId}|{RelType}|{TargetId}|{string.Join('|', Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"))}";

    // What the canvas draws on it: the caption, or the type followed by its values, such as WORKS_ON · Engineer · 5.
    public string Label => Caption ?? (Values is null ? RelType : $"{RelType} · {string.Join(" · ", Values.Values)}");

    // Every value with its name, such as "role: Engineer, days_a_week: 5". Empty for a link with none.
    public string ValuesText => Values is null ? "" : string.Join(", ", Values.Select(v => $"{v.Key}: {v.Value}"));

    public bool Equals(GraphEdge? other) =>
        other is not null && SourceId == other.SourceId && TargetId == other.TargetId && RelType == other.RelType && Caption == other.Caption
        && ValuesText == other.ValuesText;

    public override int GetHashCode() => HashCode.Combine(SourceId, TargetId, RelType, Caption, ValuesText);
}

// How many links there are of one kind between two types, such as 25 Person WORKS_ON Project.
public sealed record ConnectionCount(string SourceLabel, string RelType, string TargetLabel, long Links);

public sealed record GraphFilter
{
    public IReadOnlyList<string>? RelTypes { get; init; }
    public IReadOnlyList<PatternFilter> Properties { get; init; } = [];
}

// Remaining counts neighbours that matched but were left out by the limit.
public sealed record GraphNeighbours(IReadOnlyList<GraphNode> Nodes, int Remaining);

public sealed record Subgraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);


// What pages and services use of the graph, so a test can stand in for the database.
public interface IGraphRepository
{
    Task<IReadOnlyList<GraphNode>> SearchNodesAsync(
        string? text,
        IReadOnlyCollection<string>? labels,
        CancellationToken cancellationToken = default);

    // The one neighbourhood query: everything linked to a node, in either direction. Property filters only
    // narrow types that have that property, so a date range doesn't hide a crag or partner.
    Task<GraphNeighbours> NeighboursAsync(
        string nodeId,
        GraphFilter filter,
        IReadOnlyCollection<string> exclude,
        int limit,
        CancellationToken cancellationToken = default);

    Task<GraphSchema> SchemaAsync(CancellationToken cancellationToken = default);

    // How many nodes there are of each type, for the graph model.
    Task<IReadOnlyDictionary<string, int>> NodeCountsAsync(CancellationToken cancellationToken = default);

    // How many links there are of each kind, most first. They come from the counts kept on the relationship definitions,
    // because counting tens of millions of links each time takes too long.
    Task<IReadOnlyList<ConnectionCount>> ConnectionCountsAsync(CancellationToken cancellationToken = default);

    // The names of the values each kind of link can carry, such as role, for the kinds that have any.
    Task<IReadOnlyDictionary<(string From, string Rel, string To), IReadOnlyList<string>>> LinkAttributeNamesAsync(CancellationToken cancellationToken = default);

    // Everything matching what's been typed, as label, property and value. Long free text is left out,
    // because a whole note makes a poor suggestion.
    Task<IReadOnlyList<PropertyMatch>> MatchPropertiesAsync(string? label, string text, CancellationToken cancellationToken = default);

    // Values of one property, for suggestions once a filter's operator is chosen. A list's values come one at a time.
    // Only a list looks inside arrays, because doing so for every property made each keystroke five times slower on swiss_rail.
    Task<IReadOnlyList<string>> PropertyValuesAsync(
        string? label, string key, string? text, bool list = false, CancellationToken cancellationToken = default);

    // Runs a pattern, returning each node in the matches. Limit and offset count whole matches.
    Task<PatternResult> RunPatternAsync(GraphPattern pattern, GraphSchema schema, int limit, int offset, CancellationToken cancellationToken = default);

    // The top groups of a ranked pattern, most first.
    Task<IReadOnlyList<RankedNode>> RankPatternAsync(GraphPattern pattern, GraphSchema schema, CancellationToken cancellationToken = default);

    // Count, sum, smallest, largest and average of each number property, over every match of a pattern.
    Task<IReadOnlyList<PropertySummary>> SummarisePatternAsync(GraphPattern pattern, GraphSchema schema, CancellationToken cancellationToken = default);

    // What a node links to, for the Expand menu.
    Task<IReadOnlyList<RelationshipCount>> RelationshipCountsAsync(string nodeId, CancellationToken cancellationToken = default);

    // The given nodes and every relationship between them.
    Task<Subgraph> SubgraphAsync(IReadOnlyCollection<string> nodeIds, CancellationToken cancellationToken = default);
}

public sealed class GraphRepository(IDbContextFactory<LinkbaseDbContext> contextFactory, DomainRegistry registry, Security.FieldPolicy fieldPolicy) : IGraphRepository
{
    // A type and key joined, to test against in SQL: n.label || chr(31) || key <> ALL(pairs).
    private const char PairSeparator = '\u001f';

    private async ValueTask<Security.HiddenFields> HiddenAsync(LinkbaseDbContext db, CancellationToken cancellationToken) =>
        await fieldPolicy.HiddenAsync(db, cancellationToken);

    private static string[] Pairs(Security.HiddenFields hidden) => hidden.Pairs(PairSeparator);

    private const double SearchSimilarity = 0.3;
    private const int SearchLimit = 20;
    private const int MatchLimit = 12;
    private const int SchemaSample = 5000;

    public async Task<IReadOnlyList<GraphNode>> SearchNodesAsync(
        string? text,
        IReadOnlyCollection<string>? labels,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hidden = await HiddenAsync(db, cancellationToken);
        var term = text.Trim();
        var pattern = $"%{term}%";
        var query = db.GraphNodes.AsNoTracking().Where(n => n.UserId == AppUser.DefaultId);
        if (labels is { Count: > 0 })
        {
            query = query.Where(n => labels.Contains(n.Label));
        }

        var rows = await query
            .Where(n => EF.Functions.ILike(n.Name, pattern) || EF.Functions.TrigramsSimilarity(n.Name, term) >= SearchSimilarity)
            .OrderByDescending(n => EF.Functions.ILike(n.Name, pattern))
            .ThenByDescending(n => EF.Functions.TrigramsSimilarity(n.Name, term))
            .ThenBy(n => n.Label)
            .Take(SearchLimit)
            .Select(n => new { n.NodeId, n.Label, n.Name, n.Props })
            .ToListAsync(cancellationToken);

        hidden = await hidden.ForRecordsAsync(db, rows.Select(n => n.NodeId), cancellationToken);
        return rows.Select(n => ToNode(n.NodeId, n.Label, n.Name, n.Props, hidden)).ToList();
    }

    // The one neighbourhood query: everything linked to a node, in either direction. Property filters only
    // narrow types that have that property, so a date range doesn't hide a crag or partner.
    public async Task<GraphNeighbours> NeighboursAsync(
        string nodeId,
        GraphFilter filter,
        IReadOnlyCollection<string> exclude,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var schema = filter.Properties.Count > 0 ? await SchemaAsync(cancellationToken) : null;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hidden = await HiddenAsync(db, cancellationToken);

        var parameters = new List<NpgsqlParameter>();
        string Add(object value, NpgsqlDbType type)
        {
            var name = $"p{parameters.Count}";
            parameters.Add(new NpgsqlParameter(name, type) { Value = value });
            return $"@{name}";
        }

        var conditions = schema is null ? "TRUE" : GraphPatternSql.PropertyFiltersSql(filter.Properties, schema, Add);
        var node = Add(nodeId, NpgsqlDbType.Text);
        var relTypes = Add(filter.RelTypes is { Count: > 0 } ? filter.RelTypes.ToArray() : DBNull.Value, NpgsqlDbType.Array | NpgsqlDbType.Text);

        // Sorting by a date the viewer can't see would show its order, so for them it sorts by nothing.
        var dateHidden = new List<string>();
        if (hidden.AnyType.Contains("date"))
        {
            dateHidden.Add($"label || chr(31) || 'date' = ANY({Add(Pairs(hidden), NpgsqlDbType.Array | NpgsqlDbType.Text)})");
        }

        if (hidden.HasOverrides)
        {
            dateHidden.Add(Security.PropertyLabelSql.Fails("node_id", "'date'"));
        }

        var dateOrder = dateHidden.Count > 0
            ? $"CASE WHEN {string.Join(" OR ", dateHidden)} THEN NULL ELSE props->>'date' END"
            : "props->>'date'";

        // Materialized so the sort happens after the neighbours are found. Otherwise, with an index on label, the
        // planner walks every node in the graph in label order, hoping to reach the limit early: over 2 minutes on
        // 3.6M nodes, rather than 70 ms.
        var sql =
            $"""
            WITH found AS MATERIALIZED (
                SELECT n0.node_id, n0.label, n0.name, n0.props
                FROM (
                    SELECT DISTINCT CASE WHEN e.source_id = {node} THEN e.target_id ELSE e.source_id END AS other
                    FROM graph_edges e
                    WHERE (e.source_id = {node} OR e.target_id = {node})
                      AND ({relTypes} IS NULL OR e.rel_type = ANY({relTypes}))
                ) linked
                JOIN graph_nodes n0 ON n0.node_id = linked.other
                WHERE n0.user_id = {Add(AppUser.DefaultId, NpgsqlDbType.Bigint)}
                  AND n0.node_id <> ALL({Add(exclude.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text)})
                  AND {conditions}
            )
            SELECT node_id AS "NodeId", label AS "Label", name AS "Name", props::text AS "Props",
                   count(*) OVER () AS "Total"
            FROM found
            ORDER BY label, {dateOrder} DESC NULLS LAST, name
            LIMIT {Add(limit, NpgsqlDbType.Integer)}
            """;
        var rows = await db.Database.SqlQueryRaw<NeighbourRow>(sql, parameters.Cast<object>().ToArray()).ToListAsync(cancellationToken);

        var total = rows.Count > 0 ? (int)rows[0].Total : 0;
        var scoped = await hidden.ForRecordsAsync(db, rows.Select(r => r.NodeId), cancellationToken);
        return new GraphNeighbours(
            rows.Select(r => ToNode(r.NodeId, r.Label, r.Name, r.Props, scoped)).ToList(),
            total - rows.Count);
    }

    public async Task<GraphSchema> SchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The most common types come first, so typing C then Enter chooses Crag rather than Country.
        var labels = await db.GraphNodes.AsNoTracking()
            .Where(n => n.UserId == AppUser.DefaultId)
            .GroupBy(n => n.Label)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        // Types from a model file say what fields and links they have, so they need no sampling.
        var defined = await db.NodeTypes.AsNoTracking()
            .OrderBy(t => t.SortOrder)
            .Select(t => new { t.Name, t.Color, t.Icon, Fields = t.Fields.OrderBy(f => f.Id).Select(f => new { f.Key, f.Type, f.Options, f.GroupName }).ToList() })
            .ToListAsync(cancellationToken);
        var links = await db.LinkDefinitions.AsNoTracking()
            .OrderBy(l => l.SourceType).ThenBy(l => l.RelType).ThenBy(l => l.TargetType)
            .Select(l => new GraphConnection(l.SourceType, l.RelType, l.TargetType))
            .ToListAsync(cancellationToken);
        var definedNames = defined.Select(t => t.Name).ToHashSet();
        labels.AddRange(defined.Select(t => t.Name).Where(name => !labels.Contains(name)));
        var labelArray = labels.Where(label => !definedNames.Contains(label)).ToArray();

        // Reads a sample of each other type rather than everything, because this runs whenever Explore opens. On a type
        // bigger than the sample, a property or link that only appears on later rows isn't offered.
        var connections = await db.Database.SqlQuery<ConnectionRow>(
            $"""
            SELECT DISTINCT s.label AS "SourceLabel", e.rel_type AS "RelType", t.label AS "TargetLabel"
            FROM unnest({labelArray}) l(label)
            CROSS JOIN LATERAL (SELECT n.node_id, n.label FROM graph_nodes n WHERE n.label = l.label LIMIT {SchemaSample}) s
            JOIN graph_edges e ON e.source_id = s.node_id
            JOIN graph_nodes t ON t.node_id = e.target_id
            ORDER BY 1, 2, 3
            """).ToListAsync(cancellationToken);

        var properties = await db.Database.SqlQuery<PropertyRow>(
            $"""
            SELECT DISTINCT s.label AS "Label", kv.key AS "Key", jsonb_typeof(kv.value) AS "JsonType"
            FROM unnest({labelArray}) l(label)
            CROSS JOIN LATERAL (SELECT n.label, n.props FROM graph_nodes n WHERE n.label = l.label LIMIT {SchemaSample}) s,
                 jsonb_each(s.props) kv
            WHERE jsonb_typeof(kv.value) <> 'null'
            ORDER BY 1, 2
            """).ToListAsync(cancellationToken);

        // A property the viewer can't see isn't offered, and a filter on it fails as an unknown property does.
        var hidden = await HiddenAsync(db, cancellationToken);
        var infos = labels.Select(label => new GraphPropertyInfo(label, "name", GraphValueType.Text)).ToList();
        infos.AddRange(defined.SelectMany(t => t.Fields.Where(f => !hidden.IsHidden(t.Name, f.Key))
            .Select(f => new GraphPropertyInfo(t.Name, f.Key, TypeOf(f.Type)) { Options = f.Options ?? [], Group = f.GroupName })));
        infos.AddRange(properties.Where(p => !hidden.IsHidden(p.Label, p.Key)).Select(p => new GraphPropertyInfo(p.Label, p.Key, TypeOf(p.Key, p.JsonType))));

        // The same relationship name is rarely reused between different type pairs; where it is, the first pair's
        // definition of a shared key wins, the same loose way a node property's label already is when unspecified.
        var attributes = await db.LinkAttributes.AsNoTracking()
            .OrderBy(a => a.Id)
            .Select(a => new { a.LinkDefinition.RelType, a.Key, a.Type, a.Options })
            .ToListAsync(cancellationToken);
        var linkInfos = attributes
            .GroupBy(a => (a.RelType, a.Key))
            .Select(g => new GraphLinkPropertyInfo(g.Key.RelType, g.Key.Key, TypeOf(g.First().Type)) { Options = g.First().Options ?? [] })
            .ToList();

        return new GraphSchema(links.Concat(connections.Select(c => new GraphConnection(c.SourceLabel, c.RelType, c.TargetLabel))).ToList(), infos)
        {
            Types = defined.Select(t => new GraphTypeInfo(t.Name, t.Color, t.Icon)).ToList(),
            LinkProperties = linkInfos,
            HiddenKeys = hidden.AnyType.ToList(),
            HasOverrides = hidden.HasOverrides,
            LabelsNamedAfterParent = registry.LabelsNamedAfterParent,
        };
    }

    // How many nodes there are of each type, for the graph model.
    public async Task<IReadOnlyDictionary<string, int>> NodeCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GraphNodes.AsNoTracking()
            .Where(n => n.UserId == AppUser.DefaultId)
            .GroupBy(n => n.Label)
            .Select(g => new { Label = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Label, g => g.Count, cancellationToken);
    }

    // How many links there are of each kind, most first. They come from the counts kept on the relationship definitions,
    // because counting tens of millions of links each time takes too long.
    public async Task<IReadOnlyList<ConnectionCount>> ConnectionCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The kept counts include links the viewer can't see, so with row security on they are counted live, through the
        // policies. That is slower on a very large model, and security off keeps the fast path (docs/performance.md).
        if ((await HiddenAsync(db, cancellationToken)).Secured)
        {
            var live = await db.NodeLinks.AsNoTracking()
                .GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
                .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Links = g.LongCount() })
                .ToListAsync(cancellationToken);
            return live.OrderByDescending(d => d.Links).ThenBy(d => d.SourceType).ThenBy(d => d.RelType).ThenBy(d => d.TargetType)
                .Select(d => new ConnectionCount(d.SourceType, d.RelType, d.TargetType, d.Links)).ToList();
        }

        return await db.LinkDefinitions.AsNoTracking()
            .Where(d => d.Links > 0)
            .OrderByDescending(d => d.Links).ThenBy(d => d.SourceType).ThenBy(d => d.RelType).ThenBy(d => d.TargetType)
            .Select(d => new ConnectionCount(d.SourceType, d.RelType, d.TargetType, d.Links))
            .ToListAsync(cancellationToken);
    }

    // The names of the values each kind of link can carry, such as role, for the kinds that have any.
    public async Task<IReadOnlyDictionary<(string From, string Rel, string To), IReadOnlyList<string>>> LinkAttributeNamesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.LinkAttributes.AsNoTracking()
            .OrderBy(a => a.Id)
            .Select(a => new { a.LinkDefinition.SourceType, a.LinkDefinition.RelType, a.LinkDefinition.TargetType, a.Key })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => (r.SourceType, r.RelType, r.TargetType))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Key).ToList());
    }

    // Everything matching what's been typed, as label, property and value. Long free text is left out,
    // because a whole note makes a poor suggestion.
    public async Task<IReadOnlyList<PropertyMatch>> MatchPropertiesAsync(string? label, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var term = text.Trim();
        var pattern = $"%{EscapeLike(term)}%";
        var labelParameter = new NpgsqlParameter("label", NpgsqlDbType.Text) { Value = (object?)label ?? DBNull.Value };
        var hidden = await HiddenAsync(db, cancellationToken);
        var pairs = Pairs(hidden);

        // A key a record's own label hides is left out too. Raw SQL, since the pieces aren't parameters.
        var overridden = hidden.HasOverrides ? $"AND NOT (kv.key = ANY({Security.PropertyLabelSql.FailingKeys("n.node_id")}))" : "";
        var sql1 = $$"""
            SELECT DISTINCT m."Label", m."Key", m."Value", m."Score"
            FROM (
                SELECT n.label AS "Label", 'name' AS "Key", n.name AS "Value",
                       (CASE WHEN n.name ILIKE {0} THEN 1 ELSE 0 END + similarity(n.name, {1}))::float8 AS "Score"
                FROM graph_nodes n
                WHERE n.label <> ALL({2})
                  AND ({3} IS NULL OR n.label = {3})
                  AND (n.name ILIKE {0} OR n.name % {1})
                UNION ALL
                SELECT n.label, kv.key, item.value, 1::float8
                FROM graph_nodes n, jsonb_each(n.props) kv,
                     jsonb_array_elements_text(CASE jsonb_typeof(kv.value) WHEN 'array' THEN kv.value WHEN 'string' THEN jsonb_build_array(kv.value) ELSE '[]' END) item(value)
                WHERE ({3} IS NULL OR n.label = {3})
                  AND n.search_text ILIKE {0}
                  AND kv.key <> ALL({4})
                  AND n.label || chr(31) || kv.key <> ALL({5})
                  {{overridden}}
                  AND item.value ILIKE {0}
            ) m
            ORDER BY m."Score" DESC, m."Label", m."Value"
            LIMIT {{MatchLimit}}
            """;
        var rows = await db.Database.SqlQueryRaw<MatchRow>(sql1, pattern, term, registry.LabelsNamedAfterParent, labelParameter, registry.LongTextProperties, pairs).ToListAsync(cancellationToken);

        return rows.Select(r => new PropertyMatch(r.Label, r.Key, r.Value)).ToList();
    }

    // Values of one property, for suggestions once a filter's operator is chosen. A list's values come one at a time.
    // Only a list looks inside arrays, because doing so for every property made each keystroke five times slower on swiss_rail.
    public async Task<IReadOnlyList<string>> PropertyValuesAsync(
        string? label, string key, string? text, bool list = false, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pattern = string.IsNullOrWhiteSpace(text) ? "%" : $"%{EscapeLike(text.Trim())}%";
        var labelParameter = new NpgsqlParameter("label", NpgsqlDbType.Text) { Value = (object?)label ?? DBNull.Value };
        var hidden = await HiddenAsync(db, cancellationToken);
        var pairs = Pairs(hidden);

        // A value a record's own label hides is left out too. Raw SQL, since the piece isn't a parameter.
        var overridden = hidden.HasOverrides ? $"AND NOT ({Security.PropertyLabelSql.Fails("n.node_id", "{0}")})" : "";
        if (list)
        {
            var sql2 = $$"""
                SELECT DISTINCT item.value AS "Value"
                FROM graph_nodes n,
                     jsonb_array_elements_text(CASE jsonb_typeof(n.props->{0}) WHEN 'array' THEN n.props->{0} ELSE jsonb_build_array(n.props->{0}) END) item(value)
                WHERE ({1} IS NULL OR n.label = {1})
                  AND n.label || chr(31) || {0} <> ALL({2})
                  {{overridden}}
                  AND item.value ILIKE {3}
                ORDER BY 1
                LIMIT {{MatchLimit}}
                """;
            return await db.Database.SqlQueryRaw<string>(sql2, key, labelParameter, pairs, pattern).ToListAsync(cancellationToken);
        }

        var sql3 = $$"""
            SELECT DISTINCT v."Value"
            FROM (
                SELECT CASE WHEN {0} = 'name' THEN n.name ELSE n.props->>{0} END AS "Value"
                FROM graph_nodes n
                WHERE ({1} IS NULL OR n.label = {1})
                  AND ({0} = 'name' OR (n.label || chr(31) || {0} <> ALL({2}) {{overridden}}))
            ) v
            WHERE v."Value" ILIKE {3}
            ORDER BY v."Value"
            LIMIT {{MatchLimit}}
            """;
        return await db.Database.SqlQueryRaw<string>(sql3, key, labelParameter, pairs, pattern).ToListAsync(cancellationToken);
    }

    // Runs a pattern, returning each node in the matches. Limit and offset count whole matches.
    public async Task<PatternResult> RunPatternAsync(GraphPattern pattern, GraphSchema schema, int limit, int offset, CancellationToken cancellationToken = default)
    {
        var (sql, parameters) = GraphPatternSql.Build(pattern, schema, limit, offset);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Database.SqlQueryRaw<PathRow>(sql, parameters.Cast<object>().ToArray()).ToListAsync(cancellationToken);

        var total = rows.Count > 0 ? (int)rows[0].Total : 0;
        return new PatternResult(rows.SelectMany(r => r.NodeIds).Distinct().ToList(), Math.Max(0, total - offset - rows.Count));
    }

    // The top groups of a ranked pattern, most first.
    public async Task<IReadOnlyList<RankedNode>> RankPatternAsync(GraphPattern pattern, GraphSchema schema, CancellationToken cancellationToken = default)
    {
        var (sql, parameters) = GraphPatternSql.BuildRank(pattern, schema);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQueryRaw<RankedNode>(sql, parameters.Cast<object>().ToArray()).ToListAsync(cancellationToken);
    }

    // Count, sum, smallest, largest and average of each number property, over every match of a pattern.
    public async Task<IReadOnlyList<PropertySummary>> SummarisePatternAsync(GraphPattern pattern, GraphSchema schema, CancellationToken cancellationToken = default)
    {
        var (sql, parameters) = GraphPatternSql.BuildSummary(pattern, schema);
        if (sql is null)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQueryRaw<PropertySummary>(sql, parameters.Cast<object>().ToArray()).ToListAsync(cancellationToken);
    }

    // What a node links to, for the Expand menu.
    public async Task<IReadOnlyList<RelationshipCount>> RelationshipCountsAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Database.SqlQuery<RelationshipRow>(
            $"""
            SELECT l."RelType", l."Outgoing", n.label AS "OtherLabel", count(*)::int AS "Count"
            FROM (
                SELECT e.rel_type AS "RelType", true AS "Outgoing", e.target_id AS other
                FROM graph_edges e WHERE e.source_id = {nodeId}
                UNION ALL
                SELECT e.rel_type, false, e.source_id
                FROM graph_edges e WHERE e.target_id = {nodeId}
            ) l
            JOIN graph_nodes n ON n.node_id = l.other
            GROUP BY l."RelType", l."Outgoing", n.label
            ORDER BY "Count" DESC
            """).ToListAsync(cancellationToken);
        return rows.Select(r => new RelationshipCount(r.RelType, r.Outgoing, r.OtherLabel, r.Count)).ToList();
    }

    // The given nodes and every relationship between them.
    public async Task<Subgraph> SubgraphAsync(IReadOnlyCollection<string> nodeIds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var hidden = await HiddenAsync(db, cancellationToken);
        var nodes = await db.GraphNodes.AsNoTracking()
            .Where(n => n.UserId == AppUser.DefaultId && nodeIds.Contains(n.NodeId))
            .Select(n => new { n.NodeId, n.Label, n.Name, n.Props })
            .ToListAsync(cancellationToken);
        var edges = await db.GraphEdges.AsNoTracking()
            .Where(e => nodeIds.Contains(e.SourceId) && nodeIds.Contains(e.TargetId))
            .Select(e => new { e.SourceId, e.TargetId, e.RelType, e.Props })
            .ToListAsync(cancellationToken);

        hidden = await hidden.ForRecordsAsync(db, nodes.Select(n => n.NodeId), cancellationToken);
        return new Subgraph(
            nodes.Select(n => ToNode(n.NodeId, n.Label, n.Name, n.Props, hidden)).ToList(),
            edges.Select(e => new GraphEdge(e.SourceId, e.TargetId, e.RelType) { Values = EdgeValues(e.Props) }).ToList());
    }

    // A link's attributes as text, or null when it has none.
    private static Dictionary<string, string>? EdgeValues(string props) =>
        props.Length <= 2 ? null : Records.RecordRepository.Values(props) is { Count: > 0 } values ? values : null;

    private static GraphNode ToNode(string id, string label, string name, string props, Security.HiddenFields hidden)
    {
        var skip = hidden.For(label, id);
        using var json = JsonDocument.Parse(props);
        var properties = new List<GraphProperty>();
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (skip.Contains(property.Name))
            {
                continue;
            }

            var value = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Array => string.Join(", ", property.Value.EnumerateArray().Select(v => v.ToString())),
                _ => property.Value.ToString(),
            };
            if (!string.IsNullOrEmpty(value))
            {
                properties.Add(new GraphProperty(property.Name, value));
            }
        }

        return new GraphNode(id, label, name, properties);
    }

    private static GraphValueType TypeOf(FieldType type) => type switch
    {
        FieldType.Number => GraphValueType.Number,
        FieldType.Date => GraphValueType.Date,
        FieldType.Boolean => GraphValueType.Boolean,
        FieldType.Choice => GraphValueType.Choice,
        FieldType.List => GraphValueType.List,
        _ => GraphValueType.Text,
    };

    private static GraphValueType TypeOf(string key, string jsonType) => (key, jsonType) switch
    {
        ("date", _) => GraphValueType.Date,
        (_, _) when key.EndsWith("_date", StringComparison.Ordinal) => GraphValueType.Date,
        (_, "number") => GraphValueType.Number,
        (_, "boolean") => GraphValueType.Boolean,
        (_, "array") => GraphValueType.List,
        _ => GraphValueType.Text,
    };

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private sealed record NeighbourRow(string NodeId, string Label, string Name, string Props, long Total);

    private sealed record ConnectionRow(string SourceLabel, string RelType, string TargetLabel);


    private sealed record PropertyRow(string Label, string Key, string JsonType);

    private sealed record MatchRow(string Label, string Key, string Value, double Score);

    private sealed record PathRow(string[] NodeIds, long Total);

    private sealed record RelationshipRow(string RelType, bool Outgoing, string OtherLabel, int Count);
}
