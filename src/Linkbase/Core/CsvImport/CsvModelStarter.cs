using Linkbase.Core.Models;
using Linkbase.Data;

namespace Linkbase.Core.CsvImport;

// A column of a records file that holds the keys of another type's records, so each row links to one of them.
public sealed class ProposedLink
{
    public required int Column { get; init; }
    public required string ToType { get; set; }
    public required string RelType { get; set; }
    public bool Include { get; set; } = true;
}

public sealed class ProposedField
{
    public required int Column { get; init; }
    public required string Key { get; set; }
    public required FieldType Type { get; set; }
    public bool Include { get; set; } = true;
}

// What one file would become. A records file is a type, with its fields and the links its key-holding columns make. A
// links file (a join table such as assignments, with a column for each of two types and no key of its own) is one
// relationship between them.
public sealed class ProposedFile
{
    public required CsvFile File { get; init; }
    public bool IsLinks { get; set; }

    // Records.
    public string Type { get; set; } = "";
    public int KeyColumn { get; set; }
    public int NameColumn { get; set; } = -1;
    public List<ProposedField> Fields { get; init; } = [];
    public List<ProposedLink> Links { get; init; } = [];

    // Links file: one end in each column.
    public string FromType { get; set; } = "";
    public int FromColumn { get; set; }
    public string ToType { get; set; } = "";
    public int ToColumn { get; set; }
    public string RelType { get; set; } = "";
}

public sealed record StarterOutcome(bool Applied, IReadOnlyList<string> Done, IReadOnlyList<string> Problems);

// Starts a model from several CSV files at once: guesses the types, their fields and the relationships between them from
// the files' names and headers, and, once the guess has been corrected, imports it through CsvImportService, so
// everything is an ordinary import that Recent imports can undo. See docs/models.md.
public sealed class CsvModelStarter(CsvImportService importer)
{
    // A guess at what the files are. Nothing is read from or written to the model.
    public static IReadOnlyList<ProposedFile> Propose(IReadOnlyList<CsvFile> files)
    {
        var names = files.Select(f => CsvImportService.TypeFromFile(f.FileName)).ToList();

        // A file with columns for two types and nothing identifying its rows is links between them.
        var links = files.Select((f, i) => IsJoinTable(f, names[i], names.Where(n => n.Length > 0).ToList())).ToList();
        var types = names.Where((n, i) => n.Length > 0 && !links[i]).Distinct().ToList();

        var proposed = new List<ProposedFile>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (links[i])
            {
                var ends = file.Columns.Select(c => (c.Index, Type: CsvImportService.TypeForColumn(c.Header, types))).Where(e => e.Type is not null).Take(2).ToList();
                proposed.Add(new ProposedFile
                {
                    File = file,
                    IsLinks = true,
                    FromType = ends[0].Type!,
                    FromColumn = ends[0].Index,
                    ToType = ends[1].Type!,
                    ToColumn = ends[1].Index,
                    RelType = $"HAS_{CsvImportService.RelName(ends[1].Type!)}",
                });
                continue;
            }

            var type = names[i];
            var key = CsvImportService.GuessKeyColumn(file, type);
            var name = file.Columns.FirstOrDefault(c => c.Index != key && c.Header.Contains("name", StringComparison.OrdinalIgnoreCase))?.Index ?? -1;
            var result = new ProposedFile { File = file, Type = type, KeyColumn = key, NameColumn = name };
            var used = new HashSet<(string, string)>();
            foreach (var column in file.Columns.Where(c => c.Index != key && c.Index != name))
            {
                var target = CsvImportService.TypeForColumn(column.Header, types);
                if (target is not null && target != type)
                {
                    // A second column to the same type is named after its header, so the two relationships differ.
                    var rel = used.Add((type, target)) ? $"HAS_{CsvImportService.RelName(target)}" : CsvImportService.FieldKey(column.Header).ToUpperInvariant();
                    result.Links.Add(new ProposedLink { Column = column.Index, ToType = target, RelType = rel });
                }
                else
                {
                    result.Fields.Add(new ProposedField { Column = column.Index, Key = CsvImportService.FieldKey(column.Header), Type = column.Guess });
                }
            }

            proposed.Add(result);
        }

