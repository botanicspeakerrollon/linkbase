using System.Text.Json.Serialization;
using Linkbase.Core.Records;
using Linkbase.Data;

namespace Linkbase.Core.Extraction;

// What the extractor is told about a model: the types it may record, each with its fields, and the links it may make.
public sealed record ExtractionField(string Key, FieldType Type, IReadOnlyList<string> Options, bool Required);

public sealed record ExtractionType(string Name, IReadOnlyList<ExtractionField> Fields, IReadOnlyList<RequiredLink> RequiredLinks);

// The values a link of one relationship can carry, such as a person's role on a project.
public sealed record ExtractionLinkAttributes(string FromType, string RelType, string ToType, IReadOnlyList<ExtractionField> Fields);

public sealed record ExtractionSchema(IReadOnlyList<ExtractionType> Types, IReadOnlyList<RelationshipDefinition> Relationships)
{
    // What each relationship's links can carry. Only relationships that carry something are listed.
    public IReadOnlyList<ExtractionLinkAttributes> LinkAttributes { get; init; } = [];

    // What a module adds to the prompt about its types, such as how its grades are written.
    public string? Notes { get; init; }

    // Records a module derives can't be imported, and a field a module keeps is filled in by that module.
    public static ExtractionSchema From(
        IEnumerable<RecordType> types, IEnumerable<RelationshipDefinition> relationships, RecordHooks? hooks = null,
        IReadOnlyDictionary<(string From, string Rel, string To), IReadOnlyList<RecordField>>? linkAttributes = null)
    {
        var usable = types.Where(t => t.Owner is null).ToList();
        var names = usable.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return new ExtractionSchema(
            usable.Select(t => new ExtractionType(
                t.Name,
                t.Editable.Where(Readable).Select(f => new ExtractionField(f.Key, f.Type, f.Options, f.Required)).ToList(),
                t.RequiredLinks)).ToList(),
            relationships.Where(r => names.Contains(r.FromType) && names.Contains(r.ToType)).ToList())
        {
            Notes = hooks?.ExtractionNotes(names),
            LinkAttributes = (linkAttributes ?? new Dictionary<(string, string, string), IReadOnlyList<RecordField>>())
                .Where(a => a.Value.Count > 0 && names.Contains(a.Key.From) && names.Contains(a.Key.To))
                .Select(a => new ExtractionLinkAttributes(a.Key.From, a.Key.Rel, a.Key.To,
                    a.Value.Where(Readable).Select(f => new ExtractionField(f.Key, f.Type, f.Options, f.Required)).ToList()))
                .ToList(),
        };
    }

    // Reading text leaves out fields that hold several numbers, dates or choices: it can fill in a text list, but not these.
    private static bool Readable(RecordField field) => !(field.Multiple && field.Type != FieldType.List);

    public ExtractionType? Type(string name) => Types.FirstOrDefault(t => t.Name == name);
}

// What the model returns, in the shape of the tool's JSON schema. A value is a field's text as the source writes it.
public sealed record ExtractedValue(string Field, string Value);

public sealed record ExtractedRecord(string Type, string Name, IReadOnlyList<ExtractedValue> Fields);

// Fields are the link's own values, such as a role, as the text writes them. Props are those values once checked, as JSON.
public sealed record ExtractedLink(string RelType, string FromType, string FromName, string ToType, string ToName, IReadOnlyList<ExtractedValue>? Fields = null)
{
    [JsonIgnore]
    public string Props { get; init; } = "{}";
}

public sealed record ExtractedData(IReadOnlyList<ExtractedRecord> Records, IReadOnlyList<ExtractedLink> Links)
{
    public static readonly ExtractedData Empty = new([], []);
}

public interface IModelExtractor
{
    // KnownRecords are names found in earlier parts of a long text, so a later part can link to them.
    Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken);
}
