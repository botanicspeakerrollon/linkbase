using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Data;
public enum StagedMatch { New, Existing, Ambiguous }

// Ids are node ids, such as crag:12.
public sealed record MatchCandidate(string Id, string Name, string? Detail, double Similarity);

// An area to put a new crag in, with the areas above it.
public sealed record AreaChoice(string Id, string Name, string Path);

// Editable rows on the staging screen. Choice says what committing does with a row.
public abstract class StagedRow
{
    public const string Undecided = "?";
    public const string CreateNew = "";

    public bool Include { get; set; } = true;
    public StagedMatch Match { get; set; }
    public List<MatchCandidate> Candidates { get; set; } = [];

    // Undecided, CreateNew, or the node id of the existing row to use.
    public string Choice { get; set; } = CreateNew;

    public bool UsesExisting => Choice is { Length: > 0 } && Choice != Undecided;
}

public sealed class StagedCrag : StagedRow
{
    public required string Name { get; set; }
    public string? RockType { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string? ExtractedArea { get; set; }

    // Where a new crag goes: an existing area, or a new one with this name under NewAreaParentId.
    public string? AreaId { get; set; }
    public string? AreaLabel { get; set; }
    public string? NewAreaName { get; set; }
    public string? NewAreaParentId { get; set; }
    public AreaKind? NewAreaKind { get; set; }

    public List<StagedRoute> Routes { get; set; } = [];
}

// A route's summary is shown in the extraction but not kept: the model has no description field, because no route had one.
public sealed class StagedRoute : StagedRow
{
    public required string Name { get; set; }
    public string? Grade { get; set; }
    public GradeSystem? GradeSystem { get; set; }
    public Discipline? Discipline { get; set; }
    public int? LengthM { get; set; }
    public int? Pitches { get; set; }
}

public sealed record StagedImport(
    long BatchId,
    ImportStatus Status,
    string? SourceUrl,
    string? Title,
    IReadOnlyList<string> Warnings,
    string? Error,
    List<StagedCrag> Crags);

public sealed record ImportBatchSummary(
    long Id,
    string? SourceUrl,
    string? Title,
    ImportStatus Status,
    DateTimeOffset CreatedAt,
    int Crags,
    int Routes,
    string? Error);

public sealed record CommitSummary(int AreasCreated, int CragsCreated, int CragsMatched, int RoutesCreated, int RoutesMatched);

public sealed record UndoSummary(int RoutesDeleted, int CragsDeleted, int AreasDeleted, IReadOnlyList<string> Skipped);
