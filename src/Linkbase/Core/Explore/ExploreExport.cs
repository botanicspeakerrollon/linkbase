using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Linkbase.Core.Graph;

namespace Linkbase.Core.Explore;

// What's on the Explore canvas, as files: nodes and links as CSV for a spreadsheet, or both as JSON. Only what's shown is
// exported, so links to nodes that were removed are left out.
public static class ExploreExport
{
    // A cell starting with =, +, - or @ is escaped, so a spreadsheet opening the file shows it rather than running it.
    private static readonly CsvConfiguration Csv = new(CultureInfo.InvariantCulture) { InjectionOptions = InjectionOptions.Escape };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // One row per node: its id, type and name, then a column for every property any of them has, in the order first seen.
    public static string NodesCsv(IReadOnlyList<GraphNode> nodes)
    {
        var keys = nodes.SelectMany(n => n.Properties.Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToList();
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvWriter(text, Csv))
        {
            foreach (var header in new[] { "id", "type", "name" }.Concat(keys))
            {
                csv.WriteField(header);
            }

            csv.NextRecord();
            foreach (var node in nodes)
            {
                csv.WriteField(node.Id);
                csv.WriteField(node.Label);
                csv.WriteField(node.Name);
                foreach (var key in keys)
                {
                    csv.WriteField(node.Property(key) ?? "");
                }

                csv.NextRecord();
            }
        }

        return text.ToString();
    }

    // One row per link between two of the nodes, with both ends' ids, types and names.
    public static string LinksCsv(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvWriter(text, Csv))
        {
            foreach (var header in new[] { "source_id", "source_type", "source_name", "relationship", "target_id", "target_type", "target_name" })
            {
                csv.WriteField(header);
            }

            csv.NextRecord();
            foreach (var edge in Shown(byId, edges))
            {
                var (source, target) = (byId[edge.SourceId], byId[edge.TargetId]);
                foreach (var field in new[] { source.Id, source.Label, source.Name, edge.RelType, target.Id, target.Label, target.Name })
                {
                    csv.WriteField(field);
                }

                csv.NextRecord();
            }
        }

        return text.ToString();
    }

    // Everything at once, for another program to read: nodes with their properties, and the links between them.
    public static string AsJson(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var document = new
        {
            nodes = nodes.Select(n => new
            {
                id = n.Id,
                type = n.Label,
                name = n.Name,
                properties = n.Properties.ToDictionary(p => p.Key, p => p.Value),
            }),
            links = Shown(byId, edges).Select(e => new { source = e.SourceId, relationship = e.RelType, target = e.TargetId }),
        };
        return JsonSerializer.Serialize(document, Json);
    }

    // A file name that says what and when, such as linkbase-nodes-2026-11-08-1903.csv.
    public static string FileName(string what, string extension, DateTimeOffset now) =>
        $"linkbase-{what}-{now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.{extension}";

    public static byte[] Utf8(string text) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    // Links whose ends are both shown, once each.
    private static IEnumerable<GraphEdge> Shown(Dictionary<string, GraphNode> byId, IReadOnlyList<GraphEdge> edges) =>
        edges.Where(e => byId.ContainsKey(e.SourceId) && byId.ContainsKey(e.TargetId))
            .DistinctBy(e => (e.SourceId, e.RelType, e.TargetId));
}
