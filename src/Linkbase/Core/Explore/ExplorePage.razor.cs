using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using Linkbase.Data;
using Linkbase.Core.Graph;
using FilterOperator = Linkbase.Core.Graph.FilterOperator;

namespace Linkbase.Core.Explore;

public partial class ExplorePage
{
    private const int ExpansionLimit = 100;

    private ElementReference _canvas;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ExplorePage>? _self;
    private GraphSchema? _schema;
    private GraphPattern? _lastPattern;
    private int _patternOffset;
    private int _patternShown;
    private int _patternRemaining;
    private ContextMenu? _menu;
    private string? _loadError;
    private bool _table;
    private IJSObjectReference? _hotkeys;
    private int _hotkeyHandle;
    private IJSObjectReference? _download;

    // The keys this page handles. The layout handles the ones that work everywhere.
    private static readonly string[] Hotkeys = ["t", "f", "c", "n", "p", "e", "Delete", "Escape"];

    // Set when the keyboard picks a node in the table, so its row is scrolled to once it's drawn.
    private bool _revealRow;

    // The visible graph lives here. The JavaScript module only draws it and reports clicks.
    private readonly Dictionary<string, GraphNode> _nodes = [];
    private sealed record ContextMenu(GraphNode Node, double X, double Y, IReadOnlyList<RelationshipCount> Counts);

    private readonly HashSet<string> _expanded = [];
    private readonly Dictionary<string, int> _remaining = [];
    private IReadOnlyList<GraphEdge> _edges = [];
    private GraphNode? _selected;
    private bool _showingModel;

    private IReadOnlyCollection<string> _relTypes = [];
    private readonly List<PatternFilter> _propertyFilters = [];
    private string? _filterKey;
    private FilterOperator _filterOperator;
    private string? _filterValue;

    private IReadOnlyList<string> _scenes = [];
    private string? _sceneName;

    private IEnumerable<GraphNode> VisibleNodes => _nodes.Values;

    private GraphFilter Filter => new()
    {
        RelTypes = _relTypes.ToList(),
        Properties = _propertyFilters.ToList(),
    };

    private GraphValueType FilterKeyType =>
        _schema!.Properties.First(p => p.Key == _filterKey).Type;

    private void ChooseFilterKey(string? key)
    {
        _filterKey = key;
        _filterValue = null;
        _filterOperator = key is null ? default : PatternBuilder.OperatorsFor(FilterKeyType).First();
    }

    private void AddFilter()
    {
        _propertyFilters.Add(new PatternFilter(_filterKey!, _filterOperator, _filterValue!.Trim()));
        ChooseFilterKey(null);
    }

    private void RemoveFilter(MudChip<string> chip) => _propertyFilters.RemoveAll(f => FilterText(f) == chip.Value);

    private static string FilterText(PatternFilter filter) =>
        $"{filter.Key} {PatternBuilder.OperatorText(filter.Operator)} {filter.Value}";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (_revealRow && _hotkeys is not null)
            {
                _revealRow = false;
                await _hotkeys.InvokeVoidAsync("scrollIntoView", ".explore-table-selected");
            }

