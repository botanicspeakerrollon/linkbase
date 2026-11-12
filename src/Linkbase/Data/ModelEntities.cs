namespace Linkbase.Data;

// The generic store: any model's types, fields, relationships and data, as loaded from a model file. Climbing's records are
// here too. See docs/models.md.

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

    // The domain module that writes this type's records, such as Climbing for trips it derives from ascents. People can't
    // add, edit or delete them, or link them by hand. Null for a type people keep. See docs/models.md.
    public string? Owner { get; set; }

    // Set when the type's records form a hierarchy: the choice field saying which level a record is on, and the relationship
    // to its parent. The levels are in HierarchyLevels. See docs/models.md.
    public string? HierarchyField { get; set; }
    public string? HierarchyRel { get; set; }

    public List<FieldDefinition> Fields { get; set; } = [];
    public List<HierarchyLevel> HierarchyLevels { get; set; } = [];
}

// One level of a type's hierarchy, such as region: whether it's at the top, and the levels it may sit under.
public class HierarchyLevel
{
    public long Id { get; set; }
    public required string NodeTypeName { get; set; }
    public NodeType NodeType { get; set; } = null!;

    // One of the options of the type's HierarchyField.
    public required string Kind { get; set; }

    // A record at this level has no parent.
    public bool IsRoot { get; set; }

    // The kinds a record at this level may be in. Empty for a root.
    public List<string> Under { get; set; } = [];
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

    // The domain module that keeps this field's value, such as Climbing for a route's grade rank. People can't edit it.
    // Null for a field people fill in.
    public string? Owner { get; set; }
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

    // The values a link of this kind can carry, such as a person's role on a project. See docs/history/relationship-attributes.md.
    public List<LinkAttribute> Attributes { get; set; } = [];
}

// A value that belongs to a link rather than to either record. Same shape as a FieldDefinition; the values are kept in
// node_link.props.
public class LinkAttribute
{
    public long Id { get; set; }
    public long LinkDefinitionId { get; set; }
    public LinkDefinition LinkDefinition { get; set; } = null!;
    public required string Key { get; set; }
    public FieldType Type { get; set; }
    public List<string>? Options { get; set; }
    public bool Required { get; set; }
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

// Whether a staged item is a record to add or a link between two records.
public enum StagedKind { Record, Link }

// One thing an import has worked out it would add, kept until the import is committed or discarded so it can be reviewed
// first. A record has a type, key, name and props; a link has a relationship and the ids of its two ends. Excluded items
// are left out when the import is committed. See docs/models.md.
public class StagedItem
{
    public long Id { get; set; }
    public long ImportBatchId { get; set; }
    public ImportBatch ImportBatch { get; set; } = null!;
    public StagedKind Kind { get; set; }

    // A record's type, or a link's source type.
    public required string Type { get; set; }

    // A record's key and name.
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string? Props { get; set; }

    // A link's relationship and its two ends. A record's own node id is kept in SourceId.
    public string? RelType { get; set; }
    public string? SourceId { get; set; }
    public string? TargetId { get; set; }
    public string? TargetType { get; set; }

    public bool Included { get; set; } = true;
}
