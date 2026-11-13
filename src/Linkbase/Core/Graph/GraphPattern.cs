using Linkbase.Data;
using Linkbase.Domains;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Graph;

// A list is several text values, held as a JSON array.
public enum GraphValueType { Text, Number, Date, Boolean, Choice, List }

public enum FilterOperator { Equals, Contains, StartsWith, EndsWith, GreaterThan, GreaterOrEqual, LessThan, LessOrEqual }

public sealed record GraphConnection(string SourceLabel, string RelType, string TargetLabel);

public sealed record GraphPropertyInfo(string Label, string Key, GraphValueType Type)
{
    // A choice field's values, in the order to offer them. Empty for every other type.
    public IReadOnlyList<string> Options { get; init; } = [];

    // Records compare lists by reference, so compare the options by value.
    public bool Equals(GraphPropertyInfo? other) =>
        other is not null && Label == other.Label && Key == other.Key && Type == other.Type && Options.SequenceEqual(other.Options);

    public override int GetHashCode() => HashCode.Combine(Label, Key, Type, Options.Count);
}

// A value a relationship's links can carry, such as a partner's role. See docs/models.md#link-attributes. Keyed by RelType
// alone: the same relationship name between different type pairs is rare, and treated the same loose way a node property's
// label already is when a filter doesn't say one.
public sealed record GraphLinkPropertyInfo(string RelType, string Key, GraphValueType Type)
{
    public IReadOnlyList<string> Options { get; init; } = [];

    public bool Equals(GraphLinkPropertyInfo? other) =>
        other is not null && RelType == other.RelType && Key == other.Key && Type == other.Type && Options.SequenceEqual(other.Options);

    public override int GetHashCode() => HashCode.Combine(RelType, Key, Type, Options.Count);
}

// What exists in the graph: which labels link to which, and which properties each label (and, separately, each
// relationship's links) has.
public sealed record GraphSchema(IReadOnlyList<GraphConnection> Connections, IReadOnlyList<GraphPropertyInfo> Properties)
{
    // In the order of Properties, which the repository lists with the most common types first.
    public IReadOnlyList<string> Labels { get; } = Properties.Select(p => p.Label).Distinct().ToList();

    public IReadOnlyList<string> RelTypes { get; } = Connections.Select(c => c.RelType).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();

    // How the model file says its types should look, where it says so.
    public IReadOnlyList<GraphTypeInfo> Types { get; init; } = [];

    // The values each relationship's links can carry. Empty for a relationship with none.
    public IReadOnlyList<GraphLinkPropertyInfo> LinkProperties { get; init; } = [];
}

public sealed record GraphTypeInfo(string Label, string? Color, string? Icon);

public sealed record PatternFilter(string Key, FilterOperator Operator, string Value)
{
    // Free text: the value can be in the node's name or any of its properties.
    public const string AnyField = "*";
}

// A null label matches a node of any kind.
public sealed record PatternNode(string? Label, IReadOnlyList<PatternFilter> Filters);

public sealed record PatternRel(string RelType, bool Outgoing)
{
    // Conditions on the values the matched link itself carries, such as role = Lead. See docs/models.md#link-attributes.
    public IReadOnlyList<PatternFilter> Filters { get; init; } = [];
}

// A chain such as Crag <-AT- Route. There's always one more node than there are relationships.
public sealed record GraphPattern(IReadOnlyList<PatternNode> Nodes, IReadOnlyList<PatternRel> Rels);

// Node ids are in the order of the query's matches. Remaining counts matches left out by the limit.
public sealed record PatternResult(IReadOnlyList<string> NodeIds, int Remaining);

// Count is the nodes that have a number there. Every other figure is null when it's zero.
public sealed record PropertySummary(string Label, string Key, long Count, decimal? Sum, decimal? Min, decimal? Max, decimal? Average);

public sealed record PropertyMatch(string Label, string Key, string Value);

public sealed record RelationshipCount(string RelType, bool Outgoing, string OtherLabel, int Count);

// Turns a pattern into SQL. Labels, relationships and property names only ever reach the SQL text after
// passing an allow-list. Every value is a parameter.
public static partial class GraphPatternSql
{

