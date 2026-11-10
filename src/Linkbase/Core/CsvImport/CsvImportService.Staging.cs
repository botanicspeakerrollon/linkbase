using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsvHelper;
using CsvHelper.Configuration;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.CsvImport;

public sealed partial class CsvImportService
{
    private sealed record RecordRow(string Key, string Name, string Props);

    // Choices are each choice field's options: the ones it already has, or, for a new field, the values in its column.
    private async Task<(List<RecordRow> Rows, List<string> Problems, List<string> Notes, int Skipped, Dictionary<string, List<string>> Choices)> ReadRecordsAsync(
        CsvFile file, CsvRecordMapping mapping, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (!ModelFile.TypeName().IsMatch(mapping.Type))
        {
            problems.Add("A type's name is letters and digits, starting with a capital, such as Supplier.");
        }

        foreach (var field in mapping.Fields)
        {
            if (!ModelFile.FieldKey().IsMatch(field.Key) || field.Key == "name")
            {
                problems.Add($"The field {field.Key} needs a name in lower case letters, digits and underscores, other than name.");
            }
        }

        if (mapping.Fields.GroupBy(f => f.Key).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            problems.Add($"Two columns are both going into {repeated.Key}.");
        }

        // A field the type already has keeps its type, and a choice field its options. A required field needs a column, and
        // a type that requires links can't be imported, because a records import adds no links.
        var notes = new List<string>();
        var choices = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.NodeTypes.AsNoTracking().Where(t => t.Name == mapping.Type).Select(t => t.Owner).SingleOrDefaultAsync(cancellationToken) is { } owner)
            {
                problems.Add($"{mapping.Type} records are derived by {owner} from other records, so they can't be imported.");
            }

            var defined = await db.FieldDefinitions.AsNoTracking().Where(f => f.NodeTypeName == mapping.Type)
                .Select(f => new { f.Key, f.Type, f.Options, f.Required, f.Owner }).ToListAsync(cancellationToken);

            // A kept field's module fills it in, so it needs no column.
            foreach (var field in defined.Where(d => d.Required && d.Owner is null))
            {
                required.Add(field.Key);
                if (mapping.Fields.All(f => f.Key != field.Key))
                {
                    problems.Add($"{mapping.Type} requires {field.Key}, so choose a column for it.");
                }
            }

            var links = await db.LinkDefinitions.AsNoTracking().Where(l => l.SourceType == mapping.Type && l.Required)
                .OrderBy(l => l.RelType).ThenBy(l => l.TargetType)
                .Select(l => new { l.RelType, l.TargetType }).ToListAsync(cancellationToken);
            foreach (var link in links)
            {
                problems.Add($"Each {mapping.Type} needs at least one {mapping.Type} {link.RelType} {link.TargetType} link, which an import of records can't add. Add {mapping.Type} records in the app instead.");
            }

