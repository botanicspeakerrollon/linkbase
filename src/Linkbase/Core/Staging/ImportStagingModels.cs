using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Staging;

// A field an import adds to a type. Options are a choice field's values.
public sealed record StagedField(string Type, string Key, FieldType FieldType, IReadOnlyList<string> Options);

public sealed record StagedRelationship(string FromType, string RelType, string ToType);

// What an import adds to the model itself, as if the model file had it: types, fields and relationships. Committing adds
// what isn't there yet, and undoing takes away what committing added, once nothing is left of it.
public sealed record ModelChanges(IReadOnlyList<string> Types, IReadOnlyList<StagedField> Fields, IReadOnlyList<StagedRelationship> Relationships)
{
    public static readonly ModelChanges None = new([], [], []);
}

// A record an import would add. Id is the node's id, such as supplier:s1.
public sealed record StagedRecord(string Type, string Key, string Name, string Props)
{
    public string Id => $"{Type.ToLowerInvariant()}:{Key}";
}

// A link an import would add, between two nodes by id.
public sealed record StagedLink(string SourceType, string RelType, string SourceId, string TargetType, string TargetId);

// Who is importing and what, never the file's contents. AlreadyThere and Skipped only count what was left out, so a review
// can say so.
public sealed record StagingRequest(DataSource Source, string File, string What, ModelChanges Model, int AlreadyThere, int Skipped, IReadOnlyList<string> Notes)
{
    public StagingRequest(DataSource source, string file, string what, ModelChanges model)
        : this(source, file, what, model, 0, 0, [])
    {
    }
}

public sealed record StagedBatch(
    long Id, DataSource Source, string File, string What, ImportStatus Status, int Records, int Links, int Included, int AlreadyThere, int Skipped,
    IReadOnlyList<string> Notes, int Added, DateTimeOffset At)
{
    public int Items => Records + Links;
}

public sealed record StagedRow(long Id, StagedKind Kind, string Type, string Title, string Detail, bool Included);

public sealed record StagedPage(int Total, IReadOnlyList<StagedRow> Rows);

public sealed record StagedCommit(long BatchId, int Records, int Links)
{
    public int Added => Records + Links;
}
