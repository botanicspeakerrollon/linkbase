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

// Each row is a record of the type, identified by the key column. A new type and new fields are added to the model.
public sealed record CsvRecordMapping(string Type, int KeyColumn, int? NameColumn, IReadOnlyList<CsvFieldMapping> Fields);

// Each row links the record of one type whose key is in one column to the record of another whose key is in another.
// Attributes are columns holding the link's own values, such as a role: each Key is one of the relationship's attributes,
// which the model already has. The same two records can be linked again in the file when the values differ.
public sealed record CsvLinkMapping(string FromType, int FromColumn, string RelType, string ToType, int ToColumn, IReadOnlyList<CsvFieldMapping>? Attributes = null);

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