    public static (string Sql, List<NpgsqlParameter> Parameters) Build(GraphPattern pattern, GraphSchema schema, int limit, int offset)
    {
        if (pattern.Nodes.Count == 0 || pattern.Rels.Count != pattern.Nodes.Count - 1)
        {
            throw new LinkbaseValidationException("That search isn't complete.");
        }

        var parameters = new List<NpgsqlParameter>();
        var (from, where) = FromAndWhere(pattern, schema, parameters);
        var Add = ParameterAdder(parameters);

        var indexes = Enumerable.Range(0, pattern.Nodes.Count).ToList();
        var ids = string.Join(", ", indexes.Select(i => $"n{i}.node_id"));

        // Names that really contain the text come before near misses.
        var closest = indexes.SelectMany(i => pattern.Nodes[i].Filters
            .Where(f => f.Key == PatternFilter.AnyField)
            .Select(f => $"n{i}.name ILIKE {Add($"%{EscapeLike(f.Value)}%", NpgsqlDbType.Text)} DESC"));
        var order = string.Join(", ", closest.Concat(indexes.SelectMany(i => new[] { $"n{i}.name", $"n{i}.node_id" })));
        var sql =
            $"""
            SELECT ARRAY[{ids}] AS "NodeIds", count(*) OVER () AS "Total"
            {from}
            WHERE {string.Join(" AND ", where)}
            ORDER BY {order}
            LIMIT {Add(limit, NpgsqlDbType.Integer)} OFFSET {Add(offset, NpgsqlDbType.Integer)}
            """;
        return (sql, parameters);
    }

    // Totals for the number properties of every typed node in a pattern, over all its matches rather than one page. A node
    // that matches several times, such as a crag with many routes, counts once. Nodes of a type that has no number
    // properties, and nodes of any type, add nothing.
    public static (string? Sql, List<NpgsqlParameter> Parameters) BuildSummary(GraphPattern pattern, GraphSchema schema)
    {
        var parameters = new List<NpgsqlParameter>();
        var (from, where) = FromAndWhere(pattern, schema, parameters);
        var selects = new List<string>();
        for (var i = 0; i < pattern.Nodes.Count; i++)
        {
            if (pattern.Nodes[i].Label is not { } label)
            {
                continue;
            }

            foreach (var info in schema.Properties.Where(p => p.Label == label && p.Type == GraphValueType.Number))
            {
                // The key is safe to put in the SQL text for the same reasons as in FilterSql.
                if (!PropertyKey().IsMatch(info.Key))
                {
                    continue;
                }

                // A value that isn't a plain number, from sampled data, is left out rather than failing the cast.
                var value = $"CASE WHEN n{i}.props->>'{info.Key}' ~ '^-?[0-9]+(\\.[0-9]+)?$' THEN (n{i}.props->>'{info.Key}')::numeric END";
                selects.Add(
                    $"""
                    SELECT '{label.Replace("'", "''")}' AS "Label", '{info.Key}' AS "Key", count(v)::bigint AS "Count", sum(v) AS "Sum", min(v) AS "Min", max(v) AS "Max", avg(v) AS "Average"
                    FROM (SELECT DISTINCT ON (n{i}.node_id) {value} AS v {from} WHERE {string.Join(" AND ", where)}) t{i}
                    """);
            }
        }

        return (selects.Count == 0 ? null : string.Join("\nUNION ALL\n", selects), parameters);
    }

    private static Func<object, NpgsqlDbType, string> ParameterAdder(List<NpgsqlParameter> parameters) => (value, type) =>
    {
        var name = $"p{parameters.Count}";
        parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        return $"@{name}";
    };

