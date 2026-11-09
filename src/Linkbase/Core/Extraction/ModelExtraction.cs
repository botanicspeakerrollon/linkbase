using Linkbase.Core.Records;
using Linkbase.Data;

namespace Linkbase.Core.Extraction;

// What the extractor is told about a model: the types it may record, each with its fields, and the links it may make.
public sealed record ExtractionField(string Key, FieldType Type, IReadOnlyList<string> Options, bool Required);

public sealed record ExtractionType(string Name, IReadOnlyList<ExtractionField> Fields, IReadOnlyList<RequiredLink> RequiredLinks);

public sealed record ExtractionSchema(IReadOnlyList<ExtractionType> Types, IReadOnlyList<RelationshipDefinition> Relationships)
{
    // Records a module derives can't be imported, and a field a module keeps is filled in by that module.
    public static ExtractionSchema From(IEnumerable<RecordType> types, IEnumerable<RelationshipDefinition> relationships)
    {
        var usable = types.Where(t => t.Owner is null).ToList();
        var names = usable.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return new ExtractionSchema(
            usable.Select(t => new ExtractionType(
                t.Name,
                t.Editable.Select(f => new ExtractionField(f.Key, f.Type, f.Options, f.Required)).ToList(),
                t.RequiredLinks)).ToList(),
            relationships.Where(r => names.Contains(r.FromType) && names.Contains(r.ToType)).ToList());
    }

    public ExtractionType? Type(string name) => Types.FirstOrDefault(t => t.Name == name);
}

// What the model returns, in the shape of the tool's JSON schema. A value is a field's text as the source writes it.
public sealed record ExtractedValue(string Field, string Value);

public sealed record ExtractedRecord(string Type, string Name, IReadOnlyList<ExtractedValue> Fields);

public sealed record ExtractedLink(string RelType, string FromType, string FromName, string ToType, string ToName);

public sealed record ExtractedData(IReadOnlyList<ExtractedRecord> Records, IReadOnlyList<ExtractedLink> Links)
{
    public static readonly ExtractedData Empty = new([], []);
}

public interface IModelExtractor
{
    // KnownRecords are names found in earlier parts of a long text, so a later part can link to them.
    Task<ExtractedData> ExtractAsync(ExtractionSchema schema, string text, IReadOnlyList<string> knownRecords, CancellationToken cancellationToken);
}
