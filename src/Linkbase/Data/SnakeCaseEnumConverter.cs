using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Linkbase.Data;

public sealed class SnakeCaseEnumConverter<T>() : ValueConverter<T, string>(
    value => ToStore(value),
    stored => FromStore(stored))
    where T : struct, Enum
{
    private static readonly Dictionary<T, string> Names = Enum.GetValues<T>()
        .ToDictionary(value => value, value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()));

    private static readonly Dictionary<string, T> Values = Names
        .ToDictionary(pair => pair.Value, pair => pair.Key);

    public static string ToStore(T value) => Names[value];

    public static bool TryFromStore(string? stored, out T value)
    {
        value = default;
        return stored is not null && Values.TryGetValue(stored, out value);
    }

    public static T FromStore(string stored) =>
        Values.TryGetValue(stored, out var value)
            ? value
            : throw new InvalidOperationException($"'{stored}' is not a valid {typeof(T).Name}.");
}
