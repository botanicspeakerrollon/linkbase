using System.Diagnostics.CodeAnalysis;
using Linkbase.Core.Graph;

namespace Linkbase.Core.Explore;

// The nodes on Explore's canvas, which of them have been expanded and how many neighbours each still has to show. The page
// owns one and the JavaScript module only draws it. Nothing here knows about the page, so it can be tested on its own.
public sealed class CanvasGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = [];
    private readonly HashSet<string> _expanded = [];
    private readonly Dictionary<string, int> _remaining = [];

    public int Count => _nodes.Count;

    public IReadOnlyCollection<string> Keys => _nodes.Keys;

    public IEnumerable<GraphNode> Values => _nodes.Values;

    public GraphNode this[string id] => _nodes[id];

    public bool TryGetValue(string id, [NotNullWhen(true)] out GraphNode? node) => _nodes.TryGetValue(id, out node);

    public GraphNode? Find(string id) => _nodes.GetValueOrDefault(id);

    // A node already here stays as it is.
    public bool TryAdd(GraphNode node) => _nodes.TryAdd(node.Id, node);

    // A node already here is replaced.
    public void Set(GraphNode node) => _nodes[node.Id] = node;

    public bool IsExpanded(string id) => _expanded.Contains(id);

    // How many neighbours of the node are still to be shown, which is zero when it has not been expanded.
    public int RemainingFor(string id) => _remaining.GetValueOrDefault(id);

    public void MarkExpanded(string id, int remaining)
    {
        _expanded.Add(id);
        _remaining[id] = remaining;
    }

    // Takes a node off and forgets it, so expanding any of its neighbours brings it back.
    public void Remove(string id)
    {
        _nodes.Remove(id);
        _expanded.Remove(id);
        _remaining.Remove(id);
    }

    // Keeps one node and forgets the rest.
    public GraphNode KeepOnly(string id)
    {
        var keep = _nodes[id];
        Clear();
        _nodes[id] = keep;
        return keep;
    }

    public void Clear()
    {
        _nodes.Clear();
        _expanded.Clear();
        _remaining.Clear();
    }
}
