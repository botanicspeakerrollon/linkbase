using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsvHelper;
using CsvHelper.Configuration;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Linkbase.Core.CsvImport;

public sealed partial class CsvImportService
{
    private static CsvReader Open(string path) =>
        new(new StreamReader(path, detectEncodingFromByteOrderMarks: true), new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            BadDataFound = null,
            MissingFieldFound = null,
        });

    // A column's different values, ignoring case, in the spelling first seen, sorted. Stops counting just past the limit.
    private static List<string> ChoicesIn(string path, int column)
    {
        using var reader = Open(path);
        reader.Read();
        reader.ReadHeader();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (found.Count <= FieldValues.MaxOptions && reader.Read())
        {
            if (reader.GetField(column)?.Trim() is { Length: > 0 } value)
            {
                found.TryAdd(value, value);
            }
        }

        return found.Values.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static FieldType Guess(List<string> values) =>
        values.Count == 0 ? FieldType.Text
        : values.All(v => Typed(v, FieldType.Number) is not null) ? FieldType.Number
        : values.All(v => Typed(v, FieldType.Date) is not null) ? FieldType.Date
        : values.All(v => Typed(v, FieldType.Boolean) is not null) ? FieldType.Boolean
        : FieldType.Text;

    // The value as its field's type, as the loader stores it, or null if it isn't one.
    internal static JsonNode? Typed(string text, FieldType type, IReadOnlyList<string>? options = null) => type switch
    {
        FieldType.Choice => FieldValues.MatchChoice(text, options ?? []) is { } option ? JsonValue.Create(option) : null,
        FieldType.List => FieldValues.SplitList(text, FieldValues.ListSeparator) is { Count: > 0 } values
            ? new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
            : null,
        FieldType.Number => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : null,
        FieldType.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : null,
        FieldType.Boolean => bool.TryParse(text, out var flag) ? JsonValue.Create(flag) : null,
        _ => JsonValue.Create(text),
    };

    public static string Labelled(FieldType type) => type switch
    {
        FieldType.Number => "a number",
        FieldType.Date => "a date (yyyy-mm-dd)",
        FieldType.Boolean => "true or false",
        FieldType.Choice => "a choice",
        FieldType.List => "a list, split by |",
        _ => "text",
    };
}
