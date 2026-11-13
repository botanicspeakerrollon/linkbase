using Linkbase.Data;

namespace Linkbase.Core.Models;

// A field of a type, or a value a relationship's links carry (a link attribute), as the editor shows and saves it. A text
// field with several values is Text with Multiple, however it is stored. Owner is the domain module that keeps the field's
// value, so people can't change it. See docs/models.md.
public sealed record FieldSpec(
    string Key, FieldType Type, IReadOnlyList<string>? Options = null, bool Required = false, bool Multiple = false,
    string? Description = null, string? Group = null)
{
    public string? Owner { get; init; }

    public IReadOnlyList<string> OptionList => Options ?? [];

    // Equal when everything a person can edit is equal. The options are compared by value, which records don't do on their own.
    public bool Equals(FieldSpec? other) =>
        other is not null && Key == other.Key && Type == other.Type && Required == other.Required && Multiple == other.Multiple
        && Description == other.Description && Group == other.Group && Owner == other.Owner && OptionList.SequenceEqual(other.OptionList);

    public override int GetHashCode() => HashCode.Combine(Key, Type, Required, Multiple, Description, Group, OptionList.Count);
}

// Where a set of fields lives: a type's own, or the values on the links of one relationship. Rel is null for a type.
public sealed record FieldHome(string Type, string? Rel = null, string? To = null)
{
    public static FieldHome OfType(string type) => new(type);

    public static FieldHome OfLink(string from, string rel, string to) => new(from, rel, to);

    public bool IsLink => Rel is not null;

    // "Route", or "Person WORKS_ON Project".
    public string Label => IsLink ? $"{Type} {Rel} {To}" : Type;

    // What the field is called in messages: "field" on a type, "value" on a link.
    public string Noun => IsLink ? "value" : "field";
}

public sealed record LevelSpec(string Kind, bool Root, IReadOnlyList<string> Under);

// How a type's records form a tree: By is the choice field saying the level, Via the relationship from a record to its parent.
public sealed record HierarchySpec(string By, string Via, IReadOnlyList<LevelSpec> Levels);

// Watched is whether a domain module reads or writes the type's records, so what it relies on can't be taken away.
public sealed record TypeOutline(
    string Name, string? Color, string? Icon, string? NameTemplate, string? Owner, bool Watched, IReadOnlyList<FieldSpec> Fields, HierarchySpec? Hierarchy);

public sealed record RelationshipOutline(
    string From, string Rel, string To, bool Required, int? Max, long Links, bool Watched, IReadOnlyList<FieldSpec> Attributes)
{
    public string Label => $"{From} {Rel} {To}";
}

public sealed record ModelOutline(IReadOnlyList<TypeOutline> Types, IReadOnlyList<RelationshipOutline> Relationships);
