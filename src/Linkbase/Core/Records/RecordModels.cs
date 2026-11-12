using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Records;
// Options are a choice field's values, in the order to offer them, and empty for every other type. Owner is the domain
// module that keeps the value, such as Climbing, for a field people can't edit.
public sealed record RecordField(string Key, FieldType Type, IReadOnlyList<string> Options, string? Owner = null)
{
    public RecordField(string key, FieldType type)
        : this(key, type, [])
    {
    }

    // A record can't be saved without a value for it.
    public bool Required { get; init; }

    // Shown beside the field, as a hint.
    public string? Description { get; init; }

    // Fields of a type with the same group are shown together under its heading.
    public string? Group { get; init; }

    // The field holds several values, each of the type's kind (text for a List), kept as a JSON array.
    public bool Multiple { get; init; }

    public bool IsMany => Multiple || Type == FieldType.List;

    // The type of one value: a list's are text.
    public FieldType ElementType => Type == FieldType.List ? FieldType.Text : Type;

    // Records compare lists by reference, so compare the options by value.
    public bool Equals(RecordField? other) =>
        other is not null && Key == other.Key && Type == other.Type && Required == other.Required && Options.SequenceEqual(other.Options)
        && Owner == other.Owner && Description == other.Description && Group == other.Group && Multiple == other.Multiple;

    public override int GetHashCode() => HashCode.Combine(Key, Type, Required, Options.Count, Owner, Group, Multiple);
}

// Fields shown together under a heading, or the ungrouped ones with none. A group sits where its first field is.
public sealed record FieldSection(string? Group, IReadOnlyList<RecordField> Fields)
{
    public static List<FieldSection> Of(IEnumerable<RecordField> fields)
    {
        var all = fields.ToList();
        var sections = new List<FieldSection>();
        foreach (var field in all)
        {
            if (field.Group is null)
            {
                sections.Add(new FieldSection(null, [field]));
            }
            else if (!sections.Any(s => s.Group == field.Group))
            {
                sections.Add(new FieldSection(field.Group, all.Where(f => f.Group == field.Group).ToList()));
            }
        }

        return sections;
    }
}

// A kind of link every record of a type needs at least one of, such as an ascent's OF link to a Route.
public sealed record RequiredLink(string RelType, string OtherType);

// A relationship's limit on how many of its links one record may have, such as 1 for an area's parent.
public sealed record LinkLimit(string RelType, string OtherType, int Max);

// Owner is the domain module that derives the type's records, such as Climbing for trips. Only it writes them.
public sealed record RecordType(string Name, string? Color, string? Icon, IReadOnlyList<RecordField> Fields, string? Owner = null)
{
    // How a record's name is made from its fields, such as "{name_first} {name_last}", so people don't type one.
    public string? NameTemplate { get; init; }

    public IReadOnlyList<RequiredLink> RequiredLinks { get; init; } = [];

    // The relationships going out of the type that have a max.
    public IReadOnlyList<LinkLimit> LinkLimits { get; init; } = [];

    // Set when the type's records form a tree, such as areas in regions in countries.
    public HierarchyRules? Hierarchy { get; init; }

    // The fields people fill in, as the edit form shows them.
    public IEnumerable<RecordField> Editable => Fields.Where(f => f.Owner is null);
}

// A record a domain module derives, such as a trip. Values are as a form sends them.
public sealed record DerivedRecord(string Key, string Name, IReadOnlyDictionary<string, string?> Values);

// A link a domain module derives, between record ids, such as an ascent ON_TRIP a trip.
public sealed record DerivedLink(string SourceId, string RelType, string TargetId, JsonObject? Props = null);

// Values are shown as text: numbers as written, dates as yyyy-MM-dd, booleans as true or false, and a list's values one
// per line.
public sealed record RecordRow(string Id, string Key, string Name, IReadOnlyDictionary<string, string> Values);

public sealed record RecordRows(int Total, IReadOnlyList<RecordRow> Rows);

// How a filter compares a field: Contains for text and lists, Is for choices and booleans (and exact numbers and dates),
// AtLeast and AtMost for the ends of a number or date range.
public enum FilterOp { Contains, Is, AtLeast, AtMost }

public sealed record RecordFilter(string Field, FilterOp Op, string Value);

// Sorts by a field's key, or by Name or Key (the default), which are always there. Blank values come last either way.
public sealed record RecordSort(string Field, bool Descending = false);

public sealed record RecordDetail(
    string Id, string Type, string Key, string Name, IReadOnlyDictionary<string, string> Values, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// What a form sends: the name, and each field's value as typed, empty for none. A new record also brings its links, at
// least those its type requires.
public sealed record RecordInput(string Name, IReadOnlyDictionary<string, string?> Values)
{
    public IReadOnlyList<NewLink> Links { get; init; } = [];

    // Records to create along with it, each linked to it, such as the Route and Crag a new Ascent needs. They can need
    // records of their own. All are saved together or not at all.
    public IReadOnlyList<NewLinkedRecord> Creates { get; init; } = [];
}

// A record a dialog has collected the details of, to be created by whoever asked for it.
public sealed record PendingRecord(string Type, string? Key, RecordInput Input);

// A record to create and link the new record to: Key is made from the name when empty, as for any new record.
public sealed record NewLinkedRecord(string RelType, string Type, string? Key, RecordInput Input, IReadOnlyDictionary<string, string?>? Values = null);

// A link from a new record to one already here.
// Values are the link's attributes as a form sends them, if its relationship has any.
public sealed record NewLink(string RelType, string TargetId, IReadOnlyDictionary<string, string?>? Values = null);

// Values are the link's attributes, shown as text like a record's, such as a person's role on a project.
public sealed record RecordLink(long Id, string OtherId, string OtherType, string OtherKey, string OtherName)
{
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}

// The links of one kind on one side of a record, such as every Service that CALLS_AT this Stop. Only the first few come
// back, with the total.
public sealed record RecordLinkGroup(string RelType, bool Outgoing, int Total, IReadOnlyList<RecordLink> Links);

// A kind of link a record can have, from the model's relationships.
public sealed record LinkOption(string RelType, bool Outgoing, string OtherType);

public sealed record RecordOption(string Id, string Name, string Key);

// A relationship the model has, such as Service CALLS_AT Stop.
public sealed record RelationshipDefinition(string FromType, string RelType, string ToType);
