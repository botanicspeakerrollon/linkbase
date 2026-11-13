using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Linkbase.Core.Explore;
using Linkbase.Core.Graph;
using Linkbase.Core.Records;
using Linkbase.Data;

namespace Linkbase.Core.Views;

// What a page's address carries in its v parameter, and what a saved view stores: the page's state as JSON, made URL-safe.
// The text comes from the address bar, so it's untrusted. A reader that can't make sense of it gets null, and whatever
// the state names, such as a type or a field, is still checked against the model before it's used.
public static class ViewState
{
    // A permalink that long is no use in a chat message, and a longer one is more likely to be an attempt to break the page.
    public const int MaxLength = 8000;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Encode<T>(T state) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state, Options)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static T? Decode<T>(string? text)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return null;
        }

        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)), Options);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Where a view opens. Home, the views dialog and the copy-link button all use this.
    public static string Href(SavedViewKind kind, string? scope, string state)
    {
        var path = kind == SavedViewKind.List && scope is not null ? GraphStyle.RecordListLink(scope) : "/explore";
        return $"{path}?v={Uri.EscapeDataString(state)}";
    }
}

// Where one node sat on the canvas when a view was saved with its layout pinned.
public sealed record ScenePosition(string NodeId, double X, double Y);

// An Explore page's state. A canvas is its Nodes, or, when there are too many for an address, only the Pattern that made it.
// The expansion filters and the table and totals switches come back too, so the page looks as it did. Positions is set only
// when the view was saved with its hand-arranged layout pinned (what used to be a separate "scene"); otherwise the canvas
// lays the nodes out fresh.
public sealed record ExploreState
{
    // More than this and the address gets too long to share. The pattern alone then reopens the first page of matches.
    public const int MaxNodes = 150;

    public GraphPattern? Pattern { get; init; }
    public IReadOnlyList<string>? Nodes { get; init; }
    public IReadOnlyList<ScenePosition>? Positions { get; init; }
    public bool Model { get; init; }
    public bool Table { get; init; }
    public bool Totals { get; init; }
    public IReadOnlyList<string>? RelTypes { get; init; }
    public IReadOnlyList<PatternFilter>? Filters { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Pattern is null && Nodes is not { Count: > 0 } && !Model;

    // Null when there is nothing to come back to, so a blank page has a plain address.
    public string? Encode() => IsEmpty ? null : ViewState.Encode(this);
}

// A record list's state: its search box, its sort and its filters.
public sealed record ListState
{
    public string? Search { get; init; }
    public string? Sort { get; init; }
    public bool Descending { get; init; }
    public IReadOnlyList<RecordFilter>? Filters { get; init; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Search) && Sort is null && Filters is not { Count: > 0 };

    public string? Encode() => IsEmpty ? null : ViewState.Encode(this);
}
