using System.Globalization;
using CsvHelper;
using Linkbase.Core.History;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Models;

public sealed record ModelLoadSummary(int Types, long Nodes, long Links, TimeSpan Took);

// Replaces everything in the generic store with a model file's types and data, in one transaction. Rows whose key is
// empty are skipped, a repeated key keeps its first row, and a link whose ends aren't both loaded is left out.
public sealed class ModelLoader(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public async Task<ModelLoadSummary> LoadAsync(string path, TextWriter output, CancellationToken cancellationToken = default)
    {
        var model = ModelFile.Read(path);
        var started = DateTime.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var session = new LoadSession(connection, transaction, output, cancellationToken);

        var indexes = await ReadIndexesAsync(session);
        await ClearAsync(session, indexes);
        await DefineTypesAsync(session, model);
        await DefineRelationshipsAsync(session, model);
        var nodes = await LoadNodesAsync(session, model);
        var links = await LoadLinksAsync(session, model);
        await RebuildIndexesAsync(session, indexes);
        await CheckModelAsync(session, model);
        await RecordLoadAsync(session, model, path, nodes, links);

        await transaction.CommitAsync(cancellationToken);
        return new ModelLoadSummary(model.Types.Count, nodes, links, DateTime.UtcNow - started);
    }

    // One load's connection and transaction, with the command runner every step shares.
    private sealed class LoadSession(NpgsqlConnection connection, NpgsqlTransaction transaction, TextWriter output, CancellationToken token)
    {
        public NpgsqlConnection Connection => connection;
        public NpgsqlTransaction Transaction => transaction;
        public TextWriter Output => output;
        public CancellationToken Token => token;

        public async Task<long> RunAsync(string sql, params (string Name, object Value, NpgsqlDbType Type)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
            foreach (var (name, value, type) in parameters)
            {
                command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
            }

            return await command.ExecuteNonQueryAsync(token);
        }
    }

    // Indexes are rebuilt once at the end, which is much faster than keeping them up to date row by row.
    private static async Task<List<string>> ReadIndexesAsync(LoadSession session)
    {
        var indexes = new List<string>();
        await using (var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE tablename IN ('node', 'node_link') AND indexname NOT LIKE 'pk_%'", session.Connection, session.Transaction))
        await using (var reader = await command.ExecuteReaderAsync(session.Token))
        {
            while (await reader.ReadAsync(session.Token))
            {
                indexes.Add(reader.GetString(0));
            }
        }
        return indexes;
    }

    private static async Task ClearAsync(LoadSession session, List<string> indexes)
    {
        // The refs go with the nodes. Refs to climbing rows stay.
        await session.RunAsync("DELETE FROM external_ref WHERE node_id IS NOT NULL");
        await session.RunAsync("TRUNCATE node_link, node, link_attribute, link_definition, field_definition, hierarchy_level, node_type");
        foreach (var index in indexes)
        {
            await session.RunAsync($"DROP INDEX {IndexName(index)}");
        }
    }

    private static async Task DefineTypesAsync(LoadSession session, ModelFile model)
    {
        for (var i = 0; i < model.Types.Count; i++)
        {
            var type = model.Types[i];
            await session.RunAsync(
                "INSERT INTO node_type (name, color, icon, sort_order, owner, hierarchy_field, hierarchy_rel, name_template) VALUES (@name, @color, @icon, @order, @owner, @hierarchyField, @hierarchyRel, @template)",
                ("name", type.Name, NpgsqlDbType.Text),
                ("color", (object?)type.Color ?? DBNull.Value, NpgsqlDbType.Text),
                ("icon", (object?)type.Icon ?? DBNull.Value, NpgsqlDbType.Text),
                ("order", i, NpgsqlDbType.Integer),
                ("owner", (object?)type.Owner ?? DBNull.Value, NpgsqlDbType.Text),
                ("hierarchyField", (object?)type.Hierarchy?.By ?? DBNull.Value, NpgsqlDbType.Text),
                ("hierarchyRel", (object?)type.Hierarchy?.Via ?? DBNull.Value, NpgsqlDbType.Text),
                ("template", (object?)type.Template ?? DBNull.Value, NpgsqlDbType.Text));
            foreach (var level in type.Hierarchy?.Levels ?? [])
            {
                await session.RunAsync(
                    "INSERT INTO hierarchy_level (node_type_name, kind, is_root, under) VALUES (@type, @kind, @root, @under)",
                    ("type", type.Name, NpgsqlDbType.Text),
                    ("kind", level.Kind, NpgsqlDbType.Text),
                    ("root", level.Root, NpgsqlDbType.Boolean),
                    ("under", (level.Under ?? []).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
            }
            foreach (var field in type.Fields)
            {
                await session.RunAsync(
                    "INSERT INTO field_definition (node_type_name, key, type, options, required, owner, description, group_name, multiple) VALUES (@type, @key, @fieldType, @options, @required, @owner, @description, @group, @multiple)",
                    ("type", type.Name, NpgsqlDbType.Text),
                    ("key", field.Key, NpgsqlDbType.Text),
                    ("fieldType", SnakeCaseEnumConverter<FieldType>.ToStore(StoredType(field)), NpgsqlDbType.Text),
                    ("options", field.Options is { Count: > 0 } options ? options.Select(o => o.Trim()).ToArray() : (object)DBNull.Value, NpgsqlDbType.Array | NpgsqlDbType.Text),
                    ("required", field.Required, NpgsqlDbType.Boolean),
                    ("owner", (object?)field.Owner ?? DBNull.Value, NpgsqlDbType.Text),
                    ("description", (object?)Clean(field.Description) ?? DBNull.Value, NpgsqlDbType.Text),
                    ("group", (object?)Clean(field.Group) ?? DBNull.Value, NpgsqlDbType.Text),
                    ("multiple", field.Multiple, NpgsqlDbType.Boolean));
            }
        }
    }

    private static async Task DefineRelationshipsAsync(LoadSession session, ModelFile model)
    {
        foreach (var relationship in model.Relationships)
        {
            // The same relationship can come from more than one file. It's required if any of them says so.
            await session.RunAsync(
                """
                INSERT INTO link_definition (source_type, rel_type, target_type, required, max_links) VALUES (@from, @rel, @to, @required, @max)
                ON CONFLICT (source_type, rel_type, target_type) DO UPDATE SET required = link_definition.required OR excluded.required,
                    max_links = least(link_definition.max_links, excluded.max_links)
                """,
                ("from", relationship.From, NpgsqlDbType.Text),
                ("rel", relationship.Type, NpgsqlDbType.Text),
                ("to", relationship.To, NpgsqlDbType.Text),
                ("required", relationship.Required, NpgsqlDbType.Boolean),
                ("max", (object?)relationship.Max ?? DBNull.Value, NpgsqlDbType.Integer));

            // The same relationship can come from more than one file. The first to name an attribute defines it.
            foreach (var field in relationship.Fields)
            {
                await session.RunAsync(
                    """
                    INSERT INTO link_attribute (link_definition_id, key, type, options, required, description, multiple)
                    SELECT id, @key, @fieldType, @options, @required, @description, @multiple FROM link_definition
                    WHERE source_type = @from AND rel_type = @rel AND target_type = @to
                    ON CONFLICT (link_definition_id, key) DO NOTHING
                    """,
                    ("from", relationship.From, NpgsqlDbType.Text),
                    ("rel", relationship.Type, NpgsqlDbType.Text),
                    ("to", relationship.To, NpgsqlDbType.Text),
                    ("key", field.Key, NpgsqlDbType.Text),
                    ("fieldType", SnakeCaseEnumConverter<FieldType>.ToStore(StoredType(field)), NpgsqlDbType.Text),
                    ("options", field.Options is { Count: > 0 } options ? options.Select(o => o.Trim()).ToArray() : (object)DBNull.Value, NpgsqlDbType.Array | NpgsqlDbType.Text),
                    ("required", field.Required, NpgsqlDbType.Boolean),
                    ("description", (object?)Clean(field.Description) ?? DBNull.Value, NpgsqlDbType.Text),
                    ("multiple", field.Multiple, NpgsqlDbType.Boolean));
            }
        }
    }

    private static async Task<long> LoadNodesAsync(LoadSession session, ModelFile model)
    {
        long nodes = 0;
        foreach (var type in model.Types)
        {
            // A type with no source starts empty.
            if (type.Source is not { } typeSource)
            {
                continue;
            }

            var columns = await StageAsync(session.Connection, model, typeSource.File, session.Token);
            var key = Column(columns, typeSource.File, typeSource.Key);
            var name = type.Template is { } template ? TemplateSql(template, type.Fields, columns, typeSource.File)
                : typeSource.Name is null ? key : Column(columns, typeSource.File, typeSource.Name);
            await CheckChoicesAsync(session.Connection, session.Transaction, type.Fields, columns, typeSource.File, session.Token);
            await CheckRequiredAsync(session.Connection, session.Transaction, type.Fields, columns, typeSource.File, $"nullif({key}, '') IS NOT NULL", session.Token);
            var count = await Guard($"{type.Name} from {typeSource.File}", () => session.RunAsync(
                $"""
                INSERT INTO node (id, user_id, type, key, name, props)
                SELECT lower(@type) || ':' || {key}, @user, @type, {key}, coalesce(nullif({name}, ''), {key}),
                       {Props(type.Fields, columns, typeSource.File)}
                FROM staging
                WHERE nullif({key}, '') IS NOT NULL
                ON CONFLICT (id) DO NOTHING
                """,
                ("type", type.Name, NpgsqlDbType.Text),
                ("user", AppUser.DefaultId, NpgsqlDbType.Bigint)));
            await session.RunAsync("DROP TABLE staging");
            await session.Output.WriteLineAsync($"{type.Name}: {count:N0} from {typeSource.File}");
            nodes += count;
        }
        return nodes;
    }

    private static async Task<long> LoadLinksAsync(LoadSession session, ModelFile model)
    {
        long links = 0;
        foreach (var relationship in model.Relationships)
        {
            if (relationship.Source is not { } source)
            {
                continue;
            }

            var columns = await StageAsync(session.Connection, model, source.File, session.Token);
            var from = Column(columns, source.File, source.FromColumn);
            var to = Column(columns, source.File, source.ToColumn);
            await CheckChoicesAsync(session.Connection, session.Transaction, relationship.Fields, columns, source.File, session.Token);
            await CheckRequiredAsync(session.Connection, session.Transaction, relationship.Fields, columns, source.File,
                $"nullif({from}, '') IS NOT NULL AND nullif({to}, '') IS NOT NULL", session.Token);
            var count = await Guard($"{relationship.From} {relationship.Type} {relationship.To} from {source.File}", () => session.RunAsync(
                $"""
                INSERT INTO node_link (source_id, target_id, rel_type, props)
                SELECT f.id, t.id, @rel, {Props(relationship.Fields, columns, source.File)}
                FROM staging
                JOIN node f ON f.id = lower(@from) || ':' || {from}
                JOIN node t ON t.id = lower(@to) || ':' || {to}
                """,
                ("rel", relationship.Type, NpgsqlDbType.Text),
                ("from", relationship.From, NpgsqlDbType.Text),
                ("to", relationship.To, NpgsqlDbType.Text)));
            await session.RunAsync("DROP TABLE staging");
            await session.RunAsync(
                "UPDATE link_definition SET links = links + @count WHERE source_type = @from AND rel_type = @rel AND target_type = @to",
                ("count", count, NpgsqlDbType.Bigint),
                ("from", relationship.From, NpgsqlDbType.Text),
                ("rel", relationship.Type, NpgsqlDbType.Text),
                ("to", relationship.To, NpgsqlDbType.Text));
            await session.Output.WriteLineAsync($"{relationship.From} {relationship.Type} {relationship.To}: {count:N0} from {source.File}");
            links += count;
        }
        return links;
    }

    private static async Task RebuildIndexesAsync(LoadSession session, List<string> indexes)
    {
        await session.Output.WriteLineAsync("Rebuilding indexes");
        foreach (var index in indexes)
        {
            await session.RunAsync(index);
        }

        await session.RunAsync("ANALYZE node");
        await session.RunAsync("ANALYZE node_link");
    }

    private static async Task CheckModelAsync(LoadSession session, ModelFile model)
    {
        foreach (var relationship in model.Relationships.Where(r => r.Required))
        {
            await CheckRequiredLinksAsync(session.Connection, session.Transaction, relationship, session.Token);
        }

        foreach (var relationship in model.Relationships.Where(r => r.Max is not null))
        {
            await CheckMaxLinksAsync(session.Connection, session.Transaction, relationship, session.Token);
        }

        await HierarchyChecks.CheckAsync(session.Connection, session.Transaction, batchId: null, requireParent: true, session.Token);
    }

    private static async Task RecordLoadAsync(LoadSession session, ModelFile model, string path, long nodes, long links)
    {
        // The model file's history settings. A reload keeps the history already written, because the changes have no foreign
        // key to the records they describe. It adds one entry for the load itself.
        await session.RunAsync(
            """
            INSERT INTO model_setting (id, record_history, require_reason) VALUES (1, @record, @reason)
            ON CONFLICT (id) DO UPDATE SET record_history = excluded.record_history, require_reason = excluded.require_reason
            """,
            ("record", model.RecordHistory, NpgsqlDbType.Boolean),
            ("reason", model.RequireReason, NpgsqlDbType.Boolean));
        if (model.RecordHistory)
        {
            var summary = System.Text.Json.JsonSerializer.Serialize(new { types = model.Types.Count, records = nodes, links });
            await ChangeRecorder.WriteAsync(session.Connection, session.Transaction, JustificationKind.Import, $"Loaded the {model.Name} model from {Path.GetFileName(path)}", null,
                [new PendingChange(ChangeEntityKind.Definition, $"model:{model.Name}", ChangeOp.Update, NewValue: summary)], cancellationToken: session.Token);
        }
    }

    private static string IndexName(string definition) =>
        definition.Split(' ', StringSplitOptions.RemoveEmptyEntries)[definition.StartsWith("CREATE UNIQUE", StringComparison.Ordinal) ? 3 : 2];

    // Copies a CSV file into a temporary table called staging, with a text column per header column, named c0, c1 and so
    // on, so nothing from the file ever reaches the SQL text. Returns the header names in column order.
    private static async Task<List<string>> StageAsync(NpgsqlConnection connection, ModelFile model, string file, CancellationToken cancellationToken)
    {
        var path = Path.Combine(model.DataFolder, file);
        if (!File.Exists(path))
        {
            throw new LinkbaseValidationException($"The model refers to {file}, which isn't in {model.DataFolder}.");
        }

        List<string> header;
        using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
        using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
        {
            if (!await csv.ReadAsync())
            {
                throw new LinkbaseValidationException($"{file} is empty.");
            }

            csv.ReadHeader();
            header = csv.HeaderRecord!.Select(h => h.Trim()).ToList();
        }

        var columns = string.Join(", ", header.Select((_, i) => $"c{i} text"));
        await using (var create = new NpgsqlCommand($"CREATE TEMP TABLE staging ({columns}) ON COMMIT DROP", connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var import = await connection.BeginTextImportAsync("COPY staging FROM STDIN WITH (FORMAT csv, HEADER true)", cancellationToken))
        using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
        {
            var buffer = new char[1 << 16];
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await import.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        return header;
    }

    private static string Column(List<string> header, string file, string name)
    {
        var index = header.IndexOf(name);
        return index >= 0 ? $"c{index}" : throw new LinkbaseValidationException($"{file} has no column '{name}'. It has: {string.Join(", ", header)}.");
    }

    // Keys were checked by ModelFile, so they're safe in the SQL text. Values come from staging columns.
    private static string Props(IReadOnlyList<ModelField> fields, List<string> header, string file)
    {
        if (fields.Count == 0)
        {
            return "'{}'::jsonb";
        }

        var pairs = fields.Select(field =>
        {
            var value = $"nullif({Column(header, file, field.Column ?? field.Key)}, '')";
            // Several values are split on the separator and each stored in its own type.
            var typed = field.IsMany
                ? $"(SELECT jsonb_agg({field.ElementType switch { FieldType.Number => "btrim(v)::numeric", FieldType.Date => "btrim(v)::date", _ => "btrim(v)" }} ORDER BY i) FROM unnest(string_to_array({value}, '{FieldValues.ListSeparator}')) WITH ORDINALITY u(v, i) WHERE btrim(v) <> '')"
                : field.Type switch
                {
                    FieldType.Number => $"{value}::numeric",
                    FieldType.Date => $"{value}::date",
                    FieldType.Boolean => $"{value}::boolean",
                    _ => value,
                };
            return $"'{field.Key}', {typed}";
        });
        return $"jsonb_strip_nulls(jsonb_build_object({string.Join(", ", pairs)}))";
    }

    // A value that isn't one of its choice field's options stops the load, and says which values. The model file's author
    // owns the data, so the match is exact: a value spelled differently from its option is a mistake to fix in the file.
    private static async Task CheckChoicesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<ModelField> fields, List<string> header, string file, CancellationToken cancellationToken)
    {
        foreach (var field in fields.Where(f => f.Type == FieldType.Choice))
        {
            var column = Column(header, file, field.Column ?? field.Key);
            await using var command = new NpgsqlCommand(
                field.IsMany
                    ? $"SELECT DISTINCT btrim(v) FROM staging, unnest(string_to_array({column}, '{FieldValues.ListSeparator}')) v WHERE btrim(v) <> '' AND NOT (btrim(v) = ANY(@options)) ORDER BY 1 LIMIT 5"
                    : $"SELECT DISTINCT {column} FROM staging WHERE nullif({column}, '') IS NOT NULL AND NOT ({column} = ANY(@options)) ORDER BY 1 LIMIT 5",
                connection, transaction);
            command.Parameters.Add(new NpgsqlParameter("options", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (field.Options ?? []).Select(o => o.Trim()).ToArray() });
            var wrong = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    wrong.Add(reader.GetString(0));
                }
            }

            if (wrong.Count > 0)
            {
                throw new LinkbaseValidationException(
                    $"{file}: {field.Key} has {string.Join(", ", wrong.Select(w => $"\"{w}\""))}, which {(wrong.Count == 1 ? "isn't one" : "aren't any")} of its options: {FieldValues.Listed(field.Options ?? [])}.");
            }
        }
    }

    // A row without a value for a required field stops the load, and says which rows, counting the header as row 1. Rows
    // the load skips don't count.
    private static async Task CheckRequiredAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<ModelField> fields, List<string> header, string file,
        string loaded, CancellationToken cancellationToken)
    {
        foreach (var field in fields.Where(f => f.Required))
        {
            var column = Column(header, file, field.Column ?? field.Key);
            var empty = field.IsMany
                ? $"NOT EXISTS (SELECT 1 FROM unnest(string_to_array({column}, '{FieldValues.ListSeparator}')) v WHERE btrim(v) <> '')"
                : $"nullif({column}, '') IS NULL";
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) OVER (), line FROM (SELECT row_number() OVER () + 1 AS line, * FROM staging) s WHERE {loaded} AND {empty} ORDER BY line LIMIT 5",
                connection, transaction);
            var lines = new List<long>();
            long total = 0;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    total = reader.GetInt64(0);
                    lines.Add(reader.GetInt64(1));
                }
            }

            if (total > 0)
            {
                throw new LinkbaseValidationException(
                    $"{file}: {field.Key} is required, but {total:N0} row(s) have none, such as row {string.Join(", ", lines)}.");
            }
        }
    }

    // A record with none of a required relationship's links stops the load, and says which.
    private static async Task CheckRequiredLinksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ModelRelationship relationship, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) OVER (), n.key
            FROM node n
            WHERE n.type = @from
              AND NOT EXISTS (SELECT 1 FROM node_link l JOIN node t ON t.id = l.target_id WHERE l.source_id = n.id AND l.rel_type = @rel AND t.type = @to)
            ORDER BY n.key
            LIMIT 5
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Text) { Value = relationship.From });
        command.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = relationship.Type });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = relationship.To });
        var keys = new List<string>();
        long total = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                total = reader.GetInt64(0);
                keys.Add(reader.GetString(1));
            }
        }

        if (total > 0)
        {
            throw new LinkbaseValidationException(
                $"Each {relationship.From} needs at least one {relationship.From} {relationship.Type} {relationship.To} link, but {total:N0} have none, such as {string.Join(", ", keys)}.");
        }
    }

    // A record with more of a relationship's links than its max stops the load, and says which.
    private static async Task CheckMaxLinksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ModelRelationship relationship, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) OVER (), s.key
            FROM (
                SELECT n.key
                FROM node n JOIN node_link l ON l.source_id = n.id AND l.rel_type = @rel JOIN node t ON t.id = l.target_id AND t.type = @to
                WHERE n.type = @from
                GROUP BY n.key
                HAVING count(*) > @max
            ) s
            ORDER BY s.key
            LIMIT 5
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Text) { Value = relationship.From });
        command.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = relationship.Type });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = relationship.To });
        command.Parameters.Add(new NpgsqlParameter("max", NpgsqlDbType.Integer) { Value = relationship.Max!.Value });
        var keys = new List<string>();
        long total = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                total = reader.GetInt64(0);
                keys.Add(reader.GetString(1));
            }
        }

        if (total > 0)
        {
            throw new LinkbaseValidationException(
                $"Each {relationship.From} can have at most {relationship.Max} {relationship.From} {relationship.Type} {relationship.To} link(s), but {total:N0} have more, such as {string.Join(", ", keys)}.");
        }
    }

    // A text list is stored as the List type, as it always was; any other several-valued field keeps its own type and is
    // marked multiple.
    internal static FieldType StoredType(ModelField field) => field.Multiple && field.Type == FieldType.Text ? FieldType.List : field.Type;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // The name column's SQL: the template's text with each {field} replaced by that field's column, spaces collapsed.
    private static string TemplateSql(string template, IReadOnlyList<ModelField> fields, List<string> header, string file)
    {
        var parts = new List<string>();
        var last = 0;
        foreach (var token in NameTemplates.Matches(template))
        {
            if (token.Index > last)
            {
                parts.Add($"'{template[last..token.Index].Replace("'", "''")}'");
            }

            var field = fields.First(f => f.Key == token.Key);
            parts.Add($"coalesce({Column(header, file, field.Column ?? field.Key)}, '')");
            last = token.Index + token.Length;
        }

        if (last < template.Length)
        {
            parts.Add($"'{template[last..].Replace("'", "''")}'");
        }

        return $"nullif(btrim(regexp_replace(concat({string.Join(", ", parts)}), '\\s+', ' ', 'g')), '')";
    }

    // A value that doesn't fit its field's type stops the load, and says where.
    private static async Task<long> Guard(string what, Func<Task<long>> run)
    {
        try
        {
            return await run();
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("22", StringComparison.Ordinal))
        {
            throw new LinkbaseValidationException($"Loading {what}: {ex.MessageText}. Check that field's type in the model file.");
        }
    }
}
