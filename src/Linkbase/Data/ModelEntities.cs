namespace Linkbase.Data;

// The generic store: any model's types, fields, relationships and data, as loaded from a model file. Climbing keeps its
// own typed tables. See docs/models.md.

// Choice is one of a fixed list of values, kept in FieldDefinition.Options and stored as text. List is several text
// values, stored as a JSON array.
public enum FieldType { Text, Number, Date, Boolean, Choice, List }

// A type of node, such as Stop or Line.
public class NodeType
{
    public required string Name { get; set; }
    public string? Color { get; set; }

    // A Material icon's name, such as Train.
    public string? Icon { get; set; }

    public int SortOrder { get; set; }
    public List<FieldDefinition> Fields { get; set; } = [];
}

public class FieldDefinition
{
    public long Id { get; set; }
    public required string NodeTypeName { get; set; }
    public NodeType NodeType { get; set; } = null!;
    public required string Key { get; set; }
    public FieldType Type { get; set; }

    // A choice field's values, in the order to offer them. Null for every other type.
    public List<string>? Options { get; set; }

    // A record of the type can't be saved without a value for it.
    public bool Required { get; set; }
}

// A relationship that may exist between two types, such as Service CALLS_AT Stop.
public class LinkDefinition
{
    public long Id { get; set; }
    public required string SourceType { get; set; }
    public NodeType Source { get; set; } = null!;
    public required string RelType { get; set; }
    public required string TargetType { get; set; }
    public NodeType Target { get; set; } = null!;

    // Every record of the source type needs at least one link of this kind, such as an ascent's route.
    public bool Required { get; set; }

    // How many links of this kind there are, kept by the loader and RecordRepository, because counting tens of millions
    // of links each time the home page opens takes too long.
    public long Links { get; set; }
}

public class Node : ITimestamped
{
    // The node's id across the whole graph: its type in lower case, a colon, and its key from the source data, such as
    // stop:8503000. It stays the same when a model is reloaded, so saved scenes still line up.
    public required string Id { get; set; }

    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public required string Type { get; set; }
    public NodeType NodeType { get; set; } = null!;
    public required string Key { get; set; }
    public required string Name { get; set; }

    // Values for the type's fields, as JSON.
    public required string Props { get; set; }

    // The name and every value, for free text. Postgres fills it in.
    public string? SearchText { get; set; }

    public long? ImportBatchId { get; set; }
    public ImportBatch? ImportBatch { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class NodeLink
{
    public long Id { get; set; }
    public required string SourceId { get; set; }
    public Node Source { get; set; } = null!;
    public required string RelType { get; set; }
    public required string TargetId { get; set; }
    public Node Target { get; set; } = null!;
    public string Props { get; set; } = "{}";

    // The CSV import that added it, so that import can be undone.
    public long? ImportBatchId { get; set; }
    public ImportBatch? ImportBatch { get; set; }
}
