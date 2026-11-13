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

    // suppliers.csv suggests Supplier.
    public static string TypeFromFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var words = stem.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsAsciiLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        var name = string.Concat(words);
        name = name.EndsWith("People", StringComparison.Ordinal) ? name[..^6] + "Person"
            : name.EndsWith("ies", StringComparison.Ordinal) ? name[..^3] + "y"
            : name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) ? name[..^1]
            : name;
        return name.Length > 0 && char.IsAsciiLetterUpper(name[0]) ? name : "";
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
}
