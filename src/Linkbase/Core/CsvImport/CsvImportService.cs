using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsvHelper;
using CsvHelper.Configuration;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.CsvImport;

// A CSV file's columns, each with a few values and the type they look like.
public sealed record CsvColumn(int Index, string Header, FieldType Guess, IReadOnlyList<string> Samples);

// Preview is the first few rows as they are in the file, to show what was uploaded.
public sealed record CsvFile(string Path, string FileName, IReadOnlyList<CsvColumn> Columns, int Rows)
{
    public IReadOnlyList<IReadOnlyList<string>> Preview { get; init; } = [];
}

public sealed record CsvFieldMapping(int Column, string Key, FieldType Type);

// Each row is a record of the type, identified by the key column. A new type and new fields are added to the model.
public sealed record CsvRecordMapping(string Type, int KeyColumn, int? NameColumn, IReadOnlyList<CsvFieldMapping> Fields);

// Each row links the record of one type whose key is in one column to the record of another whose key is in another.
public sealed record CsvLinkMapping(string FromType, int FromColumn, string RelType, string ToType, int ToColumn);

// What an import would do. Problems stop it; rows that are skipped don't.
public sealed record CsvImportPlan(int Rows, int ToAdd, int AlreadyThere, int Skipped, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool CanImport => Problems.Count == 0 && ToAdd > 0;
}

public sealed record CsvImportResult(long BatchId, int Added);

// What a file of links probably is, from its headers and the model. RelType is filled in when the model has exactly one
// relationship between the two types.
public sealed record CsvLinkGuess(string FromType, int FromColumn, string ToType, int ToColumn, string? RelType);

// The existing type a file of records most looks like, and why, such as "it has Person's fields title and joined".
public sealed record CsvTypeGuess(string Type, string Reason);

public sealed record CsvBatch(long Id, string File, string What, int Added, ImportStatus Status, DateTimeOffset At);

// Imports records or links from a CSV file into the generic store, in one transaction, and undoes an import. Records whose
// key is already there are left as they are, so undoing an import only ever removes what it added. See docs/models.md.
public sealed class CsvImportService(IDbContextFactory<LinkbaseDbContext> contextFactory)
{
    public const long MaxBytes = 100L * 1024 * 1024;
    private const int Samples = 5;
    private const int GuessRows = 1000;
    private const int ProblemsShown = 10;
    private const int PreviewRows = 5;

