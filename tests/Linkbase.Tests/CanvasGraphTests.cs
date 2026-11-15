using Linkbase.Core.Explore;
using Linkbase.Core.Graph;

namespace Linkbase.Tests;

public class CanvasGraphTests
{
    private static GraphNode Node(string id) => new(id, id, id, []);

    [Fact]
    public void Adding_a_node_already_there_leaves_it_as_it_was()
    {
        var graph = new CanvasGraph();
        var first = Node("a");
        graph.TryAdd(first);

        Assert.False(graph.TryAdd(new GraphNode("a", "other", "other", [])));
        Assert.Same(first, graph["a"]);
    }

    [Fact]
    public void Removing_a_node_forgets_that_it_was_expanded()
    {
        var graph = new CanvasGraph();
        graph.TryAdd(Node("a"));
        graph.MarkExpanded("a", 7);
        Assert.True(graph.IsExpanded("a"));
        Assert.Equal(7, graph.RemainingFor("a"));

        graph.Remove("a");

        Assert.False(graph.IsExpanded("a"));
        Assert.Equal(0, graph.RemainingFor("a"));
        Assert.Equal(0, graph.Count);
    }

    [Fact]
    public void Keeping_only_one_node_drops_the_rest_and_their_expansion()
    {
        var graph = new CanvasGraph();
        graph.TryAdd(Node("a"));
        graph.TryAdd(Node("b"));
        graph.MarkExpanded("a", 1);
        graph.MarkExpanded("b", 2);

        var kept = graph.KeepOnly("b");

        Assert.Equal("b", kept.Id);
        Assert.Equal(["b"], graph.Keys.ToList());
        Assert.False(graph.IsExpanded("a"));
        Assert.Equal(0, graph.RemainingFor("a"));
    }
}