        return proposed;
    }

    private static bool IsJoinTable(CsvFile file, string type, IReadOnlyList<string> types)
    {
        var ends = file.Columns.Count(c => CsvImportService.TypeForColumn(c.Header, types) is { } t && t != type);
        if (ends < 2)
        {
            return false;
        }

        string Plain(string header) => new(header.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var typeName = type.ToLowerInvariant();
        return !file.Columns.Any(c => Plain(c.Header) is "id" or "key" or "code" || Plain(c.Header) == $"{typeName}id");
    }

    // Everything wrong with the files as they stand, without changing anything.
    public async Task<IReadOnlyList<string>> CheckAsync(IReadOnlyList<ProposedFile> files, CancellationToken cancellationToken = default)
    {
        var problems = Validate(files);
        if (problems.Count > 0)
        {
            return problems;
        }

        foreach (var file in files.Where(f => !f.IsLinks))
        {
            var plan = await importer.PlanAsync(file.File, RecordMapping(file), cancellationToken);
            problems.AddRange(plan.Problems.Select(p => $"{file.File.FileName}: {p}"));
        }

        return problems;
    }

    // Imports every file's records, then every link, each as an import of its own. It checks the records first, so a
    // mistake in a file changes nothing, but a problem found only in the links is reported once the records are in.
    public async Task<StarterOutcome> ApplyAsync(IReadOnlyList<ProposedFile> files, CancellationToken cancellationToken = default)
    {
        var problems = (await CheckAsync(files, cancellationToken)).ToList();
        if (problems.Count > 0)
        {
            return new StarterOutcome(false, [], problems);
        }

        var done = new List<string>();
        foreach (var file in files.Where(f => !f.IsLinks))
        {
            var result = await importer.ImportAsync(file.File, RecordMapping(file), cancellationToken);
            done.Add($"{result.Added:N0} {file.Type} record(s).");
        }

        var linkMappings = files.Where(f => f.IsLinks)
            .Select(f => (File: f.File, Mapping: new CsvLinkMapping(f.FromType, f.FromColumn, f.RelType, f.ToType, f.ToColumn)))
            .Concat(files.Where(f => !f.IsLinks).SelectMany(f => f.Links.Where(l => l.Include)
                .Select(l => (File: f.File, Mapping: new CsvLinkMapping(f.Type, f.KeyColumn, l.RelType, l.ToType, l.Column)))));
        foreach (var (file, mapping) in linkMappings)
        {
            var label = $"{mapping.FromType} {mapping.RelType} {mapping.ToType}";
            var plan = await importer.PlanAsync(file, mapping, cancellationToken);
            if (plan.Problems.Count > 0)
            {
                problems.AddRange(plan.Problems.Select(p => $"{label} links from {file.FileName}: {p}"));
            }
            else if (plan.ToAdd > 0)
            {
                var result = await importer.ImportAsync(file, mapping, cancellationToken);
                done.Add($"{result.Added:N0} {label} link(s).");
            }
            else
            {
                done.Add($"No {label} links to add from {file.FileName}.");
            }
        }

        return new StarterOutcome(true, done, problems);
    }

    private static CsvRecordMapping RecordMapping(ProposedFile file) => new(
        file.Type.Trim(),
        file.KeyColumn,
        file.NameColumn < 0 ? null : file.NameColumn,
        file.Fields.Where(f => f.Include).Select(f => new CsvFieldMapping(f.Column, f.Key.Trim(), f.Type)).ToList());

    private static List<string> Validate(IReadOnlyList<ProposedFile> files)
    {
        var problems = new List<string>();
        if (files.Count == 0)
        {
            problems.Add("Choose at least one CSV file.");
        }

        var records = files.Where(f => !f.IsLinks).ToList();
        foreach (var repeated in records.GroupBy(f => f.Type.Trim()).Where(g => g.Count() > 1))
        {
            problems.Add($"{string.Join(" and ", repeated.Select(f => f.File.FileName))} are both {repeated.Key}. Give each file its own type.");
        }

        var types = records.Select(f => f.Type.Trim()).ToHashSet();
        foreach (var file in records)
        {
            foreach (var link in file.Links.Where(l => l.Include && !types.Contains(l.ToType)))
            {
                problems.Add($"{file.File.FileName}: {link.ToType} isn't one of the types being made.");
            }

            if (file.KeyColumn == file.NameColumn)
            {
                problems.Add($"{file.File.FileName}: the key and the name are the same column. Leave the name as the key instead.");
            }
        }

        foreach (var file in files.Where(f => f.IsLinks && (!types.Contains(f.FromType) || !types.Contains(f.ToType))))
        {
            problems.Add($"{file.File.FileName}: links between {file.FromType} and {file.ToType}, but one of them isn't being made.");
        }

        return problems;
    }
}