    private static (StringBuilder From, List<string> Where) FromAndWhere(GraphPattern pattern, GraphSchema schema, List<NpgsqlParameter> parameters)
    {
        var Add = ParameterAdder(parameters);
        var from = new StringBuilder("FROM graph_nodes n0");
        var where = new List<string> { $"n0.user_id = {Add(AppUser.DefaultId, NpgsqlDbType.Bigint)}" };

        for (var i = 0; i < pattern.Rels.Count; i++)
        {
            var rel = pattern.Rels[i];
            if (!schema.RelTypes.Contains(rel.RelType))
            {
                throw new LinkbaseValidationException($"Unknown relationship '{rel.RelType}'.");
            }

            var (near, far) = rel.Outgoing ? ("source_id", "target_id") : ("target_id", "source_id");
            from.Append(System.Globalization.CultureInfo.InvariantCulture, $" JOIN graph_edges e{i} ON e{i}.{near} = n{i}.node_id AND e{i}.rel_type = {Add(rel.RelType, NpgsqlDbType.Text)}");
            from.Append(System.Globalization.CultureInfo.InvariantCulture, $" JOIN graph_nodes n{i + 1} ON n{i + 1}.node_id = e{i}.{far}");

            foreach (var filter in rel.Filters)
            {
                where.Add(LinkFilterSql(i, rel.RelType, filter, schema, Add));
            }
        }

        for (var i = 0; i < pattern.Nodes.Count; i++)
        {
            var node = pattern.Nodes[i];
            if (node.Label is { } label)
            {
                if (!schema.Labels.Contains(label))
                {
                    throw new LinkbaseValidationException($"Unknown type '{label}'.");
                }

                where.Add($"n{i}.label = {Add(label, NpgsqlDbType.Text)}");
            }

            foreach (var filter in node.Filters)
            {
                where.Add(FilterSql(i, node.Label, filter, schema, Add));
            }
        }

        return (from, where);
    }

    // Conditions on the nodes an expansion adds, as node n0. A condition only applies to types that have that
    // property, so filtering by a route's grade doesn't hide the crag it's at.
    public static string PropertyFiltersSql(IReadOnlyList<PatternFilter> filters, GraphSchema schema, Func<object, NpgsqlDbType, string> add)
    {
        var conditions = filters.Select(filter =>
        {
            var info = schema.Properties.FirstOrDefault(p => p.Key == filter.Key && p.Key != "name")
                ?? throw new LinkbaseValidationException($"Unknown property '{filter.Key}'.");
            var labels = schema.Properties.Where(p => p.Key == filter.Key && p.Type == info.Type).Select(p => p.Label).ToArray();

            // This checks the key, so it has to come before the key goes into the text below.
            var comparison = FilterSql(0, info.Label, filter, schema, add);
            return $"(n0.label <> ALL({add(labels, NpgsqlDbType.Array | NpgsqlDbType.Text)}) OR n0.props->>'{filter.Key}' IS NULL OR {comparison})";
        });
        return string.Join(" AND ", conditions.DefaultIfEmpty("TRUE"));
    }

    private static string FilterSql(int index, string? label, PatternFilter filter, GraphSchema schema, Func<object, NpgsqlDbType, string> add)
    {
        if (filter.Key == PatternFilter.AnyField)
        {
            return AnyFieldSql(index, filter, add);
        }

        var type = ValueType(schema, label, filter.Key)
            ?? throw new LinkbaseValidationException($"Unknown property '{filter.Key}'.");
        var column = filter.Key == "name" ? $"n{index}.name" : $"n{index}.props->>'{filter.Key}'";
        return ValueFilterSql(column, $"n{index}.props", filter, type, add);
    }

    // A filter on a matched link's own values, such as role = Lead, found on relationship e{index}.
    private static string LinkFilterSql(int index, string relType, PatternFilter filter, GraphSchema schema, Func<object, NpgsqlDbType, string> add)
    {
        if (filter.Key == PatternFilter.AnyField)
        {
            throw new LinkbaseValidationException("A link's own condition needs one of its values, not free text.");
        }

        var type = LinkValueType(schema, relType, filter.Key)
            ?? throw new LinkbaseValidationException($"'{relType}' has no value called '{filter.Key}'.");
        return ValueFilterSql($"e{index}.props->>'{filter.Key}'", $"e{index}.props", filter, type, add);
    }

