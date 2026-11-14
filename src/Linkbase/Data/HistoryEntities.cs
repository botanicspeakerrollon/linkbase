namespace Linkbase.Data;

// The audit trail: what changed, who changed it, when, and why. See docs/history/audit-history-plan.md.

// manual: a person edited something. import: an import was committed or undone, or a model file was loaded. derived: a domain
// module rebuilt records it owns, such as trips. correction: a fix from the corrections file.
public enum JustificationKind { Manual, Import, Derived, Correction }

// What a change is to. A node is a record. A link is a record-to-record link, with the source in EntityId and the target in
// RelatedId. A label is a change to who may see a node or link, written the same way. A definition is part of the model: a type, field or relationship. An import batch stands for everything the
// import added or removed, which is not logged row by row.
public enum ChangeEntityKind { Node, Link, ExternalRef, Definition, ImportBatch, Label, PropertyLabel }

public enum ChangeOp { Create, Update, Delete }

// The model's history settings, from its model file: at most one row, and none means both are on. Turning recording off
// stops new entries and keeps the ones already there.
public class ModelSetting
{
    public const int OnlyId = 1;

    public int Id { get; set; } = OnlyId;
    public bool RecordHistory { get; set; } = true;
    public bool RequireReason { get; set; } = true;

    // The kinds of source a person can pick, from the model file. Empty means just Internet.
    public string[] SourceKinds { get; set; } = [];

    // The model file's security section as JSON (the parts, their names and values, where grants come from, and any users
    // written in the file), or null when the model has none. See docs/security-design.md.
    public string? Security { get; set; }
}

// The why of a change set. The note is a brief paraphrase, never text copied from a source. The source says where the change
// came from: SourceId identifies it (a web address, a ticket number, a document name), SourceKind says what sort of place that
// is (Internet, or an application in the organisation), and SourceComment is a brief paraphrased remark about it.
public class Justification
{
    public long Id { get; set; }
    public JustificationKind Kind { get; set; }
    public string? Note { get; set; }
    public string? SourceId { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceComment { get; set; }
    public long? ImportBatchId { get; set; }
    public ImportBatch? ImportBatch { get; set; }
    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

// One intent that was saved together: one edit, one import commit, one trip rebuild. CauseChangeSetId is the change set that
// made a module rebuild what it owns.
public class ChangeSet
{
    public long Id { get; set; }
    public long JustificationId { get; set; }
    public Justification Justification { get; set; } = null!;
    public long? CauseChangeSetId { get; set; }
    public ChangeSet? Cause { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// One thing that changed. An update has one row for each field or link attribute that changed, named by FieldKey, with the
// values as text. A create or a delete of a record or link has one row whose value is JSON (name and values, or the link's
// values). A link's RelType and RelatedId say which link. EntityId has no foreign key: a model reload replaces the records,
// and the history of what was there stays.
public class Change
{
    public long Id { get; set; }
    public long ChangeSetId { get; set; }
    public ChangeSet ChangeSet { get; set; } = null!;
    public ChangeEntityKind EntityKind { get; set; }
    public required string EntityId { get; set; }
    public ChangeOp Op { get; set; }
    public string? FieldKey { get; set; }
    public string? RelType { get; set; }
    public string? RelatedId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
