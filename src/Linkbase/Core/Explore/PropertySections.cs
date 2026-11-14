using Linkbase.Core.Graph;

namespace Linkbase.Core.Explore;

// A node's values shown together under the heading its model file gives them, in the order the model lists its fields.
// A group sits where its first field is, as on the record page.
public sealed record PropertySection(string? Group, IReadOnlyList<GraphProperty> Properties)
{
    public static List<PropertySection> Of(string label, IEnumerable<GraphProperty> properties, GraphSchema? schema)
    {
        // Fields the model defines come first, in its order; anything else the record happens to carry follows.
        var defined = schema?.Properties.Where(p => p.Label == label && p.Key != "name").ToList() ?? [];
        var order = defined.Select((p, i) => (p.Key, i)).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().i);
        var groups = defined.Where(p => p.Group is not null).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Group);
        var ordered = properties.OrderBy(p => order.GetValueOrDefault(p.Key, int.MaxValue)).ToList();

        var sections = new List<PropertySection>();
        foreach (var property in ordered)
        {
            var group = groups.GetValueOrDefault(property.Key);
            if (group is null)
            {
                sections.Add(new PropertySection(null, [property]));
            }
            else if (!sections.Any(s => s.Group == group))
            {
                sections.Add(new PropertySection(group, ordered.Where(p => groups.GetValueOrDefault(p.Key) == group).ToList()));
            }
        }

        return sections;
    }
}