            foreach (var field in mapping.Fields)
            {
                var existing = defined.FirstOrDefault(d => d.Key == field.Key);
                if (existing?.Owner is not null)
                {
                    problems.Add($"{mapping.Type}'s {field.Key} is kept by {existing.Owner}, so it can't be imported. Untick its column.");
                }
                else if (existing is not null && existing.Type != field.Type)
                {
                    problems.Add($"{mapping.Type} already has {field.Key} as {Labelled(existing.Type)}, not {Labelled(field.Type)}.");
                }
                else if (field.Type == FieldType.Choice && existing is not null)
                {
                    choices[field.Key] = existing.Options ?? [];
                }
                else if (field.Type == FieldType.Choice)
                {
                    var found = ChoicesIn(file.Path, field.Column);
                    if (found.Count == 0)
                    {
                        problems.Add($"{field.Key} has no values to make choices from.");
                    }
                    else if (found.Count > FieldValues.MaxOptions)
                    {
                        problems.Add($"{field.Key} has more than {FieldValues.MaxOptions} different values, too many for a choice. Import it as text.");
                    }
                    else
                    {
                        choices[field.Key] = found;
                        notes.Add($"{field.Key} becomes a choice of {FieldValues.Listed(found)}.");
                    }
                }
            }
        }

        var rows = new List<RecordRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var (empty, repeats, bad) = (0, 0, 0);
        if (problems.Count == 0)
        {
            using var reader = Open(file.Path);
            reader.Read();
            reader.ReadHeader();
            var line = 1;
            while (reader.Read())
            {
                line++;
                var key = reader.GetField(mapping.KeyColumn)?.Trim() ?? "";
                if (key.Length == 0)
                {
                    empty++;
                    continue;
                }

                if (!seen.Add(key))
                {
                    repeats++;
                    continue;
                }

                var name = mapping.NameColumn is { } nameColumn ? reader.GetField(nameColumn)?.Trim() : null;
                var props = new JsonObject();
                foreach (var field in mapping.Fields)
                {
                    // A list of nothing, such as "|", is no value, like an empty cell.
                    var text = reader.GetField(field.Column)?.Trim() ?? "";
                    if (text.Length == 0 || (field.Type == FieldType.List && FieldValues.SplitList(text, FieldValues.ListSeparator).Count == 0))
                    {
                        if (required.Contains(field.Key) && ++bad <= ProblemsShown)
                        {
                            problems.Add($"Row {line}: {field.Key} is empty, and {mapping.Type} requires it.");
                        }

                        continue;
                    }

                    var options = choices.GetValueOrDefault(field.Key) ?? [];
                    if (Typed(text, field.Type, options) is { } value)
                    {
                        props[field.Key] = value;
                    }
                    else if (++bad <= ProblemsShown)
                    {
                        problems.Add(field.Type == FieldType.Choice
                            ? $"Row {line}: {field.Key} is \"{text}\", which isn't one of {FieldValues.Listed(options)}."
                            : $"Row {line}: {field.Key} is \"{text}\", which isn't {Labelled(field.Type)}.");
                    }
                }

                rows.Add(new RecordRow(key, string.IsNullOrEmpty(name) ? key : name, props.ToJsonString()));
            }
        }

        if (bad > ProblemsShown)
        {
            problems.Add($"And {bad - ProblemsShown:N0} more values that are missing or don't fit their field's type.");
        }

        if (empty > 0)
        {
            notes.Add($"{empty:N0} row(s) with no key are skipped.");
        }

        if (repeats > 0)
        {
            notes.Add($"{repeats:N0} row(s) repeat a key already in the file, so only the first of each is used.");
        }

        return (rows, problems, notes, empty + repeats, choices);
    }

    private static async Task<(List<(string Source, string Target, string Props)> Pairs, List<string> Problems, List<string> Notes, int Skipped)> ReadLinksAsync(
        LinkbaseDbContext db, CsvFile file, CsvLinkMapping mapping, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (!ModelFile.RelType().IsMatch(mapping.RelType))
        {
            problems.Add("A relationship's name is capitals, digits and underscores, such as SUPPLIES.");
        }

        var owners = await db.NodeTypes.AsNoTracking().Select(t => new { t.Name, t.Owner }).ToDictionaryAsync(t => t.Name, t => t.Owner, cancellationToken);
        foreach (var type in new[] { mapping.FromType, mapping.ToType }.Distinct())
        {
            if (!owners.TryGetValue(type, out var owner))
            {
                problems.Add($"There's no type called {type}. Import its records first.");
            }
            else if (owner is not null)
            {
                problems.Add($"{type} records are derived by {owner} from other records, so links to them can't be imported.");
            }
        }

        var pairs = new List<(string Source, string Target, string Props)>();
        var notes = new List<string>();

        // The relationship's attributes, which the model has already: a file can fill them but not define them.
        var attributes = await RecordRepository.AttributesAsync(db, mapping.FromType, mapping.RelType, mapping.ToType, cancellationToken);
        var mapped = mapping.Attributes ?? [];
        foreach (var column in mapped.Where(m => attributes.All(a => a.Key != m.Key)))
        {
            problems.Add($"{mapping.FromType} {mapping.RelType} {mapping.ToType} has no value called {column.Key}. Links carry only the values the model defines for them.");
        }

        foreach (var required in attributes.Where(a => a.Required && mapped.All(m => m.Key != a.Key)))
        {
            problems.Add($"{RecordRepository.Title(required.Key)} is required on every {mapping.FromType} {mapping.RelType} {mapping.ToType} link, so it needs a column.");
        }

        if (problems.Count > 0)
        {
            return (pairs, problems, notes, 0);
        }

        var rows = new List<(int Line, string From, string To, Dictionary<string, string?> Values)>();
        using (var reader = Open(file.Path))
        {
            reader.Read();
            reader.ReadHeader();
            var line = 1;
            while (reader.Read())
            {
                line++;
                // A list's values are split by | in a file, and one per line when checked.
                var values = mapped.ToDictionary(
                    m => m.Key,
                    m => (string?)(attributes.First(a => a.Key == m.Key).Type == FieldType.List
                        ? reader.GetField(m.Column)?.Replace('|', '\n')
                        : reader.GetField(m.Column)));
                rows.Add((line, reader.GetField(mapping.FromColumn)?.Trim() ?? "", reader.GetField(mapping.ToColumn)?.Trim() ?? "", values));
            }
        }

        var fromKeys = await ExistingKeysAsync(db, mapping.FromType, rows.Select(r => r.From).Where(k => k.Length > 0).Distinct().ToList(), cancellationToken);
        var toKeys = await ExistingKeysAsync(db, mapping.ToType, rows.Select(r => r.To).Where(k => k.Length > 0).Distinct().ToList(), cancellationToken);
        var seen = new HashSet<(string, string, string)>();
        var (empty, missing, repeats) = (0, 0, 0);
        var missingShown = new List<string>();
        foreach (var (line, from, to, values) in rows)
        {
            if (from.Length == 0 || to.Length == 0)
            {
                empty++;
            }
            else if (!fromKeys.Contains(from) || !toKeys.Contains(to))
            {
                if (++missing <= 3)
                {
                    missingShown.Add(!fromKeys.Contains(from) ? $"row {line} has no {mapping.FromType} {from}" : $"row {line} has no {mapping.ToType} {to}");
                }
            }
            else
            {
                string props;
                try
                {
                    props = RecordRepository.Props(attributes, values);
                }
                catch (LinkbaseValidationException ex)
                {
                    if (problems.Count < ProblemsShown)
                    {
                        problems.Add($"Row {line}: {ex.Message}");
                    }

                    continue;
                }

                if (!seen.Add((from, to, props)))
                {
                    repeats++;
                }
                else
                {
                    pairs.Add(($"{mapping.FromType.ToLowerInvariant()}:{from}", $"{mapping.ToType.ToLowerInvariant()}:{to}", props));
                }
            }
        }

        if (empty > 0)
        {
            notes.Add($"{empty:N0} row(s) with an end missing are skipped.");
        }

        if (missing > 0)
        {
            notes.Add($"{missing:N0} row(s) name a record that isn't there and are skipped, such as: {string.Join("; ", missingShown)}.");
        }

        if (repeats > 0)
        {
            notes.Add($"{repeats:N0} row(s) repeat a link already in the file{(attributes.Count > 0 ? " with the same values" : "")}.");
        }

        return (pairs, problems, notes, empty + missing + repeats);
    }

    private static async Task<HashSet<string>> ExistingKeysAsync(LinkbaseDbContext db, string type, List<string> keys, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(10_000))
        {
            found.UnionWith(await db.Nodes.AsNoTracking()
                .Where(n => n.Type == type && chunk.Contains(n.Key))
                .Select(n => n.Key)
                .ToListAsync(cancellationToken));
        }

        return found;
    }

    // The links the model already has between the same two records with the same values. A row with no values is there when
    // the two are linked that way at all.
    private static async Task<HashSet<(string Source, string Target, string Props)>> ExistingLinksAsync(
        LinkbaseDbContext db, string relType, List<(string Source, string Target, string Props)> pairs, CancellationToken cancellationToken)
    {
        // In a transaction only so the staging table lasts until the lookup, and goes when it's rolled back.
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await StageLinksAsync(connection, pairs, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT s.idx FROM staging_link s WHERE EXISTS (SELECT 1 FROM node_link l WHERE l.source_id = s.source_id AND l.rel_type = @rel AND l.target_id = s.target_id AND (s.props = '{}'::jsonb OR l.props = s.props))",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = relType });
        var found = new HashSet<(string, string, string)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add(pairs[reader.GetInt32(0)]);
            }
        }

        await transaction.RollbackAsync(cancellationToken);
        return found;
    }

    private static async Task StageLinksAsync(NpgsqlConnection connection, List<(string Source, string Target, string Props)> pairs, CancellationToken cancellationToken)
    {
        await using (var create = new NpgsqlCommand("CREATE TEMP TABLE staging_link (idx int, source_id text, target_id text, props jsonb) ON COMMIT DROP", connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var import = await connection.BeginBinaryImportAsync("COPY staging_link (idx, source_id, target_id, props) FROM STDIN (FORMAT BINARY)", cancellationToken);
        for (var i = 0; i < pairs.Count; i++)
        {
            await import.StartRowAsync(cancellationToken);
            await import.WriteAsync(i, NpgsqlDbType.Integer, cancellationToken);
            await import.WriteAsync(pairs[i].Source, NpgsqlDbType.Text, cancellationToken);
            await import.WriteAsync(pairs[i].Target, NpgsqlDbType.Text, cancellationToken);
            await import.WriteAsync(pairs[i].Props, NpgsqlDbType.Jsonb, cancellationToken);
        }

        await import.CompleteAsync(cancellationToken);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value, NpgsqlDbType Type)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        foreach (var (name, value, type) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        }

        return await command.ExecuteNonQueryAsync();
    }

    private static void Stop(List<string> problems)
    {
        if (problems.Count > 0)
        {
            throw new LinkbaseValidationException(problems[0]);
        }
    }
}
