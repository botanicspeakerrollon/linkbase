namespace Linkbase.Data;

public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

public abstract class ImportedEntity : ITimestamped
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long? ImportBatchId { get; set; }
    public ImportBatch? ImportBatch { get; set; }
}

public abstract class OwnedEntity : ImportedEntity
{
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
}

public class AppUser
{
    // The single user seeded by the first migration.
    public const long DefaultId = 1;

    public long Id { get; set; }
    public required string DisplayName { get; set; }
}

public class Area : OwnedEntity
{
    public long? ParentId { get; set; }
    public Area? Parent { get; set; }
    public required string Name { get; set; }
    public AreaKind Kind { get; set; }
    public List<Area> Children { get; set; } = [];
    public List<Crag> Crags { get; set; } = [];
}

public class Crag : OwnedEntity
{
    public long AreaId { get; set; }
    public Area Area { get; set; } = null!;
    public required string Name { get; set; }
    public string? RockType { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public List<string> Aliases { get; set; } = [];
    public string? Notes { get; set; }
    public List<Route> Routes { get; set; } = [];
}

public class Route : OwnedEntity
{
    public long CragId { get; set; }
    public Crag Crag { get; set; } = null!;
    public required string Name { get; set; }
    public string? Grade { get; set; }
    public GradeSystem? GradeSystem { get; set; }
    public Discipline? Discipline { get; set; }
    public int? LengthM { get; set; }
    public int? Pitches { get; set; }
    public string? Description { get; set; }
    public List<Ascent> Ascents { get; set; } = [];
}

public class Ascent : OwnedEntity
{
    public long RouteId { get; set; }
    public Route Route { get; set; } = null!;
    // A partial date is stored as the first of its month or year.
    public DateOnly Date { get; set; }
    public DatePrecision DatePrecision { get; set; }
    public AscentStyle Style { get; set; }
    public string? StyleRaw { get; set; }
    public string? Notes { get; set; }
    public short? MyRating { get; set; }
    public DataSource Source { get; set; }
    public long? TripId { get; set; }
    public Trip? Trip { get; set; }
    public bool TripInferred { get; set; }
    public List<AscentPartner> Partners { get; set; } = [];
}

public class Partner : OwnedEntity
{
    public required string Name { get; set; }
    public List<AscentPartner> Ascents { get; set; } = [];
}

public class AscentPartner
{
    public long AscentId { get; set; }
    public Ascent Ascent { get; set; } = null!;
    public long PartnerId { get; set; }
    public Partner Partner { get; set; } = null!;
}

public class Trip : OwnedEntity
{
    // Identifies a trip across rebuilds, such as "England 2012-07-01".
    public required string Key { get; set; }
    public required string Name { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    // Trips with unknown dates have no start date but still need an order.
    public DateOnly SortDate { get; set; }
    public string? Notes { get; set; }
    public bool Abroad { get; set; }
    public bool Home { get; set; }
    public bool Approximate { get; set; }
    public bool DatesPartlyUnknown { get; set; }
    public bool Manual { get; set; }
    public List<Ascent> Ascents { get; set; } = [];
}

// Rows of the graph_nodes and graph_edges views. Props is built from named columns in the view.
public class GraphNodeRow
{
    public required string NodeId { get; set; }
    public long UserId { get; set; }
    public required string Label { get; set; }
    public required string Name { get; set; }
    public required string Props { get; set; }
}

public class GraphEdgeRow
{
    public required string SourceId { get; set; }
    public required string TargetId { get; set; }
    public required string RelType { get; set; }
    public required string Props { get; set; }
}

// A saved view of the graph explorer: which nodes were visible and where.
public class GraphScene : ITimestamped
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public required string Name { get; set; }
    public required string Nodes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// A possible duplicate the importer wouldn't guess at. The new row exists and isn't merged.
public class ReviewItem : ImportedEntity
{
    public ExternalEntityType EntityType { get; set; }
    public long EntityId { get; set; }
    public long CandidateId { get; set; }
    public double Similarity { get; set; }
    public required string Reason { get; set; }
    public ReviewStatus Status { get; set; }
}

public class ExternalRef : ImportedEntity
{
    public ExternalEntityType EntityType { get; set; }
    public long EntityId { get; set; }
    public DataSource Source { get; set; }
    public required string ExternalId { get; set; }
    public string? Url { get; set; }
}

public class ImportBatch : ITimestamped
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public DataSource Source { get; set; }
    public string? SourceUrl { get; set; }
    public ImportStatus Status { get; set; }
    public string? RawPayload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
