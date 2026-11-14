using Linkbase.Data;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Security;

// A label written in a model file, as a default for every record of a type or every link of a relationship: a level's value,
// and the values of the two sets. Anything left out asks for nothing.
public sealed class ModelLabel
{
    public string? Level { get; init; }
    public IReadOnlyList<string>? AnyOf { get; init; }
    public IReadOnlyList<string>? AllOf { get; init; }
}

// The columns of a CSV file that hold each part of a record's or link's label. A blank cell takes the default. The sets are
// split by |, as a field's several values are.
public sealed class ModelLabelColumns
{
    public string? Level { get; init; }
    public string? AnyOf { get; init; }
    public string? AllOf { get; init; }
}

// The label columns of the INSERT that loads a type's records or a relationship's links. A model with no security section, or
// a type or relationship with no label, adds nothing, and the columns keep their defaults.
public sealed class LoaderLabels
{
    private static readonly (string, object, NpgsqlDbType)[] None = [];

    private LoaderLabels(string columns, string select, (string, object, NpgsqlDbType)[] parameters, Func<NpgsqlConnection, NpgsqlTransaction?, CancellationToken, Task> validate)
    {
        Columns = columns;
        Select = select;
        Parameters = parameters;
        Validate = validate;
    }

    // To add to the INSERT's column list and its SELECT list, each starting with a comma.
    public string Columns { get; }
    public string Select { get; }
    public (string Name, object Value, NpgsqlDbType Type)[] Parameters { get; }

    // Stops the load, naming the value, if a staged cell holds a level or value the model doesn't allow.
    public Func<NpgsqlConnection, NpgsqlTransaction?, CancellationToken, Task> Validate { get; }

    public static LoaderLabels For(ModelSecurity? security, ModelLabel? defaults, ModelLabelColumns? columns, Func<string, string> column, string what)
    {
        if (security is not { Enabled: true } || (defaults is null && columns is null))
        {
            return new LoaderLabels("", "", None, (_, _, _) => Task.CompletedTask);
        }

        var parameters = new List<(string, object, NpgsqlDbType)>();
        var checks = new List<(string Sql, string Message)>();
        var levels = security.Level?.Values?.ToArray() ?? [];
        var any = defaults?.AnyOf is { Count: > 0 } a ? a.ToArray() : [LabelCodec.Everyone];
        var all = defaults?.AllOf?.ToArray() ?? [];
        parameters.Add(("labelLevel", (short)Math.Max(0, security.LevelRank(defaults?.Level)), NpgsqlDbType.Smallint));
        parameters.Add(("labelAny", any, NpgsqlDbType.Array | NpgsqlDbType.Text));
        parameters.Add(("labelAll", all, NpgsqlDbType.Array | NpgsqlDbType.Text));

        var level = "@labelLevel::smallint";
        if (columns?.Level is { } levelColumn)
        {
            var c = column(levelColumn);
            parameters.Add(("levels", levels, NpgsqlDbType.Array | NpgsqlDbType.Text));
            level = $"coalesce((array_position(@levels, nullif(btrim({c}), '')) - 1)::smallint, @labelLevel::smallint)";
            checks.Add(($"SELECT btrim({c}) FROM staging WHERE nullif(btrim({c}), '') IS NOT NULL AND array_position(@levels, btrim({c})) IS NULL LIMIT 1",
                $"{what}'s {security.Level?.Name ?? "level"} column '{levelColumn}' has the value '{{0}}', which isn't one of {string.Join(", ", levels)}."));
        }

        var anySql = "@labelAny";
        if (columns?.AnyOf is { } anyColumn)
        {
            anySql = SetSql(column(anyColumn), "@labelAny");
            checks.Add((SetCheck(column(anyColumn), security.AnyOf!, parameters, "anyValues"), $"{what}'s {security.AnyOf?.Name ?? "anyOf"} column '{anyColumn}' has the value '{{0}}', which isn't allowed."));
        }

        var allSql = "@labelAll";
        if (columns?.AllOf is { } allColumn)
        {
            allSql = SetSql(column(allColumn), "@labelAll");
            checks.Add((SetCheck(column(allColumn), security.AllOf!, parameters, "allValues"), $"{what}'s {security.AllOf?.Name ?? "allOf"} column '{allColumn}' has the value '{{0}}', which isn't allowed."));
        }

        async Task Validate(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken token)
        {
            foreach (var (sql, message) in checks)
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                foreach (var (name, value, type) in parameters.Where(p => sql.Contains('@' + p.Item1, StringComparison.Ordinal)))
                {
                    command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
                }

                if (await command.ExecuteScalarAsync(token) is string bad)
                {
                    throw new LinkbaseValidationException(string.Format(System.Globalization.CultureInfo.InvariantCulture, message, bad));
                }
            }
        }

        return new LoaderLabels(", label_level, label_any, label_all", $", {level}, {anySql}, {allSql}", [.. parameters], Validate);
    }

    // The cell's values split by |, trimmed, without blanks or repeats; the default if there are none.
    private static string SetSql(string column, string fallback) =>
        $"coalesce(nullif(ARRAY(SELECT DISTINCT btrim(x) FROM unnest(string_to_array({column}, '|')) x WHERE btrim(x) <> '' ORDER BY 1), '{{}}'::text[]), {fallback})";

    // A value is wrong if it has a forbidden character or is too long, or the model lists its values and it isn't one.
    private static string SetCheck(string column, SecurityPart part, List<(string, object, NpgsqlDbType)> parameters, string valuesName)
    {
        var listed = "";
        if (part.Values is { Count: > 0 } values)
        {
            parameters.Add((valuesName, values.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
            listed = $" OR NOT btrim(x) = ANY(@{valuesName})";
        }

        return $$"""
            SELECT btrim(x) FROM staging, unnest(string_to_array({{column}}, '|')) x
            WHERE btrim(x) <> '' AND (length(btrim(x)) > {{ModelSecurity.MaxValue}} OR btrim(x) ~ '[{},"\\*[:cntrl:]]'{{listed}}) LIMIT 1
            """;
    }
}
