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

public class AppUser
{
    // The single user seeded by the first migration.
    public const long DefaultId = 1;

    public long Id { get; set; }
    public required string DisplayName { get; set; }
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

// A named, reopenable state of a page: an Explore search or canvas, or a record list's search and filters. State is the
// same text the page's address carries in its v parameter, so a view and a permalink are one thing. Pinned views are
// shown on Home. See Core/Views.
public class SavedView : ITimestamped
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public required string Name { get; set; }
    public SavedViewKind Kind { get; set; }

    // The type a list view is of. Null for Explore.
    public string? Scope { get; set; }

    public required string State { get; set; }
    public bool Pinned { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// An id in another system, such as a UKC crag id, for a node in the generic store.
public class ExternalRef : ImportedEntity
{
    public required string NodeId { get; set; }

    // The node's type is kept with its id because a source's ids are only unique per kind of thing.
    public required string NodeType { get; set; }

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
