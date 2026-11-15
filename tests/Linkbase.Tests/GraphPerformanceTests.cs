using System.Diagnostics;
using System.Globalization;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// Skipped unless a large test database is named. See docs/performance.md.
public sealed class PerformanceFactAttribute : FactAttribute
{
    public PerformanceFactAttribute()
    {
        if (string.IsNullOrEmpty(GraphPerformanceTests.Connection))
        {
            Skip = "Set LINKBASE_PERF_CONNECTION to the throwaway database from the large-sample steps in docs/performance.md.";
        }
    }
}

[Trait("Category", "Performance")]
public class GraphPerformanceTests
{
    public static readonly string? Connection = Environment.GetEnvironmentVariable("LINKBASE_PERF_CONNECTION");
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(120);
    private const int Runs = 3;
    private const int CanvasLimit = 100;

    // Everything the queries need to know if the real schema query is too slow to use.
    private static readonly GraphSchema Fallback = new(
        [new GraphConnection("Service", "CALLS_AT", "Stop"), new GraphConnection("Service", "OF", "Line"), new GraphConnection("Line", "RUN_BY", "Agency")],
        [
            new GraphPropertyInfo("Service", "name", GraphValueType.Text),
            new GraphPropertyInfo("Stop", "name", GraphValueType.Text),
            new GraphPropertyInfo("Stop", "platform", GraphValueType.Text),
            new GraphPropertyInfo("Stop", "elevation", GraphValueType.Number),
            new GraphPropertyInfo("Line", "name", GraphValueType.Text),
            new GraphPropertyInfo("Agency", "name", GraphValueType.Text),
        ]);

    private sealed class Factory : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(Connection).Options);
    }

    private sealed record StopRow(string NodeId);

    private static async Task<string> MeasureAsync(string what, Func<CancellationToken, Task<int>> run)
    {
        var times = new List<double>();
        var rows = 0;
        for (var i = 0; i < Runs; i++)
        {
            using var cancel = new CancellationTokenSource(GiveUpAfter);
            var watch = Stopwatch.StartNew();
            try
            {
                rows = await run(cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return $"| {what} | over {GiveUpAfter.TotalSeconds:0} s | |";
            }

            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        // The first run shows a cold cache. The median is what someone sees once they're working.
        var median = times.Order().ElementAt(Runs / 2);
        return string.Create(CultureInfo.InvariantCulture, $"| {what} | {median:0} ms | {times[0]:0} ms | {rows} |");
    }

    [PerformanceFact]
    public async Task Measure_every_query_the_explore_page_runs()
    {
        var factory = new Factory();
        var graph = new GraphRepository(factory, Linkbase.Domains.RegisteredDomains.Registry, Linkbase.Core.Security.FieldPolicy.Unrestricted);
        var lines = new List<string> { "| What | Median | First run | Rows |", "| --- | --- | --- | --- |" };

        var schema = Fallback;
        lines.Add(await MeasureAsync("Open the page: schema", async token =>
        {
            schema = await graph.SchemaAsync(token);
            return schema.Properties.Count;
        }));
        lines.Add(await MeasureAsync("Graph model: node counts", async token => (await graph.NodeCountsAsync(token)).Count));

        // The busiest of the stops with this name.
        await using var db = factory.CreateDbContext();
        var stops = await db.Database.SqlQuery<StopRow>($"""SELECT id AS "NodeId" FROM node WHERE type = 'Stop' AND name = 'Zürich HB'""").ToListAsync();
        var busiest = stops[0].NodeId;
        var most = -1;
        foreach (var stop in stops)
        {
            var links = await db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM node_link WHERE target_id = {stop.NodeId}""").SingleAsync();
            if (links > most)
            {
                (busiest, most) = (stop.NodeId, links);
            }
        }

        lines.Add($"| *Busiest stop: {busiest}, {most} links* | | | |");

        Task<int> Pattern(GraphPattern pattern, CancellationToken token) =>
            graph.RunPatternAsync(pattern, schema, CanvasLimit, 0, token).ContinueWith(r => r.Result.NodeIds.Count, token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

        var filter = (string key, FilterOperator op, string value) => new PatternFilter(key, op, value);
        lines.Add(await MeasureAsync("Keystroke: suggestions for \"Zür\"", async token => (await graph.MatchPropertiesAsync(null, "Zür", token)).Count));
        lines.Add(await MeasureAsync("Keystroke: suggestions for just \"Zü\"", async token => (await graph.MatchPropertiesAsync(null, "Zü", token)).Count));
        lines.Add(await MeasureAsync("Keystroke: values of a property", async token => (await graph.PropertyValuesAsync("Stop", "platform", "1", cancellationToken: token)).Count));
        lines.Add(await MeasureAsync("Search: first 100 of a type", token => Pattern(new GraphPattern([new PatternNode("Line", [])], []), token)));
        lines.Add(await MeasureAsync("Search: name contains, within a type", token => Pattern(new GraphPattern([new PatternNode("Stop", [filter("name", FilterOperator.Contains, "Zürich")])], []), token)));
        lines.Add(await MeasureAsync("Search: free text, everything", token => Pattern(new GraphPattern([new PatternNode(null, [filter(PatternFilter.AnyField, FilterOperator.Contains, "Zürich")])], []), token)));
        lines.Add(await MeasureAsync("Search: following two relationships", token => Pattern(
            new GraphPattern(
                [new PatternNode("Stop", [filter("name", FilterOperator.Equals, "Bern")]), new PatternNode("Service", []), new PatternNode("Line", [])],
                [new PatternRel("CALLS_AT", false), new PatternRel("OF", true)]),
            token)));
        lines.Add(await MeasureAsync("Expand: busiest stop, first 100", async token => (await graph.NeighboursAsync(busiest, new GraphFilter(), [], CanvasLimit, token)).Nodes.Count));
        lines.Add(await MeasureAsync("Right-click menu: busiest stop's counts", async token => (await graph.RelationshipCountsAsync(busiest, token)).Count));

        var shown = (await graph.NeighboursAsync(busiest, new GraphFilter(), [], CanvasLimit)).Nodes.Select(n => n.Id).Append(busiest).ToList();
        lines.Add(await MeasureAsync("Draw: nodes and links for 100 nodes", async token => (await graph.SubgraphAsync(shown, token)).Edges.Count));

        var report = string.Join(Environment.NewLine, lines);
        var path = Environment.GetEnvironmentVariable("LINKBASE_PERF_REPORT");
        if (!string.IsNullOrEmpty(path))
        {
            await File.WriteAllTextAsync(path, report);
        }

        Assert.DoesNotContain(lines, line => line.Contains("| over ", StringComparison.Ordinal));
    }
}
