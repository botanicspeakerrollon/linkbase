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
    private Task ExportNodesAsync() =>
        SaveAsync(ExploreExport.FileName("nodes", "csv", DateTimeOffset.Now), "text/csv", ExploreExport.NodesCsv(VisibleNodes.ToList()));

    private Task ExportLinksAsync() =>
        SaveAsync(ExploreExport.FileName("links", "csv", DateTimeOffset.Now), "text/csv", ExploreExport.LinksCsv(VisibleNodes.ToList(), _edges));

    private Task ExportJsonAsync() =>
        SaveAsync(ExploreExport.FileName("graph", "json", DateTimeOffset.Now), "application/json", ExploreExport.AsJson(VisibleNodes.ToList(), _edges));

    private async Task SaveAsync(string fileName, string contentType, string text)
    {
        _download ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/download.js");
        using var stream = new MemoryStream(ExploreExport.Utf8(text));
        using var reference = new DotNetStreamReference(stream);
        await _download.InvokeVoidAsync("saveStream", fileName, contentType, reference);
    }

    // Cytoscape draws the picture from the whole graph, whether or not the table is covering it.
    private async Task ExportPictureAsync()
    {
        if (_module is not null && !await _module.InvokeAsync<bool>("savePng", ExploreExport.FileName("graph", "png", DateTimeOffset.Now)))
        {
            Snackbar.Add("There's nothing on the canvas to draw.", Severity.Info);
        }
    }
}
