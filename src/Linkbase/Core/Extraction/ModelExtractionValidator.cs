using System.Text;
using System.Text.Json.Nodes;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;

namespace Linkbase.Core.Extraction;

// Checks the model's answer against the source text before anything is staged, and treats the answer as untrusted. A name
// or value the text doesn't contain was invented, so it is dropped. Long values are dropped too, because only facts are kept,
// never the source's own wording. A choice or true/false value is one of a few fixed answers, so it can only be checked
// against the field, not the text.
public static class ModelExtractionValidator
{
    public const int MaxValueLength = 200;

    // A field with this key holds a brief summary in the model's own words, so the text can't be searched for it.
    public const string SummaryField = "notes";

    public sealed record Result(IReadOnlyList<StagedRecord> Records, IReadOnlyList<ExtractedLink> Links, IReadOnlyList<string> Warnings);

    // Equal results mean "the same text": ignores case, accents, punctuation and spacing.
    public static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    public static Result Validate(ExtractionSchema schema, ExtractedData data, string sourceText, RecordHooks? hooks = null)
    {
        var source = Normalise(sourceText);
        var warnings = new List<string>();
        bool InSource(string? value) => !string.IsNullOrWhiteSpace(value) && Normalise(value) is { Length: > 0 } plain && source.Contains(plain, StringComparison.Ordinal);

        var records = new Dictionary<string, StagedRecord>(StringComparer.Ordinal);
        foreach (var record in data.Records)
        {
            if (schema.Type(record.Type) is not { } type)
            {
                warnings.Add($"Dropped '{record.Name}': {record.Type} isn't a type in this model.");
                continue;
            }

            var name = record.Name?.Trim() ?? "";
            if (name.Length > MaxValueLength || !InSource(name))
            {
                warnings.Add($"Dropped {type.Name} '{Shorten(name)}': it isn't in the text.");
                continue;
            }

            var props = new JsonObject();
            foreach (var value in record.Fields)
            {
                var field = type.Fields.FirstOrDefault(f => f.Key == value.Field);
                if (field is null)
                {
                    warnings.Add($"Ignored {name}'s {value.Field}: {type.Name} has no such field.");
                }
                else if (!props.ContainsKey(field.Key) && Check(field, value.Value, InSource, sourceText) is { } typed)
                {
                    props[field.Key] = typed;
                }
                else if (!props.ContainsKey(field.Key))
                {
                    warnings.Add($"Removed {name}'s {field.Key} '{Shorten(value.Value)}': it isn't in the text or doesn't fit the field.");
                }
            }

            if (hooks is not null)
            {
                warnings.AddRange(hooks.CheckExtracted(type.Name, name, props, sourceText));
            }

            if (type.Fields.FirstOrDefault(f => f.Required && !props.ContainsKey(f.Key)) is { } missing)
            {
                warnings.Add($"Dropped {type.Name} '{name}': {missing.Key} is required and the text doesn't give it.");
                continue;
            }

            // The same name twice is one record. A different name with the same slug, such as "A-B" and "A B", gets the next free key.
            if (records.Values.Any(r => r.Type == type.Name && Normalise(r.Name) == Normalise(name)))
            {
                continue;
            }

            var stem = RecordRepository.Slug(name);
            var staged = new StagedRecord(type.Name, stem, name, props.ToJsonString());
            for (var n = 2; records.ContainsKey(staged.Id); n++)
            {
                staged = staged with { Key = $"{stem}-{n}" };
            }

            records.Add(staged.Id, staged);
        }

        var links = new List<ExtractedLink>();
        var seen = new HashSet<(string, string, string, string, string, string)>();
        foreach (var link in data.Links)
        {
            if (!schema.Relationships.Any(r => r.RelType == link.RelType && r.FromType == link.FromType && r.ToType == link.ToType))
            {
                warnings.Add($"Dropped the link {link.FromName} {link.RelType} {link.ToName}: the model has no {link.FromType} {link.RelType} {link.ToType}.");
            }
            else if (!InSource(link.FromName) || !InSource(link.ToName))
            {
                warnings.Add($"Dropped the link {link.FromName} {link.RelType} {link.ToName}: a name in it isn't in the text.");
            }
            else
            {
                // The link's own values are checked like a record's fields. The same two records can be linked again only
                // with different values.
                var attributes = schema.LinkAttributes
                    .FirstOrDefault(a => a.FromType == link.FromType && a.RelType == link.RelType && a.ToType == link.ToType)?.Fields ?? [];
                var props = new JsonObject();
                foreach (var value in link.Fields ?? [])
                {
                    var field = attributes.FirstOrDefault(f => f.Key == value.Field);
                    if (field is null)
                    {
                        warnings.Add($"Ignored the {value.Field} of {link.FromName} {link.RelType} {link.ToName}: {link.RelType} has no such value.");
                    }
                    else if (!props.ContainsKey(field.Key) && Check(field, value.Value, InSource, sourceText) is { } typed)
                    {
                        props[field.Key] = typed;
                    }
                    else if (!props.ContainsKey(field.Key))
                    {
                        warnings.Add($"Removed the {field.Key} '{Shorten(value.Value)}' of {link.FromName} {link.RelType} {link.ToName}: it isn't in the text or doesn't fit.");
                    }
                }

                if (attributes.FirstOrDefault(f => f.Required && !props.ContainsKey(f.Key)) is { } absent)
                {
                    warnings.Add($"Dropped the link {link.FromName} {link.RelType} {link.ToName}: {absent.Key} is required and the text doesn't give it.");
                }
                else if (seen.Add((link.RelType, link.FromType, Normalise(link.FromName), link.ToType, Normalise(link.ToName), props.ToJsonString())))
                {
                    links.Add(link with { FromName = link.FromName.Trim(), ToName = link.ToName.Trim(), Fields = null, Props = props.ToJsonString() });
                }
            }
        }

        return new Result(records.Values.ToList(), links, warnings);
    }

    // The value as the field stores it, or null if the text doesn't back it or it doesn't fit.
    private static JsonNode? Check(ExtractionField field, string? text, Func<string?, bool> inSource, string sourceText)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxValueLength)
        {
            return null;
        }

        var typed = CsvImportService.Typed(text, field.Type, field.Options);
        return typed is null ? null : field.Type switch
        {
            FieldType.Choice or FieldType.Boolean => typed,
            // Dates are written as yyyy-mm-dd whatever the text's style, so the text must at least have the year.
            FieldType.Date => sourceText.Contains(text[..4], StringComparison.Ordinal) ? typed : null,
            FieldType.List => FieldValues.SplitList(text, FieldValues.ListSeparator).All(item => inSource(item)) ? typed : null,
            FieldType.Text when field.Key == SummaryField => typed,
            _ => inSource(text) ? typed : null,
        };
    }

    private static string Shorten(string? text) => text is null ? "" : text.Length <= 40 ? text : text[..40] + "…";
}
