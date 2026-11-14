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

// A CSV file's columns, each with a few values and the type they look like.
public sealed record CsvColumn(int Index, string Header, FieldType Guess, IReadOnlyList<string> Samples);

// Preview is the first few rows as they are in the file, to show what was uploaded.
public sealed record CsvFile(string Path, string FileName, IReadOnlyList<CsvColumn> Columns, int Rows)
{
    public IReadOnlyList<IReadOnlyList<string>> Preview { get; init; } = [];
}

public sealed record CsvFieldMapping(int Column, string Key, FieldType Type);

// Each row is a record of the type, identified by the key column. KeyColumn is -1 when the file has no column to use as
// a key: then NameColumn must be set, and each row is identified by its name instead. Its key is the name's slug
// ("zoe-adams" for "Zoë Adams"), reused from a record already here with the same name (ignoring case), or made up and
// disambiguated when it's new. Two rows with the same name (ignoring case) are the same record; a name that matches more
// than one record already here is added as new, with a note, rather than guessed. A new type and new fields are added to
// the model. UpdateExisting changes records whose key is already there to the file's values, where they differ, instead
// of leaving them. Only the columns the file has a value for change, and the changes are logged so they can be put back.
public sealed record CsvRecordMapping(string Type, int KeyColumn, int? NameColumn, IReadOnlyList<CsvFieldMapping> Fields, bool UpdateExisting = false);

// Each row links the record of one type whose key is in one column to the record of another whose key is in another.
// Attributes are columns holding the link's own values, such as a role: each Key is one of the relationship's attributes,
// which the model already has. The same two records can be linked again in the file when the values differ, unless
// UpdateExisting is set: then, when the two are already linked that way exactly once, its values change to the file's
// instead of a second link being added. Two or more links already between them is left alone either way, since which one
// the file means isn't clear.
public sealed record CsvLinkMapping(string FromType, int FromColumn, string RelType, string ToType, int ToColumn, IReadOnlyList<CsvFieldMapping>? Attributes = null)
{
    public bool UpdateExisting { get; init; }
}

// What an import would do. Problems stop it; rows that are skipped don't.
public sealed record CsvImportPlan(int Rows, int ToAdd, int AlreadyThere, int Skipped, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool CanImport => Problems.Count == 0 && (ToAdd > 0 || ToUpdate > 0);

    // Records already there that the file would change, when it was asked to update them. AlreadyThere then counts only the
    // ones it wouldn't change.
    public int ToUpdate { get; init; }
}

public sealed record CsvImportResult(long BatchId, int Added)
{
    public int Updated { get; init; }
}

// What a file of links probably is, from its headers and the model. RelType is filled in when the model has exactly one
// relationship between the two types.
public sealed record CsvLinkGuess(string FromType, int FromColumn, string ToType, int ToColumn, string? RelType);

// The existing type a file of records most looks like, and why, such as "it has Person's fields title and joined".
public sealed record CsvTypeGuess(string Type, string Reason);

public sealed record CsvBatch(long Id, string File, string What, int Added, ImportStatus Status, DateTimeOffset At);
