using Linkbase.Core.Graph;
using MudBlazor;

namespace Linkbase.Core.Explore;

// Looks up how a node or type is drawn, using the styles the registered domains declare. Injected where it's needed;
// GraphStyle holds the helpers that need no domain.
public sealed class GraphStyles
{
    private readonly Dictionary<string, LabelStyle> known;
    private readonly IReadOnlyList<LabelStyle> kindStyles;

    public GraphStyles(DomainRegistry domains)
    {
        var all = domains.All.SelectMany(d => d.LabelStyles).ToList();
        known = all.Where(s => s.Kind is null).ToDictionary(s => s.Label);
        kindStyles = all.Where(s => s.Kind is not null).ToList();
    }

    // The styles for part of a type, which draw over the type's own.
    public IEnumerable<LabelStyle> KindStylesOf(string label) => kindStyles.Where(s => s.Label == label);

    // A node's own style: its kind's if it has one, else its type's.
    public LabelStyle For(GraphNode node, GraphSchema? schema = null) =>
        kindStyles.FirstOrDefault(s => s.Label == node.Label && s.Kind == node.Property("kind")) ?? For(node.Label, schema);

    // A model file's colour and icon come first, then a domain's style, then a stable default.
    public LabelStyle For(string label, GraphSchema? schema = null)
    {
        var defined = schema?.Types.FirstOrDefault(t => t.Label == label);
        return For(label, defined?.Color, defined?.Icon);
    }

    public LabelStyle For(string label, string? color, string? icon)
    {
        var domainStyle = known.GetValueOrDefault(label);
        var shown = GraphStyle.IconNamed(icon) ?? domainStyle?.Icon ?? Icons.Material.Filled.Circle;
        var style = domainStyle ?? new LabelStyle(label, GraphStyle.PaletteColor(label), shown);
        return style with { Color = color ?? style.Color, Icon = shown };
    }

    public string Caption(GraphNode node) => node.IsModel
        ? $"{node.Label} ({node.Property("nodes")})"
        : For(node.Label).Caption?.Invoke(node) ?? node.Name;

    // A domain's own page, or else the generated record page, whose ids are the type in lower case, a colon and the key.
    public string? Link(GraphNode node) => node.IsModel
        ? null
        : known.GetValueOrDefault(node.Label)?.Link?.Invoke(node)
            ?? GraphStyle.RecordLink(node.Label, node.Id[(node.Id.IndexOf(':') + 1)..]);
}
