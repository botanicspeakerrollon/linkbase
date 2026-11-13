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
    [JSInvokable]
    public async Task OnHotkey(string key)
    {
        switch (key)
        {
            case "t" when _nodes.Count > 0:
                await ShowViewAsync(!_table);
                break;
            case "f":
                await ShowViewAsync(false);
                await FitAsync();
                break;
            case "c" when _nodes.Count > 0:
                await ShowViewAsync(false);
                await ClearAsync();
                break;
            case "n":
                await StepAsync(1);
                break;
            case "p":
                await StepAsync(-1);
                break;
            case "e" when _selected is { IsModel: true } model:
                await ShowTypeAsync(model.Label);
                break;
            case "e" when _selected is { } node:
                await ExpandAsync(node.Id);
                break;
            case "s" when _lastPattern is not null && _nodes.Count > 0:
                await ToggleSummaryAsync();
                break;
            case "v":
                await OpenViewsAsync();
                break;
            case "w" when _selected is { IsModel: false }:
                await FindPathAsync();
                break;
            case "d" when _selected is { IsModel: false }:
                await ShowNearbyChangesAsync();
                break;
            case "Delete" when _selected is { IsModel: false } node:
                await RemoveAsync(node.Id);
                break;
            case "Escape" when _menu is not null:
                _menu = null;
                break;
            case "Escape":
                _selected = null;
                await SelectOnCanvasAsync(null);
                break;
            default:
                return;
        }

        await InvokeAsync(StateHasChanged);
    }

    // Selects the next or previous node, in the table's order: by type as the schema lists them, then by name. It wraps
    // round, and with nothing selected starts at either end.
    private async Task StepAsync(int direction)
    {
        var order = _schema?.Labels.Select((label, i) => (label, i)).ToDictionary(x => x.label, x => x.i) ?? [];
        var nodes = VisibleNodes
            .OrderBy(n => order.GetValueOrDefault(n.Label, int.MaxValue))
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        if (nodes.Count == 0)
        {
            return;
        }

        var current = _selected is null ? -1 : nodes.FindIndex(n => n.Id == _selected.Id);
        var next = current < 0
            ? (direction > 0 ? 0 : nodes.Count - 1)
            : (current + direction + nodes.Count) % nodes.Count;
        _selected = nodes[next];
        _menu = null;
        await SelectOnCanvasAsync(_selected.Id);
        if (_table)
        {
            _revealRow = true;
        }
        else if (_module is not null)
        {
            await _module.InvokeVoidAsync("reveal", _selected.Id);
        }
    }

    private async Task ShowViewAsync(bool table)
    {
        _menu = null;
        if (_table == table)
        {
            return;
        }

        _table = table;
        SyncAddress();
        // Nodes may have been added or removed while the table covered the canvas.
        if (!table)
        {
            await FitAsync();
        }
    }

    private async Task SelectFromTableAsync(GraphNode node)
    {
        _selected = node;
        _menu = null;
        await SelectOnCanvasAsync(node.Id);
    }
}