            return;
        }

        _scenes = await Graph.SceneNamesAsync();
        _schema = await Graph.SchemaAsync();

        // The search bar can work before the graph libraries finish loading from the CDN.
        StateHasChanged();
        _self = DotNetObjectReference.Create(this);
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/graph.js");
            await _module.InvokeVoidAsync("create", _canvas, _self, _schema.Labels.Select(label => GraphStyle.For(label, _schema)).Concat(_schema.Labels.SelectMany(GraphStyle.KindStylesOf)).ToList());
        }
        catch (JSException ex)
        {
            _loadError = $"The graph can't be drawn: {ex.Message}";
        }

        // Shortcuts are a convenience, so the page works without them.
        try
        {
            _hotkeys = await JS.InvokeAsync<IJSObjectReference>("import", "./js/hotkeys.js");
            _hotkeyHandle = await _hotkeys.InvokeAsync<int>("register", _self, Hotkeys);
        }
        catch (JSException)
        {
            _hotkeys = null;
        }

        StateHasChanged();
    }

    // Replaces the canvas with the model itself: one node per type and one link per kind of relationship.
    private async Task ShowModelAsync()
    {
        var schema = await Graph.SchemaAsync();
        var counts = await Graph.NodeCountsAsync();
        var linkCounts = (await Graph.ConnectionCountsAsync()).ToDictionary(c => (c.SourceLabel, c.RelType, c.TargetLabel), c => c.Links);
        long LinksOf(GraphConnection c) => linkCounts.GetValueOrDefault((c.SourceLabel, c.RelType, c.TargetLabel));
        ForgetCanvas();
        foreach (var label in schema.Labels)
        {
            var fields = schema.Properties.Where(p => p.Label == label).Select(p => GraphStyle.PropertyTitle(p.Key).ToLowerInvariant()).Distinct();
            var links = schema.Connections
                .Where(c => c.SourceLabel == label || c.TargetLabel == label)
                .Select(c => (c.SourceLabel == label ? $"{PatternBuilder.RelText(c.RelType, true)} {c.TargetLabel}" : $"{PatternBuilder.RelText(c.RelType, false)} {c.SourceLabel}")
                    + $" ({LinksOf(c).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)})");
            var id = GraphNode.ModelPrefix + label;
            _nodes[id] = new GraphNode(id, label, label,
            [
                new GraphProperty("nodes", counts.GetValueOrDefault(label).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)),
                new GraphProperty("properties", string.Join(", ", fields)),
                new GraphProperty("links", string.Join(", ", links)),
            ]);
        }

        _edges = schema.Connections
            .Select(c => new GraphEdge(GraphNode.ModelPrefix + c.SourceLabel, GraphNode.ModelPrefix + c.TargetLabel, c.RelType, $"{c.RelType} ({GraphStyle.Compact(LinksOf(c))})"))
            .ToList();
        _showingModel = true;
        _lastPattern = null;
        await DrawAsync(null);
    }

    private Task ShowTypeAsync(string label) => RunPatternAsync(new GraphPattern([new PatternNode(label, [])], []));

    private void ForgetCanvas()
    {
        _nodes.Clear();
        _expanded.Clear();
        _remaining.Clear();
        _edges = [];
        _selected = null;
        _menu = null;
        _showingModel = false;
    }

    private async Task RunPatternAsync(GraphPattern pattern)
    {
        if (_showingModel)
        {
            ForgetCanvas();
        }

        _lastPattern = pattern;
        _patternOffset = 0;
        _patternShown = 0;
        await AddPatternResultsAsync();
    }

    private Task ShowMorePatternAsync() => AddPatternResultsAsync();

    private async Task AddPatternResultsAsync()
    {
        try
        {
            var result = await Graph.RunPatternAsync(_lastPattern!, _schema!, ExpansionLimit, _patternOffset);
            _patternOffset += ExpansionLimit;
            _patternRemaining = result.Remaining;
            _patternShown = Math.Min(_patternOffset, _patternOffset + result.Remaining);
            if (result.NodeIds.Count == 0)
            {
                Snackbar.Add("Nothing matches that search.", Severity.Info);
                return;
            }

            var found = await Graph.SubgraphAsync(result.NodeIds);
            foreach (var node in found.Nodes)
            {
                _nodes.TryAdd(node.Id, node);
            }

            _selected = found.Nodes.Count == 1 ? found.Nodes[0] : _selected;
            await RefreshEdgesAndDrawAsync(anchorId: null);
            await SelectOnCanvasAsync(_selected?.Id);
        }
        catch (LinkbaseValidationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
        }
    }

    private string ChipStyle(string label) =>
        $"background-color: {GraphStyle.For(label, _schema).Color}; color: #ffffff";

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_hotkeys is not null)
            {
                await _hotkeys.InvokeVoidAsync("unregister", _hotkeyHandle);
                await _hotkeys.DisposeAsync();
            }

            if (_download is not null)
            {
                await _download.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The browser has already gone.
        }

        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("destroy");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The browser has already gone, so there's nothing left to clean up.
            }
        }

        _self?.Dispose();
    }
}
