using Linkbase.Data;
using Linkbase.Domains;
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

public sealed record ScenePosition(string NodeId, double X, double Y);

public sealed record SavedScene(string Name, IReadOnlyList<ScenePosition> Positions);


public sealed class GraphRepository(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
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

        return rows.Select(n => ToNode(n.NodeId, n.Label, n.Name, n.Props)).ToList();
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
            ORDER BY label, props->>'date' DESC NULLS LAST, name
            LIMIT {Add(limit, NpgsqlDbType.Integer)}
            """;
        var rows = await db.Database.SqlQueryRaw<NeighbourRow>(sql, parameters.Cast<object>().ToArray()).ToListAsync(cancellationToken);

        var total = rows.Count > 0 ? (int)rows[0].Total : 0;
        return new GraphNeighbours(
            rows.Select(r => ToNode(r.NodeId, r.Label, r.Name, r.Props)).ToList(),
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
            .Select(t => new { t.Name, t.Color, t.Icon, Fields = t.Fields.Select(f => new { f.Key, f.Type, f.Options }).ToList() })
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

        var infos = labels.Select(label => new GraphPropertyInfo(label, "name", GraphValueType.Text)).ToList();
        infos.AddRange(defined.SelectMany(t => t.Fields.Select(f => new GraphPropertyInfo(t.Name, f.Key, TypeOf(f.Type)) { Options = f.Options ?? [] })));
        infos.AddRange(properties.Select(p => new GraphPropertyInfo(p.Label, p.Key, TypeOf(p.Key, p.JsonType))));
        return new GraphSchema(links.Concat(connections.Select(c => new GraphConnection(c.SourceLabel, c.RelType, c.TargetLabel))).ToList(), infos)
        {
            Types = defined.Select(t => new GraphTypeInfo(t.Name, t.Color, t.Icon)).ToList(),
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

        var rows = await db.Database.SqlQuery<MatchRow>(
            $"""
            SELECT DISTINCT m."Label", m."Key", m."Value", m."Score"
            FROM (
                SELECT n.label AS "Label", 'name' AS "Key", n.name AS "Value",
                       (CASE WHEN n.name ILIKE {pattern} THEN 1 ELSE 0 END + similarity(n.name, {term}))::float8 AS "Score"
                FROM graph_nodes n
                WHERE n.label <> ALL({RegisteredDomains.LabelsNamedAfterParent})
                  AND ({labelParameter} IS NULL OR n.label = {labelParameter})
                  AND (n.name ILIKE {pattern} OR n.name % {term})
                UNION ALL
                SELECT n.label, kv.key, item.value, 1::float8
                FROM graph_nodes n, jsonb_each(n.props) kv,
                     jsonb_array_elements_text(CASE jsonb_typeof(kv.value) WHEN 'array' THEN kv.value WHEN 'string' THEN jsonb_build_array(kv.value) ELSE '[]' END) item(value)
                WHERE ({labelParameter} IS NULL OR n.label = {labelParameter})
                  AND n.search_text ILIKE {pattern}
                  AND kv.key <> ALL({RegisteredDomains.LongTextProperties})
                  AND item.value ILIKE {pattern}
            ) m
            ORDER BY m."Score" DESC, m."Label", m."Value"
            LIMIT {MatchLimit}
            """).ToListAsync(cancellationToken);

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

        if (list)
        {
            return await db.Database.SqlQuery<string>(
                $"""
                SELECT DISTINCT item.value AS "Value"
                FROM graph_nodes n,
                     jsonb_array_elements_text(CASE jsonb_typeof(n.props->{key}) WHEN 'array' THEN n.props->{key} ELSE jsonb_build_array(n.props->{key}) END) item(value)
                WHERE ({labelParameter} IS NULL OR n.label = {labelParameter})
                  AND item.value ILIKE {pattern}
                ORDER BY 1
                LIMIT {MatchLimit}
                """).ToListAsync(cancellationToken);
        }

        return await db.Database.SqlQuery<string>(
            $"""
            SELECT DISTINCT v."Value"
            FROM (
                SELECT CASE WHEN {key} = 'name' THEN n.name ELSE n.props->>{key} END AS "Value"
                FROM graph_nodes n
                WHERE {labelParameter} IS NULL OR n.label = {labelParameter}
            ) v
            WHERE v."Value" ILIKE {pattern}
            ORDER BY v."Value"
            LIMIT {MatchLimit}
            """).ToListAsync(cancellationToken);
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

        var nodes = await db.GraphNodes.AsNoTracking()
            .Where(n => n.UserId == AppUser.DefaultId && nodeIds.Contains(n.NodeId))
            .Select(n => new { n.NodeId, n.Label, n.Name, n.Props })
            .ToListAsync(cancellationToken);
        var edges = await db.GraphEdges.AsNoTracking()
            .Where(e => nodeIds.Contains(e.SourceId) && nodeIds.Contains(e.TargetId))
            .Select(e => new { e.SourceId, e.TargetId, e.RelType, e.Props })
            .ToListAsync(cancellationToken);

        return new Subgraph(
            nodes.Select(n => ToNode(n.NodeId, n.Label, n.Name, n.Props)).ToList(),
            edges.Select(e => new GraphEdge(e.SourceId, e.TargetId, e.RelType) { Values = EdgeValues(e.Props) }).ToList());
    }

    // A link's attributes as text, or null when it has none.
    private static Dictionary<string, string>? EdgeValues(string props) =>
        props.Length <= 2 ? null : Records.RecordRepository.Values(props) is { Count: > 0 } values ? values : null;

    public async Task<IReadOnlyList<string>> SceneNamesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GraphScenes.AsNoTracking()
            .Where(s => s.UserId == AppUser.DefaultId)
            .OrderBy(s => s.Name)
            .Select(s => s.Name)
            .ToListAsync(cancellationToken);
    }

    // Saving under an existing name replaces that scene.
    public async Task SaveSceneAsync(string name, IReadOnlyList<ScenePosition> positions, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new LinkbaseValidationException("A scene needs a name.");
        }

        var trimmed = name.Trim();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var scene = await db.GraphScenes.SingleOrDefaultAsync(s => s.UserId == AppUser.DefaultId && s.Name == trimmed, cancellationToken);
        var nodes = JsonSerializer.Serialize(positions);
        if (scene is null)
        {
            db.GraphScenes.Add(new GraphScene { UserId = AppUser.DefaultId, Name = trimmed, Nodes = nodes });
        }
        else
        {
            scene.Nodes = nodes;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SavedScene?> LoadSceneAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var nodes = await db.GraphScenes.AsNoTracking()
            .Where(s => s.UserId == AppUser.DefaultId && s.Name == name)
            .Select(s => s.Nodes)
            .SingleOrDefaultAsync(cancellationToken);
        return nodes is null ? null : new SavedScene(name, JsonSerializer.Deserialize<List<ScenePosition>>(nodes) ?? []);
    }

    public async Task DeleteSceneAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.GraphScenes
            .Where(s => s.UserId == AppUser.DefaultId && s.Name == name)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static GraphNode ToNode(string id, string label, string name, string props)
    {
        using var json = JsonDocument.Parse(props);
        var properties = new List<GraphProperty>();
        foreach (var property in json.RootElement.EnumerateObject())
        {
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
