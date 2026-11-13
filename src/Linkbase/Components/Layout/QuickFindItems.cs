using Linkbase.Core;
using Linkbase.Core.Explore;
using Linkbase.Core.Graph;
using Linkbase.Core.Records;
using MudBlazor;

namespace Linkbase.Components.Layout;

// What Quick-find closes with: somewhere to go, or a type of record to start adding.
public sealed record QuickFindChoice(string? Href, string? AddType);

public sealed record QuickFindItem(string Group, string Title, string? Hint, string Icon, string? Color, QuickFindChoice Choice);

// The things Quick-find offers and how a typed text orders them. Pure, so the choices can be tested without a page.
public static class QuickFindItems
{
    public const string RecordsGroup = "Records";
    public const string PagesGroup = "Go to";
    public const string ActionsGroup = "Actions";

    public static QuickFindItem ForRecord(GraphNode node)
    {
        var style = GraphStyle.For(node.Label);
        return new QuickFindItem(RecordsGroup, node.Name, node.Label, style.Icon, style.Color,
            new QuickFindChoice(GraphStyle.Link(node) ?? GraphStyle.RecordLink(node.Label, node.Id[(node.Id.IndexOf(':') + 1)..]), null));
    }

    // The pages in the rail, in its order, and a list for each type.
    public static IReadOnlyList<QuickFindItem> Pages(IEnumerable<IDomainModule> domains, IEnumerable<RecordType> types)
    {
        var pages = new List<QuickFindItem>
        {
            Page("Home", "/", Icons.Material.Outlined.Home),
            Page("Explore", "/explore", Icons.Material.Filled.Hub),
        };
        pages.AddRange(domains.SelectMany(d => d.NavItems).Select(n => Page(n.Title, "/" + n.Href.TrimStart('/'), n.Icon)));
        foreach (var type in types)
        {
            var style = GraphStyle.For(type.Name, type.Color, type.Icon);
            pages.Add(new QuickFindItem(PagesGroup, type.Name, "List", style.Icon, style.Color, new QuickFindChoice(GraphStyle.RecordListLink(type.Name), null)));
        }

        pages.Add(Page("Import", "/import", Icons.Material.Filled.UploadFile));
        pages.Add(Page("Guide", "/guide", Icons.Material.Outlined.HelpOutline));
        return pages;
    }

    // Derived types can't be added by hand.
    public static IReadOnlyList<QuickFindItem> Actions(IEnumerable<RecordType> types) =>
        types.Where(t => t.Owner is null).Select(t =>
        {
            var style = GraphStyle.For(t.Name, t.Color, t.Icon);
            return new QuickFindItem(ActionsGroup, $"Add {t.Name}", null, Icons.Material.Filled.Add, style.Color, new QuickFindChoice(null, t.Name));
        }).ToList();

    // With no text, the pages and actions as they are. With text, the records first (already matched on name), then whatever
    // pages and actions contain every word, those that start with the text before the rest.
    public static IReadOnlyList<QuickFindItem> Arrange(
        string? text, IEnumerable<QuickFindItem> records, IEnumerable<QuickFindItem> pages, IEnumerable<QuickFindItem> actions)
    {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rest = pages.Concat(actions).ToList();
        if (words.Length == 0)
        {
            return rest;
        }

        var phrase = string.Join(' ', words);
        var matched = rest
            .Where(i => words.All(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(i => i.Title.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return records.Concat(matched).ToList();
    }

    private static QuickFindItem Page(string title, string href, string icon) =>
        new(PagesGroup, title, null, icon, null, new QuickFindChoice(href, null));
}
