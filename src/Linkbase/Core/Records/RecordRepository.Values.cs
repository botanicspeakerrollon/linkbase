using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.Records;
public sealed partial class RecordRepository
{
    // Lets the modules' hooks set the fields they keep.
    private string Hooked(RecordType type, string name, string props)
    {
        if (!_hooks.Watch(type.Name))
        {
            return props;
        }

        var json = JsonNode.Parse(props) as JsonObject ?? [];
        _hooks.BeforeSave(type, name, json);
        return json.ToJsonString();
    }

    // Keeps the relationship's count, which the home page and the graph model show, in step with its links.
    private static Task<int> CountAsync(LinkbaseDbContext db, string sourceType, string relType, string targetType, int change, CancellationToken cancellationToken) =>
        db.LinkDefinitions
            .Where(d => d.SourceType == sourceType && d.RelType == relType && d.TargetType == targetType)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Links, d => d.Links + change), cancellationToken);

    private static string Name(RecordInput input) =>
        string.IsNullOrWhiteSpace(input.Name) ? throw new LinkbaseValidationException("A record needs a name.") : input.Name.Trim();

    // A type with a name template makes the name from the fields, whatever the form sent as the name.
    private static string Name(RecordType type, RecordInput input) =>
        type.NameTemplate is null
            ? Name(input)
            : NameTemplates.Render(type.NameTemplate, input.Values)
                ?? throw new LinkbaseValidationException($"A {type.Name} needs a value for at least one of {string.Join(", ", NameTemplates.Tokens(type.NameTemplate).Distinct().Select(Title))}, to make its name.");

    // Values are checked against their field's type and stored typed, as the loader does. Values for keys the type doesn't
    // define are kept as they were, and so are those of fields a module keeps, unless it's the module writing them.
    private static string Props(RecordType type, RecordInput input, string? existing = null, bool includeKept = false) =>
        Props(includeKept ? type.Fields : type.Editable, input.Values, existing);

    // The same checks for any set of fields, such as a relationship's attributes.
    internal static string Props(IEnumerable<RecordField> fields, IReadOnlyDictionary<string, string?> values, string? existing = null)
    {
        var props = (existing is null ? null : JsonNode.Parse(existing) as JsonObject) ?? [];
        foreach (var field in fields)
        {
            var text = values.GetValueOrDefault(field.Key)?.Trim();
            if (field.IsMany && FieldValues.SplitList(text ?? "", '\n', '\r').Count == 0)
            {
                text = null;
            }

            if (string.IsNullOrEmpty(text))
            {
                props.Remove(field.Key);
                if (field.Required)
                {
                    throw new LinkbaseValidationException($"{Title(field.Key)} is required.");
                }

                continue;
            }

            props[field.Key] = field.IsMany
                ? new JsonArray(FieldValues.SplitList(text, '\n', '\r').Select(v => Single(field, v)).ToArray())
                : Single(field, text);
        }

        return props.ToJsonString();
    }

    // One value as its field's type (a several-valued field's type is its values').
    private static JsonValue? Single(RecordField field, string text) => field.ElementType switch
    {
        FieldType.Number => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : throw new LinkbaseValidationException($"{Title(field.Key)} must be a number, such as 12.5."),
        FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : throw new LinkbaseValidationException($"{Title(field.Key)} must be a date, such as 2026-11-06."),
        FieldType.Boolean => bool.TryParse(text, out var flag)
            ? JsonValue.Create(flag)
            : throw new LinkbaseValidationException($"{Title(field.Key)} must be true or false."),
        FieldType.Choice => FieldValues.MatchChoice(text, field.Options) is { } option
            ? JsonValue.Create(option)
            : throw new LinkbaseValidationException($"{Title(field.Key)} must be one of {FieldValues.Listed(field.Options)}."),
        _ => JsonValue.Create(text),
    };

    internal static Dictionary<string, string> Values(string props)
    {
        using var document = JsonDocument.Parse(props);
        return document.RootElement.EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString()!,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => string.Join('\n', p.Value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
                _ => p.Value.GetRawText(),
            });
    }

    private static async Task<string> FreeKeyAsync(LinkbaseDbContext db, string type, string stem, CancellationToken cancellationToken)
    {
        var taken = await db.Nodes.AsNoTracking()
            .Where(n => n.Type == type && (n.Key == stem || n.Key.StartsWith(stem + "-")))
            .Select(n => n.Key)
            .ToListAsync(cancellationToken);
        var key = stem;
        for (var i = 2; taken.Contains(key); i++)
        {
            key = $"{stem}-{i}";
        }

        return key;
    }

    // Lower case letters, digits and hyphens, such as zoe-adams for Zoë Adams.
    internal static string Slug(string name)
    {
        var plain = name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        var slug = new string(plain.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "record" : slug[..Math.Min(slug.Length, 60)].TrimEnd('-');
    }

    public static string Title(string key) => char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