    // Shared by a node's own filters and a link's: the key has already been checked against the schema, so it's safe in
    // the SQL text, and it must also look like a column name.
    private static string ValueFilterSql(string column, string propsColumn, PatternFilter filter, GraphValueType type, Func<object, NpgsqlDbType, string> add)
    {
        if (!PropertyKey().IsMatch(filter.Key))
        {
            throw new LinkbaseValidationException($"Unknown property '{filter.Key}'.");
        }

        switch (type)
        {
            case GraphValueType.Number:
                if (!decimal.TryParse(filter.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    throw new LinkbaseValidationException($"'{filter.Value}' isn't a number.");
                }

                return $"({column})::numeric {Comparison(filter.Operator)} {add(number, NpgsqlDbType.Numeric)}";
            case GraphValueType.Date:
                if (!DateOnly.TryParse(filter.Value, CultureInfo.InvariantCulture, out var date))
                {
                    throw new LinkbaseValidationException($"'{filter.Value}' isn't a date. Use the form 2024-06-30.");
                }

                return $"({column})::date {Comparison(filter.Operator)} {add(date, NpgsqlDbType.Date)}";
            case GraphValueType.Boolean:
                return $"{column} = {add(filter.Value.Trim().ToLowerInvariant(), NpgsqlDbType.Text)}";
            case GraphValueType.List when filter.Key != "name":
                return ListSql(propsColumn, filter, add);
            default:
                return TextSql(column, filter, add);
        }
    }

    // Some types take their name from another node, so they match on their own properties rather than flooding every search.
    private static string AnyFieldSql(int index, PatternFilter filter, Func<object, NpgsqlDbType, string> add)
    {
        var node = $"n{index}";
        var like = add($"%{EscapeLike(filter.Value)}%", NpgsqlDbType.Text);
        var term = add(filter.Value, NpgsqlDbType.Text);
        var borrowed = add(RegisteredDomains.LabelsNamedAfterParent, NpgsqlDbType.Array | NpgsqlDbType.Text);

        // search_text holds the name too, so a type named after another node would match on that borrowed name and repeat it in
        // every one of them. Those match only where the text is in their own values: graph_search_text with an empty name. Both
        // arms start with search_text ILIKE, so the trigram index still finds the rows and the second check only trims them.
        // % is a near miss on the name, up to pg_trgm's similarity threshold. Unlike similarity(), it can use an index.
        return $"(({node}.search_text ILIKE {like} AND ({node}.label <> ALL({borrowed}) OR graph_search_text('', {node}.props) ILIKE {like})) OR ({node}.label <> ALL({borrowed}) AND {node}.name % {term}))";
    }

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static string TextSql(string column, PatternFilter filter, Func<object, NpgsqlDbType, string> add)
    {
        var escaped = EscapeLike(filter.Value);
        return filter.Operator switch
        {
            FilterOperator.Equals => $"lower({column}) = lower({add(filter.Value, NpgsqlDbType.Text)})",
            FilterOperator.Contains => $"{column} ILIKE {add($"%{escaped}%", NpgsqlDbType.Text)}",
            FilterOperator.StartsWith => $"{column} ILIKE {add($"{escaped}%", NpgsqlDbType.Text)}",
            FilterOperator.EndsWith => $"{column} ILIKE {add($"%{escaped}", NpgsqlDbType.Text)}",
            _ => throw new LinkbaseValidationException($"That comparison doesn't work on text like '{filter.Key}'."),
        };
    }

    // A list matches when any of its values does. A value that isn't an array, from sampled data, counts as one value.
    private static string ListSql(string propsColumn, PatternFilter filter, Func<object, NpgsqlDbType, string> add)
    {
        var json = $"{propsColumn}->'{filter.Key}'";
        var values = $"jsonb_array_elements_text(CASE jsonb_typeof({json}) WHEN 'array' THEN {json} ELSE jsonb_build_array({json}) END)";
        return $"EXISTS (SELECT 1 FROM {values} item WHERE {TextSql("item", filter, add)})";
    }

    private static string Comparison(FilterOperator op) => op switch
    {
        FilterOperator.Equals => "=",
        FilterOperator.GreaterThan => ">",
        FilterOperator.GreaterOrEqual => ">=",
        FilterOperator.LessThan => "<",
        FilterOperator.LessOrEqual => "<=",
        _ => throw new LinkbaseValidationException("That comparison only works on text."),
    };

    private static GraphValueType? ValueType(GraphSchema schema, string? label, string key)
    {
        var found = schema.Properties.FirstOrDefault(p => p.Key == key && (label is null || p.Label == label));
        return found?.Type;
    }

    private static GraphValueType? LinkValueType(GraphSchema schema, string relType, string key) =>
        schema.LinkProperties.FirstOrDefault(p => p.RelType == relType && p.Key == key)?.Type;

    [GeneratedRegex("^[a-z][a-z_]*$")]
    private static partial Regex PropertyKey();
}