    // Saves an upload where the later steps can read it again, since a browser's file can only be read once.
    public static async Task<string> SaveAsync(Stream upload, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "linkbase-imports");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}.csv");
        await using var file = File.Create(path);
        await upload.CopyToAsync(file, cancellationToken);
        return path;
    }

    public static CsvFile Read(string path, string fileName)
    {
        using var reader = Open(path);
        if (!reader.Read())
        {
            throw new LinkbaseValidationException($"{fileName} is empty.");
        }

        reader.ReadHeader();
        var headers = reader.HeaderRecord!.Select(h => h.Trim()).ToArray();
        if (headers.Length == 0 || headers.All(h => h.Length == 0))
        {
            throw new LinkbaseValidationException($"{fileName} has no header row.");
        }

        var values = headers.Select(_ => new List<string>()).ToArray();
        var preview = new List<IReadOnlyList<string>>();
        var rows = 0;
        while (reader.Read())
        {
            rows++;
            if (preview.Count < PreviewRows)
            {
                preview.Add(headers.Select((_, i) => reader.GetField(i)?.Trim() ?? "").ToList());
            }

            if (rows > GuessRows)
            {
                continue;
            }

            for (var i = 0; i < headers.Length; i++)
            {
                var value = reader.GetField(i)?.Trim() ?? "";
                if (value.Length > 0)
                {
                    values[i].Add(value);
                }
            }
        }

        var columns = headers.Select((header, i) => new CsvColumn(i, header.Length == 0 ? $"Column {i + 1}" : header, Guess(values[i]), values[i].Distinct().Take(Samples).ToList())).ToList();
        return new CsvFile(path, fileName, columns, rows) { Preview = preview };
    }

    // A field's key from a column's header, such as days_a_week from "Days a week".
    public static string FieldKey(string header)
    {
        var plain = new string(header.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')
            .ToArray());
        var key = string.Join('_', plain.Split('_', StringSplitOptions.RemoveEmptyEntries));
        key = key.Length == 0 || !char.IsAsciiLetterLower(key[0]) ? $"f_{key}" : key;
        key = key == "name" ? "name_field" : key;
        return key[..Math.Min(key.Length, 63)].TrimEnd('_');
    }

    // The type a column holds the keys of, from its header: stop, stop_id, Stop ID, stops and from_stop_id all mean Stop.
    public static string? TypeForColumn(string header, IEnumerable<string> types)
    {
        var words = new string(header.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] is "from" or "to" or "source" or "target")
        {
            words.RemoveAt(0);
        }

        if (words.Count > 1 && words[^1] is "id" or "key" or "code" or "ref")
        {
            words.RemoveAt(words.Count - 1);
        }

        var stem = string.Concat(words);
        if (stem.Length == 0)
        {
            return null;
        }

        return types.FirstOrDefault(type =>
        {
            var name = type.ToLowerInvariant();
            return stem == name || stem == $"{name}s" || stem == $"{name}es" || (name.EndsWith('y') && stem == $"{name[..^1]}ies");
        });
    }

    // A file with columns naming two types is probably links between them. If the model only has the relationship the
    // other way round, the ends are swapped to match it.
    public static CsvLinkGuess? GuessLinks(CsvFile file, IReadOnlyList<string> types, IReadOnlyList<RelationshipDefinition> relationships)
    {
        var ends = file.Columns
            .Select(c => (c.Index, Type: TypeForColumn(c.Header, types)))
            .Where(e => e.Type is not null)
            .Take(2)
            .ToList();
        if (ends.Count < 2)
        {
            return null;
        }

        var (from, to) = (ends[0], ends[1]);
        if (RelationshipsBetween(relationships, from.Type!, to.Type!).Count == 0 && RelationshipsBetween(relationships, to.Type!, from.Type!).Count > 0)
        {
            (from, to) = (to, from);
        }

        var between = RelationshipsBetween(relationships, from.Type!, to.Type!);
        return new CsvLinkGuess(from.Type!, from.Index, to.Type!, to.Index, between.Count == 1 ? between[0] : null);
    }

    // A type's name as part of a relationship's, such as SUPPLIER_CONTACT for SupplierContact.
    public static string RelName(string type) =>
        string.Concat(type.Select((c, i) => i > 0 && char.IsUpper(c) ? $"_{c}" : c.ToString())).ToUpperInvariant();

    public static IReadOnlyList<string> RelationshipsBetween(IReadOnlyList<RelationshipDefinition> relationships, string fromType, string toType) =>
        relationships.Where(r => r.FromType == fromType && r.ToType == toType).Select(r => r.RelType).Distinct().ToList();

    // The column that identifies each row: one called id, key or code, or the type's id, such as supplier_id. The first
    // column if there's none.
    public static int GuessKeyColumn(CsvFile file, string? type)
    {
        var typeName = type?.ToLowerInvariant();
        string Plain(string header) => new(header.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return file.Columns.FirstOrDefault(c => Plain(c.Header) is "id" or "key" or "code")?.Index
            ?? file.Columns.FirstOrDefault(c => typeName is not null && Plain(c.Header) is var plain && (plain == $"{typeName}id" || plain == $"{typeName}key" || plain == $"{typeName}code"))?.Index
            ?? 0;
    }

    // Which existing type a file of records belongs to, from what's in it: columns named like the type's fields, keys that
    // are already records of it, and the file's name. Null when nothing points clearly at one type, so it becomes a new one.
    public async Task<CsvTypeGuess?> GuessTypeAsync(CsvFile file, int keyColumn, string? nameFromFile, IReadOnlyList<RecordType> types, CancellationToken cancellationToken = default)
    {
        if (types.Count == 0)
        {
            return null;
        }

        var headers = file.Columns.Where(c => c.Index != keyColumn).Select(c => FieldKey(c.Header)).ToHashSet();
        var keys = KeySample(file.Path, keyColumn);
        // One type at a time, so each lookup uses the (type, key) index rather than reading every record.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var known = new Dictionary<string, int>();
        foreach (var type in types)
        {
            known[type.Name] = await db.Nodes.AsNoTracking().CountAsync(n => n.Type == type.Name && keys.Contains(n.Key), cancellationToken);
        }

        // A key already there is the strongest sign, then the name, then each matching field.
        var scored = types.Select(type =>
        {
            var fields = type.Fields.Select(f => f.Key).Where(headers.Contains).ToList();
            var keysThere = known.GetValueOrDefault(type.Name);
            var named = type.Name == nameFromFile;
            return (type.Name, fields, keysThere, named, Score: (keysThere > 0 ? 3 : 0) + (named ? 3 : 0) + fields.Count);
        })
        .Where(s => s.Score > 0)
        .OrderByDescending(s => s.Score)
        .ToList();

        if (scored.Count == 0 || (scored.Count > 1 && scored[1].Score == scored[0].Score))
        {
            return null;
        }

        var best = scored[0];
        var reasons = new List<string>();
        if (best.named)
        {
            reasons.Add("the file is named after it");
        }

        if (best.fields.Count > 0)
        {
            reasons.Add($"it has {best.Name}'s {(best.fields.Count == 1 ? "field" : "fields")} {string.Join(", ", best.fields)}");
        }

        if (best.keysThere > 0)
        {
            reasons.Add(best.keysThere == 1 ? $"one of its keys is already a {best.Name}" : $"{best.keysThere} of its keys are already {best.Name} records");
        }

        return new CsvTypeGuess(best.Name, $"Looks like {best.Name}: {string.Join(", and ", reasons)}.");
    }

    private static List<string> KeySample(string path, int keyColumn, int count = 200)
    {
        using var reader = Open(path);
        reader.Read();
        reader.ReadHeader();
        var keys = new List<string>();
        while (keys.Count < count && reader.Read())
        {
            if (reader.GetField(keyColumn)?.Trim() is { Length: > 0 } key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    public async Task<CsvImportPlan> PlanAsync(CsvFile file, CsvRecordMapping mapping, CancellationToken cancellationToken = default)
    {
        var (rows, problems, notes, skipped, _) = await ReadRecordsAsync(file, mapping, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingKeysAsync(db, mapping.Type, rows.Select(r => r.Key).ToList(), cancellationToken);
        return new CsvImportPlan(file.Rows, rows.Count(r => !existing.Contains(r.Key)), rows.Count(r => existing.Contains(r.Key)), skipped, problems, notes);
    }

    public async Task<CsvImportPlan> PlanAsync(CsvFile file, CsvLinkMapping mapping, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var (pairs, problems, notes, skipped) = await ReadLinksAsync(db, file, mapping, cancellationToken);
        var existing = await ExistingLinksAsync(db, mapping.RelType, pairs, cancellationToken);
        return new CsvImportPlan(file.Rows, pairs.Count - existing, existing, skipped, problems, notes);
    }

    public async Task<CsvImportResult> ImportAsync(CsvFile file, CsvRecordMapping mapping, CancellationToken cancellationToken = default)
    {
        var (rows, problems, _, _, choices) = await ReadRecordsAsync(file, mapping, cancellationToken);
        Stop(problems);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The type and any new fields join the model, as if the model file had them.
        var newType = await ExecuteAsync(connection, transaction,
            "INSERT INTO node_type (name, sort_order) SELECT @type, coalesce(max(sort_order), 0) + 1 FROM node_type ON CONFLICT (name) DO NOTHING",
            ("type", mapping.Type, NpgsqlDbType.Text)) > 0;
        foreach (var field in mapping.Fields)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO field_definition (node_type_name, key, type, options) VALUES (@type, @key, @fieldType, @options) ON CONFLICT (node_type_name, key) DO NOTHING",
                ("type", mapping.Type, NpgsqlDbType.Text),
                ("key", field.Key, NpgsqlDbType.Text),
                ("fieldType", SnakeCaseEnumConverter<FieldType>.ToStore(field.Type), NpgsqlDbType.Text),
                ("options", choices.TryGetValue(field.Key, out var options) ? options.ToArray() : (object)DBNull.Value, NpgsqlDbType.Array | NpgsqlDbType.Text));
        }

        var batch = await NewBatchAsync(connection, transaction, file, $"{mapping.Type} records", newType ? new { type = mapping.Type } : null, cancellationToken);
        await ExecuteAsync(connection, transaction, "CREATE TEMP TABLE staging_node (id text, key text, name text, props jsonb) ON COMMIT DROP");
        await using (var import = await connection.BeginBinaryImportAsync("COPY staging_node (id, key, name, props) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var row in rows)
            {
                await import.StartRowAsync(cancellationToken);
                await import.WriteAsync($"{mapping.Type.ToLowerInvariant()}:{row.Key}", NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(row.Key, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(row.Name, NpgsqlDbType.Text, cancellationToken);
                await import.WriteAsync(row.Props, NpgsqlDbType.Jsonb, cancellationToken);
            }

            await import.CompleteAsync(cancellationToken);
        }

        var added = await ExecuteAsync(connection, transaction,
            """
            INSERT INTO node (id, user_id, type, key, name, props, import_batch_id)
            SELECT id, @user, @type, key, name, props, @batch FROM staging_node
            ON CONFLICT DO NOTHING
            """,
            ("user", AppUser.DefaultId, NpgsqlDbType.Bigint),
            ("type", mapping.Type, NpgsqlDbType.Text),
            ("batch", batch, NpgsqlDbType.Bigint));
        await FinishBatchAsync(connection, transaction, batch, added, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CsvImportResult(batch, added);
    }

    public async Task<CsvImportResult> ImportAsync(CsvFile file, CsvLinkMapping mapping, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var (pairs, problems, _, _) = await ReadLinksAsync(db, file, mapping, cancellationToken);
        Stop(problems);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var newRelationship = await ExecuteAsync(connection, transaction,
            "INSERT INTO link_definition (source_type, rel_type, target_type) VALUES (@from, @rel, @to) ON CONFLICT DO NOTHING",
            ("from", mapping.FromType, NpgsqlDbType.Text),
            ("rel", mapping.RelType, NpgsqlDbType.Text),
            ("to", mapping.ToType, NpgsqlDbType.Text)) > 0;

        var batch = await NewBatchAsync(connection, transaction, file, $"{mapping.FromType} {mapping.RelType} {mapping.ToType} links", newRelationship ? new { from = mapping.FromType, rel = mapping.RelType, to = mapping.ToType } : null, cancellationToken);
        await StageLinksAsync(connection, pairs, cancellationToken);
        var added = await ExecuteAsync(connection, transaction,
            """
            INSERT INTO node_link (source_id, rel_type, target_id, import_batch_id)
            SELECT s.source_id, @rel, s.target_id, @batch FROM staging_link s
            WHERE NOT EXISTS (SELECT 1 FROM node_link l WHERE l.source_id = s.source_id AND l.rel_type = @rel AND l.target_id = s.target_id)
            """,
            ("rel", mapping.RelType, NpgsqlDbType.Text),
            ("batch", batch, NpgsqlDbType.Bigint));
        await ExecuteAsync(connection, transaction,
            "UPDATE link_definition SET links = links + @added WHERE source_type = @from AND rel_type = @rel AND target_type = @to",
            ("added", (long)added, NpgsqlDbType.Bigint),
            ("from", mapping.FromType, NpgsqlDbType.Text),
            ("rel", mapping.RelType, NpgsqlDbType.Text),
            ("to", mapping.ToType, NpgsqlDbType.Text));
        await FinishBatchAsync(connection, transaction, batch, added, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CsvImportResult(batch, added);
    }

    // Removes everything the import added: its links, then its records, including changes made to them since. Types,
    // fields and relationships it added stay in the model.
    public async Task UndoAsync(long batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batch = await db.ImportBatches.SingleOrDefaultAsync(b => b.Id == batchId && b.Source == DataSource.Csv && b.UserId == AppUser.DefaultId, cancellationToken)
            ?? throw new LinkbaseValidationException("There's no such import.");
        if (batch.Status != ImportStatus.Committed)
        {
            throw new LinkbaseValidationException("This import has already been undone.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var links = await db.NodeLinks.Where(l => l.ImportBatchId == batchId)
            .GroupBy(l => new { SourceType = l.Source.Type, l.RelType, TargetType = l.Target.Type })
            .Select(g => new { g.Key.SourceType, g.Key.RelType, g.Key.TargetType, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // A record keeps at least one of each link its type requires, so undoing can't take away its last one.
        foreach (var group in links)
        {
            var required = await db.LinkDefinitions.AnyAsync(
                d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType && d.Required, cancellationToken);
            var stranded = !required ? 0 : await db.NodeLinks
                .Where(l => l.ImportBatchId == batchId && l.RelType == group.RelType && l.Source.Type == group.SourceType && l.Target.Type == group.TargetType)
                .Select(l => l.SourceId)
                .Distinct()
                .CountAsync(source => !db.NodeLinks.Any(o => o.SourceId == source && o.RelType == group.RelType && o.Target.Type == group.TargetType && o.ImportBatchId != batchId), cancellationToken);
            if (stranded > 0)
            {
                throw new LinkbaseValidationException(
                    $"Undoing would leave {stranded:N0} {group.SourceType} record(s) with no {group.SourceType} {group.RelType} {group.TargetType} link, which each needs. Link them another way first.");
            }
        }

        foreach (var group in links)
        {
            await db.LinkDefinitions
                .Where(d => d.SourceType == group.SourceType && d.RelType == group.RelType && d.TargetType == group.TargetType)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links - group.Count), cancellationToken);
        }

        await db.NodeLinks.Where(l => l.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);

        // The import's own records first, then their links one side at a time, so both lookups use an index. Joining every
        // link to its ends took minutes on the Swiss sample's 45M links.
        var added = db.Nodes.Where(n => n.ImportBatchId == batchId).Select(n => n.Id);
        var linked = await db.NodeLinks.CountAsync(l => added.Contains(l.SourceId), cancellationToken)
            + await db.NodeLinks.CountAsync(l => added.Contains(l.TargetId), cancellationToken);
        if (linked > 0)
        {
            throw new LinkbaseValidationException($"Records this import added have {linked:N0} link(s) added since. Remove those first.");
        }

        await db.Nodes.Where(n => n.ImportBatchId == batchId).ExecuteDeleteAsync(cancellationToken);
        await RemoveWhatItCreatedAsync(db, batch.RawPayload, cancellationToken);
        batch.Status = ImportStatus.Undone;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // A type the import added goes too, with its fields, once nothing is left of it. So does a relationship it added, once
    // it has no links. Fields it added to a type that was already there stay.
    private static async Task RemoveWhatItCreatedAsync(LinkbaseDbContext db, string? summary, CancellationToken cancellationToken)
    {
        var created = (JsonNode.Parse(summary ?? "{}") as JsonObject)?["created"] as JsonObject;
        if ((string?)created?["type"] is { } type
            && !await db.Nodes.AnyAsync(n => n.Type == type, cancellationToken)
            && !await db.LinkDefinitions.AnyAsync(d => (d.SourceType == type || d.TargetType == type) && d.Links > 0, cancellationToken))
        {
            await db.LinkDefinitions.Where(d => d.SourceType == type || d.TargetType == type).ExecuteDeleteAsync(cancellationToken);
            await db.FieldDefinitions.Where(f => f.NodeTypeName == type).ExecuteDeleteAsync(cancellationToken);
            await db.NodeTypes.Where(t => t.Name == type).ExecuteDeleteAsync(cancellationToken);
        }

        if ((string?)created?["rel"] is { } rel && (string?)created["from"] is { } from && (string?)created["to"] is { } to)
        {
            await db.LinkDefinitions.Where(d => d.SourceType == from && d.RelType == rel && d.TargetType == to && d.Links == 0).ExecuteDeleteAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<CsvBatch>> RecentAsync(int count, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var batches = await db.ImportBatches.AsNoTracking()
            .Where(b => b.Source == DataSource.Csv && b.UserId == AppUser.DefaultId)
            .OrderByDescending(b => b.Id)
            .Take(count)
            .Select(b => new { b.Id, b.RawPayload, b.Status, b.CreatedAt })
            .ToListAsync(cancellationToken);
        return batches.Select(b =>
        {
            var summary = JsonNode.Parse(b.RawPayload ?? "{}") as JsonObject ?? [];
            return new CsvBatch(b.Id, (string?)summary["file"] ?? "", (string?)summary["what"] ?? "", (int?)summary["added"] ?? 0, b.Status, b.CreatedAt);
        }).ToList();
    }

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
            var defined = await db.FieldDefinitions.AsNoTracking().Where(f => f.NodeTypeName == mapping.Type)
                .Select(f => new { f.Key, f.Type, f.Options, f.Required }).ToListAsync(cancellationToken);
            foreach (var field in defined.Where(d => d.Required))
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
                if (existing is not null && existing.Type != field.Type)
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

    private static async Task<(List<(string Source, string Target)> Pairs, List<string> Problems, List<string> Notes, int Skipped)> ReadLinksAsync(
        LinkbaseDbContext db, CsvFile file, CsvLinkMapping mapping, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (!ModelFile.RelType().IsMatch(mapping.RelType))
        {
            problems.Add("A relationship's name is capitals, digits and underscores, such as SUPPLIES.");
        }

        var types = await db.NodeTypes.AsNoTracking().Select(t => t.Name).ToListAsync(cancellationToken);
        foreach (var type in new[] { mapping.FromType, mapping.ToType }.Where(t => !types.Contains(t)))
        {
            problems.Add($"There's no type called {type}. Import its records first.");
        }

        var pairs = new List<(string, string)>();
        var notes = new List<string>();
        if (problems.Count > 0)
        {
            return (pairs, problems, notes, 0);
        }

        var rows = new List<(int Line, string From, string To)>();
        using (var reader = Open(file.Path))
        {
            reader.Read();
            reader.ReadHeader();
            var line = 1;
            while (reader.Read())
            {
                line++;
                rows.Add((line, reader.GetField(mapping.FromColumn)?.Trim() ?? "", reader.GetField(mapping.ToColumn)?.Trim() ?? ""));
            }
        }

        var fromKeys = await ExistingKeysAsync(db, mapping.FromType, rows.Select(r => r.From).Where(k => k.Length > 0).Distinct().ToList(), cancellationToken);
        var toKeys = await ExistingKeysAsync(db, mapping.ToType, rows.Select(r => r.To).Where(k => k.Length > 0).Distinct().ToList(), cancellationToken);
        var seen = new HashSet<(string, string)>();
        var (empty, missing, repeats) = (0, 0, 0);
        var missingShown = new List<string>();
        foreach (var (line, from, to) in rows)
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
            else if (!seen.Add((from, to)))
            {
                repeats++;
            }
            else
            {
                pairs.Add(($"{mapping.FromType.ToLowerInvariant()}:{from}", $"{mapping.ToType.ToLowerInvariant()}:{to}"));
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
            notes.Add($"{repeats:N0} row(s) repeat a link already in the file.");
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

    private static async Task<int> ExistingLinksAsync(LinkbaseDbContext db, string relType, List<(string Source, string Target)> pairs, CancellationToken cancellationToken)
    {
        // In a transaction only so the staging table lasts until the count, and goes when it's rolled back.
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await StageLinksAsync(connection, pairs, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM staging_link s WHERE EXISTS (SELECT 1 FROM node_link l WHERE l.source_id = s.source_id AND l.rel_type = @rel AND l.target_id = s.target_id)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = relType });
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await transaction.RollbackAsync(cancellationToken);
        return count;
    }

    private static async Task StageLinksAsync(NpgsqlConnection connection, List<(string Source, string Target)> pairs, CancellationToken cancellationToken)
    {
        await using (var create = new NpgsqlCommand("CREATE TEMP TABLE staging_link (source_id text, target_id text) ON COMMIT DROP", connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var import = await connection.BeginBinaryImportAsync("COPY staging_link (source_id, target_id) FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (var (source, target) in pairs)
        {
            await import.StartRowAsync(cancellationToken);
            await import.WriteAsync(source, NpgsqlDbType.Text, cancellationToken);
            await import.WriteAsync(target, NpgsqlDbType.Text, cancellationToken);
        }

        await import.CompleteAsync(cancellationToken);
    }

    // Created says whether the import added a type or a relationship to the model, which undoing it takes away again.
    private static async Task<long> NewBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CsvFile file, string what, object? created, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO import_batch (user_id, source, status, raw_payload, created_at, updated_at) VALUES (@user, 'csv', 'pending', @summary, now(), now()) RETURNING id",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Bigint) { Value = AppUser.DefaultId });
        command.Parameters.Add(new NpgsqlParameter("summary", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { file = file.FileName, what, created }) });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // What the import added, by file name and kind, never the file's contents.
    private static async Task FinishBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long batch, int added, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE import_batch SET status = 'committed', raw_payload = raw_payload || jsonb_build_object('added', @added), updated_at = now() WHERE id = @batch",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("added", NpgsqlDbType.Integer) { Value = added });
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Bigint) { Value = batch });
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static CsvReader Open(string path) =>
        new(new StreamReader(path, detectEncodingFromByteOrderMarks: true), new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            BadDataFound = null,
            MissingFieldFound = null,
        });

    // A column's different values, ignoring case, in the spelling first seen, sorted. Stops counting just past the limit.
    private static List<string> ChoicesIn(string path, int column)
    {
        using var reader = Open(path);
        reader.Read();
        reader.ReadHeader();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (found.Count <= FieldValues.MaxOptions && reader.Read())
        {
            if (reader.GetField(column)?.Trim() is { Length: > 0 } value)
            {
                found.TryAdd(value, value);
            }
        }

        return found.Values.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static FieldType Guess(List<string> values) =>
        values.Count == 0 ? FieldType.Text
        : values.All(v => Typed(v, FieldType.Number) is not null) ? FieldType.Number
        : values.All(v => Typed(v, FieldType.Date) is not null) ? FieldType.Date
        : values.All(v => Typed(v, FieldType.Boolean) is not null) ? FieldType.Boolean
        : FieldType.Text;

    // The value as its field's type, as the loader stores it, or null if it isn't one.
    private static JsonNode? Typed(string text, FieldType type, IReadOnlyList<string>? options = null) => type switch
    {
        FieldType.Choice => FieldValues.MatchChoice(text, options ?? []) is { } option ? JsonValue.Create(option) : null,
        FieldType.List => FieldValues.SplitList(text, FieldValues.ListSeparator) is { Count: > 0 } values
            ? new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
            : null,
        FieldType.Number => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : null,
        FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : null,
        FieldType.Boolean => bool.TryParse(text, out var flag) ? JsonValue.Create(flag) : null,
        _ => JsonValue.Create(text),
    };

    public static string Labelled(FieldType type) => type switch
    {
        FieldType.Number => "a number",
        FieldType.Date => "a date (yyyy-mm-dd)",
        FieldType.Boolean => "true or false",
        FieldType.Choice => "a choice",
        FieldType.List => "a list, split by |",
        _ => "text",
    };
}
