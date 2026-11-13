using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using Linkbase.Data;
using Linkbase.Core.Graph;
using Linkbase.Core.Views;
using FilterOperator = Linkbase.Core.Graph.FilterOperator;

namespace Linkbase.Core.Explore;

public partial class ExplorePage
{
    [JSInvokable]
    public Task OnNodeClicked(string nodeId)
    {
        _menu = null;
        _selected = _nodes.GetValueOrDefault(nodeId);
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnNodeDoubleClicked(string nodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            _menu = null;
            _selected = node;
            if (node.IsModel)
            {
                await ShowTypeAsync(node.Label);
                await InvokeAsync(StateHasChanged);
            }
            else
            {
                await ExpandAsync(nodeId);
            }
        }
    }

    [JSInvokable]
    public async Task OnNodeRightClicked(string nodeId, double x, double y)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            _selected = node;
            _menu = node.IsModel ? null : new ContextMenu(node, x, y, await Graph.RelationshipCountsAsync(nodeId));
            await SelectOnCanvasAsync(nodeId);
            await InvokeAsync(StateHasChanged);
        }
    }

    [JSInvokable]
    public Task OnBackgroundClicked()
    {
        _menu = null;
        _selected = null;
        return InvokeAsync(StateHasChanged);
    }

    // The menu is positioned on the canvas, so it would drift away from its node.
    [JSInvokable]
    public Task OnViewportChanged()
    {
        if (_menu is null)
        {
            return Task.CompletedTask;
        }

        _menu = null;
        return InvokeAsync(StateHasChanged);
    }

    private async Task ExpandFromMenuAsync(string? relType)
    {
        var nodeId = _menu!.Node.Id;
        _menu = null;
        await ExpandAsync(nodeId, relType);
    }

    private async Task RemoveFromMenuAsync(string nodeId)
    {
        _menu = null;
        await RemoveAsync(nodeId);
    }

    private async Task RemoveOthersAsync(string nodeId)
    {
        _menu = null;
        var keep = _nodes[nodeId];
        _nodes.Clear();
        _expanded.Clear();
        _remaining.Clear();
        _nodes[nodeId] = keep;
        _selected = keep;
        await DrawAsync(null);
    }

    private async Task ExpandAsync(string nodeId, string? relType = null)
    {
        var filter = relType is null ? Filter : Filter with { RelTypes = [relType] };
        // What's on the graph is excluded, so "show more" gets the next batch. Removed nodes aren't on it, so they come back.
        GraphNeighbours result;
        try
        {
            result = await Graph.NeighboursAsync(nodeId, filter, _nodes.Keys, ExpansionLimit);
        }
        catch (LinkbaseValidationException ex)
        {
            // A filter that came from a link can name a value the field can't hold.
            Snackbar.Add(ex.Message, Severity.Warning);
            return;
        }

        foreach (var node in result.Nodes)
        {
            _nodes.TryAdd(node.Id, node);
        }

        _expanded.Add(nodeId);
        _remaining[nodeId] = result.Remaining;
        if (result.Nodes.Count == 0)
        {
            Snackbar.Add("Nothing new to add with the current filters.", Severity.Info);
        }

        await RefreshEdgesAndDrawAsync(nodeId);
        await SelectOnCanvasAsync(nodeId);
        await InvokeAsync(StateHasChanged);
    }

    private async Task RefreshEdgesAndDrawAsync(string? anchorId)
    {
        var subgraph = await Graph.SubgraphAsync(_nodes.Keys.ToList());
        _edges = subgraph.Edges;
        await DrawAsync(anchorId);
    }

    // Positions is set only when restoring a view saved with its layout pinned: the canvas then places nodes exactly where
    // they were, instead of laying them out fresh.
    private async Task DrawAsync(string? anchorId, IReadOnlyList<ScenePosition>? positions = null)
    {
        SyncAddress();
        await RefreshAsOfAsync();
        if (_module is not null)
        {
            var (nodes, edges) = CanvasElements();
            if (positions is not null)
            {
                await _module.InvokeVoidAsync("restore", nodes, edges, positions);
            }
            else
            {
                await _module.InvokeVoidAsync("update", nodes, edges, anchorId);
            }
        }
    }

    private (object[] Nodes, object[] Edges) CanvasElements()
    {
        var visible = VisibleNodes.Select(n => n.Id).ToHashSet();
        var nodes = VisibleNodes.Select(n => (object)new { id = n.Id, label = n.Label, kind = n.Property("kind"), caption = GraphStyle.Caption(n) }).ToArray();
        var edges = _edges
            .Where(e => visible.Contains(e.SourceId) && visible.Contains(e.TargetId) && !_asOfView.MissingEdges.Contains(e.Key))
            .Select(e => (object)new { id = e.Key, source = e.SourceId, target = e.TargetId, relType = EdgeCaption(e) })
            .ToArray();
        return (nodes, edges);
    }

    private (string From, string Rel, string To) ModelKey(GraphEdge edge) =>
        (edge.SourceId[GraphNode.ModelPrefix.Length..], edge.RelType, edge.TargetId[GraphNode.ModelPrefix.Length..]);

    // The attribute names a kind of link carries, in the model view.
    private IReadOnlyList<string> ModelAttributes(GraphEdge edge) =>
        _showingModel && _modelLinks.TryGetValue(ModelKey(edge), out var link) ? link.Attributes : [];

    // What is drawn on a link. The values are optional, because they can crowd a busy graph.
    private string EdgeCaption(GraphEdge edge)
    {
        if (_showingModel)
        {
            var names = ModelAttributes(edge);
            return _linkValues && names.Count > 0 ? $"{edge.Label} · {string.Join(", ", names)}" : edge.Label;
        }

        return _linkValues ? edge.Label : edge.Caption ?? edge.RelType;
    }

    private async Task ToggleLinkValuesAsync(bool show)
    {
        _linkValues = show;
        await DrawAsync(null);
    }

    public string FirstEdgeKeyForTest() => _edges.First().Key;

    private string EndName(string nodeId) => _nodes.TryGetValue(nodeId, out var node) ? node.Name : nodeId;

    private async Task SelectEndAsync(string nodeId)
    {
        _selected = _nodes.GetValueOrDefault(nodeId);
        await SelectOnCanvasAsync(nodeId);
    }

    [JSInvokable]
    public Task OnEdgeClicked(string edgeKey)
    {
        _menu = null;
        var edge = _edges.FirstOrDefault(e => e.Key == edgeKey);
        _selected = null;
        _selectedEdge = edge;
        return InvokeAsync(StateHasChanged);
    }

    private async Task SelectOnCanvasAsync(string? nodeId)
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("select", nodeId);
        }
    }

    // Takes a node off the graph and forgets it, so expanding any of its neighbours brings it back.
    private async Task RemoveAsync(string nodeId)
    {
        _nodes.Remove(nodeId);
        _expanded.Remove(nodeId);
        _remaining.Remove(nodeId);
        _selected = null;
        await DrawAsync(null);
    }

    // "How is this connected to...?": picks the other end in a dialog and draws the shortest path between them.
    private async Task FindPathAsync()
    {
        var parameters = new DialogParameters<PathDialog> { { d => d.Start, _selected is { IsModel: false } start ? start : null } };
        var dialog = await Dialogs.ShowAsync<PathDialog>("How are they connected?", parameters, new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Small, FullWidth = true });
        if (await dialog.Result is { Canceled: false, Data: Linkbase.Core.Paths.PathResult path })
        {
            await ShowPathAsync(path);
        }
    }

    // Adds the path's records, and every link between records on the canvas. Records already there stay where they are.
    private async Task ShowPathAsync(Linkbase.Core.Paths.PathResult path)
    {
        if (_showingModel)
        {
            ForgetCanvas();
        }

        var found = await Graph.SubgraphAsync(path.NodeIds);
        foreach (var node in found.Nodes)
        {
            _nodes.TryAdd(node.Id, node);
        }

        _selected = _nodes.GetValueOrDefault(path.NodeIds[0]);
        await RefreshEdgesAndDrawAsync(anchorId: null);
        await SelectOnCanvasAsync(path.NodeIds[0]);
        await InvokeAsync(StateHasChanged);
    }

    private async Task ShowNearbyChangesAsync()
    {
        if (_selected is not { IsModel: false } node)
        {
            return;
        }

        var parameters = new DialogParameters<NearbyChangesDialog> { { d => d.Node, node } };
        await Dialogs.ShowAsync<NearbyChangesDialog>($"What changed near {node.Name}", parameters, new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Medium, FullWidth = true });
    }

    private async Task FitAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("fit");
        }
    }

    private async Task ClearAsync()
    {
        ForgetCanvas();
        await DrawAsync(null);
    }

}
