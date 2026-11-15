using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Linkbase.Core.Graph;
using Linkbase.Core.Views;
using Linkbase.Data;

namespace Linkbase.Core.Explore;

// Permalinks and saved views. The page's address always carries its state in the v parameter (see ViewState), so copying the
// address, pressing Back or opening a saved view all come to the same thing: the page is made to match its address.
public partial class ExplorePage
{
    // The state of the address, as the page last made it or applied it. Null is a plain /explore.
    [Parameter, SupplyParameterFromQuery(Name = "v")] public string? View { get; set; }

    private string? _addressed;

    // Addresses the page has asked the browser to go to and has not yet heard back about, oldest first. The browser reports
    // each one as a parameter change, which must not be mistaken for someone else changing the address.
    private readonly List<string?> _sent = [];

    // Set once the graph libraries and the schema are there, which an address can't be applied before.
    private bool _ready;
    private bool _restoring;

    private ExploreState CurrentState()
    {
        var onCanvas = _nodes.Count > 0 && !_showingModel;
        return new ExploreState
        {
            Pattern = onCanvas ? _lastPattern : null,
            Nodes = onCanvas && _nodes.Count <= ExploreState.MaxNodes ? _nodes.Keys.ToList() : null,
            Model = _showingModel,
            Table = _table && _nodes.Count > 0,
            Cards = _cards && _nodes.Count > 0,
            Totals = _summaryOpen && onCanvas && _lastPattern is not null,
            RelTypes = _relTypes.Count > 0 ? _relTypes.ToList() : null,
            Filters = _propertyFilters.Count > 0 ? _propertyFilters.ToList() : null,
        };
    }

    // Puts the page's state in its address, replacing the entry, so Back leaves the page rather than stepping through every click.
    private void SyncAddress()
    {
        if (_restoring || !_ready)
        {
            return;
        }

        var encoded = CurrentState().Encode();
        if (encoded == _addressed)
        {
            return;
        }

        _addressed = encoded;
        _sent.Add(encoded);
        Navigation.NavigateTo(encoded is null ? "/explore" : $"/explore?v={encoded}", replace: true);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_ready || (View == _addressed && _sent.Count == 0))
        {
            return;
        }

        var index = _sent.IndexOf(View);
        if (index >= 0)
        {
            // Our own change coming back.
            _sent.RemoveRange(0, index + 1);
            return;
        }

