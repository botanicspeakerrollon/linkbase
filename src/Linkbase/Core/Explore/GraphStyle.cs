using System.Text.Json.Serialization;
using Linkbase.Domains;
using Linkbase.Core.Graph;
using MudBlazor;

namespace Linkbase.Core.Explore;

// How one type of node looks and behaves. The colour and icon also go to graph.js.
public sealed record LabelStyle(string Label, string Color, string Icon)
{
    // Set when this style is only for nodes of the type whose "kind" property has this value, such as the Area nodes that are countries.
    public string? Kind { get; init; }

    [JsonIgnore] public Func<GraphNode, string>? Caption { get; init; }

    // Where to edit or view a node of this type in the rest of the app, if anywhere.
    [JsonIgnore] public Func<GraphNode, string>? Link { get; init; }
}

public static class GraphStyle
{
    // Types with no style of their own get one of these, always the same one for the same type.
    private static readonly string[] Palette = ["#0f766e", "#b45309", "#4338ca", "#be123c", "#4d7c0f", "#a21caf", "#0369a1", "#9f1239"];

    private static readonly Dictionary<string, LabelStyle> Known = RegisteredDomains.All.SelectMany(d => d.LabelStyles).Where(s => s.Kind is null).ToDictionary(s => s.Label);

    private static readonly IReadOnlyList<LabelStyle> KindStyles = RegisteredDomains.All.SelectMany(d => d.LabelStyles).Where(s => s.Kind is not null).ToList();

    // The styles for part of a type, which draw over the type's own.
    public static IEnumerable<LabelStyle> KindStylesOf(string label) => KindStyles.Where(s => s.Label == label);

    // A node's own style: its kind's if it has one, else its type's.
    public static LabelStyle For(GraphNode node, GraphSchema? schema = null) =>
        KindStyles.FirstOrDefault(s => s.Label == node.Label && s.Kind == node.Property("kind")) ?? For(node.Label, schema);

    // A model file's colour and icon come first, then a domain's style, then a stable default.
    public static LabelStyle For(string label, GraphSchema? schema = null)
    {
        var defined = schema?.Types.FirstOrDefault(t => t.Label == label);
        return For(label, defined?.Color, defined?.Icon);
    }

    public static LabelStyle For(string label, string? color, string? icon)
    {
        var known = Known.GetValueOrDefault(label);
        var shown = IconNamed(icon) ?? known?.Icon ?? Icons.Material.Filled.Circle;
        var style = known ?? new LabelStyle(label, PaletteColor(label), shown);
        return style with { Color = color ?? style.Color, Icon = shown };
    }

    // A Material icon by name, such as Train, or null if there's no such icon.
    public static string? IconNamed(string? name) =>
        name is null ? null : typeof(Icons.Material.Filled).GetField(name)?.GetValue(null) as string;

    public static string Caption(GraphNode node) => node.IsModel
        ? $"{node.Label} ({node.Property("nodes")})"
        : For(node.Label).Caption?.Invoke(node) ?? node.Name;

    // A domain's own page, or else the generated record page, whose ids are the type in lower case, a colon and the key.
    public static string? Link(GraphNode node) => node.IsModel
        ? null
        : Known.TryGetValue(node.Label, out var known)
            ? known.Link?.Invoke(node)
            : RecordLink(node.Label, node.Id[(node.Id.IndexOf(':') + 1)..]);

    public static string RecordLink(string type, string key) => $"/data/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(key)}";

    public static string RecordListLink(string type) => $"/data/{Uri.EscapeDataString(type)}";

    // Short enough to draw on a link, such as 940, 9.4k, 944k or 42M.
    public static string Compact(long count) => count switch
    {
        < 1_000 => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        < 10_000 => (count / 1_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k",
        < 999_500 => (count / 1_000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "k",
        < 9_950_000 => (count / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M",
        _ => (count / 1_000_000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "M",
    };

    public static string PropertyTitle(string key) =>
        char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');

    // Not string.GetHashCode, which changes on every run.
    private static string PaletteColor(string label) =>
        Palette[label.Aggregate(0, (hash, c) => unchecked((hash * 31) + c) & 0x7fffffff) % Palette.Length];
}