        _sent.Clear();
        await ApplyAddressAsync(View);
    }

    // Makes the canvas match an address someone else set: a link, a saved view, or the Back button.
    private Task ApplyAddressAsync(string? address) => WithBusyAsync(() => ApplyAddressCoreAsync(address));

    private async Task ApplyAddressCoreAsync(string? address)
    {
        _addressed = address;
        _restoring = true;
        try
        {
            var state = ViewState.Decode<ExploreState>(address);
            if (address is not null && state is null)
            {
                Snackbar.Add("That link can't be read, so the page is empty.", Severity.Warning);
            }

            await RestoreAsync(state ?? new ExploreState());
        }
        finally
        {
            _restoring = false;
        }
    }

    private async Task RestoreAsync(ExploreState state)
    {
        ForgetCanvas();
        _lastPattern = null;
        _view = ExploreView.Graph;

        // Everything here came from an address, so keep only what the model knows. The queries check again.
        _relTypes = (state.RelTypes ?? []).Where(r => r is not null && _schema!.RelTypes.Contains(r)).ToList();
        _propertyFilters.Clear();
        _propertyFilters.AddRange((state.Filters ?? [])
            .Where(f => f is { Key: not null, Value: not null } && _schema!.Properties.Any(p => p.Key == f.Key && p.Key != "name")));
        ChooseFilterKey(null);

        var pattern = CleanPattern(state.Pattern);
        if (state.Model)
        {
            await ShowModelAsync();
            return;
        }

        _summaryOpen = state.Totals && pattern is not null;
        var ids = (state.Nodes ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct().Take(ExploreState.MaxNodes).ToList();
        if (ids.Count > 0)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var subgraph = await Graph.SubgraphAsync(ids);
            var queryMs = clock.ElapsedMilliseconds;
            foreach (var node in subgraph.Nodes)
            {
                _nodes[node.Id] = node;
            }

            _edges = subgraph.Edges;
            _lastPattern = pattern;
            await DrawAsync(null, state.Positions);
            var drawMs = clock.ElapsedMilliseconds - queryMs;
            await LoadSummaryAsync();
            await LoadRankingAsync();
            Log.LogInformation("Explore restored {Nodes} nodes and {Edges} links: query {Query} ms, draw and layout {Draw} ms{Pinned}.",
                subgraph.Nodes.Count, subgraph.Edges.Count, queryMs, drawMs, state.Positions is null ? "" : " (positions pinned)");
            if (subgraph.Nodes.Count < ids.Count)
            {
                Snackbar.Add($"{ids.Count - subgraph.Nodes.Count} of the records in this view no longer exist.", Severity.Info);
            }
        }
        else if (pattern is not null)
        {
            await RunPatternAsync(pattern);
        }
        else
        {
            await DrawAsync(null);
        }

        _view = _nodes.Count == 0 ? ExploreView.Graph : state.Table ? ExploreView.Table : state.Cards ? ExploreView.Cards : ExploreView.Graph;
    }

    private static GraphPattern? CleanPattern(GraphPattern? pattern)
    {
        if (pattern?.Nodes is not { Count: > 0 } nodes || pattern.Rels is null || pattern.Rels.Count != nodes.Count - 1
            || nodes.Any(n => n is null) || pattern.Rels.Any(r => r?.RelType is null))
        {
            return null;
        }

        // A ranking that doesn't fit the chain is dropped, leaving the plain search.
        var rank = pattern.Rank is { } r && r.Group >= 0 && r.Group < nodes.Count && r.Count >= 0 && r.Count < nodes.Count && r.Group != r.Count
            ? r with { Top = Math.Clamp(r.Top, 1, PatternRank.MaxTop), Measure = Enum.IsDefined(r.Measure) ? r.Measure : RankMeasure.Distinct, Key = r.NeedsKey && Enum.IsDefined(r.Measure) ? r.Key : null }
            : null;
        return new GraphPattern(
            nodes.Select(n => new PatternNode(n.Label, (n.Filters ?? []).Where(f => f is { Key: not null, Value: not null }).ToList())).ToList(),
            pattern.Rels)
        { Rank = rank };
    }

    private async Task OpenViewsAsync()
    {
        var parameters = new DialogParameters<SavedViewsDialog>
        {
            { d => d.State, CurrentState().Encode() },
            { d => d.Kind, SavedViewKind.Explore },
            { d => d.CanPinLayout, _nodes.Count > 0 && !_showingModel },
            { d => d.PinnedState, PinnedStateAsync },
        };
        await Dialogs.ShowAsync<SavedViewsDialog>("Saved views", parameters, new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true });
    }

    // The current state with the canvas's exact node positions pinned, for a view that should reopen with its hand-arranged
    // layout rather than a fresh one. What used to be a separate "scene" is now this: a saved view with its layout pinned.
    private async Task<string?> PinnedStateAsync()
    {
        if (_module is null || _nodes.Count == 0 || _showingModel)
        {
            return CurrentState().Encode();
        }

        var positions = await _module.InvokeAsync<ScenePosition[]>("positions");
        return (CurrentState() with { Positions = positions }).Encode();
    }

    private async Task CopyLinkAsync()
    {
        if (CurrentState().Encode() is not { } state)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", Navigation.ToAbsoluteUri(ViewState.Href(SavedViewKind.Explore, null, state)).ToString());
            Snackbar.Add("Link copied.", Severity.Success);
        }
        catch (JSException)
        {
            // Browsers only allow this on a secure page, so say where to find the link instead.
            Snackbar.Add("The browser wouldn't copy it. The link is in the address bar.", Severity.Info);
        }
    }
}
