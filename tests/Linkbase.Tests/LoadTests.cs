using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Linkbase.Core.Graph;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

namespace Linkbase.Tests;

// Skipped unless LINKBASE_LOAD_MODELS names at least one database. See "Several users at once" in docs/performance.md.
public sealed class LoadFactAttribute : FactAttribute
{
    public LoadFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(LoadTests.ModelsSetting))
        {
            Skip = "Set LINKBASE_LOAD_MODELS, or run scripts/load-test.ps1. See docs/performance.md.";
        }
    }
}

// Several people using one model at once. Each simulated user loops over the queries the Explore page runs through the
// real GraphRepository, with a pause between steps, against the same connection pool the app would use. It reads only.
// What it picks to search for and expand comes from the model itself, so it works on any of them.
[Trait("Category", "Performance")]
public class LoadTests(ITestOutputHelper output)
{
    // Name=connection, separated by '|': "tiny=Host=...;Database=a|large=Host=...;Database=b".
    public static readonly string? ModelsSetting = Environment.GetEnvironmentVariable("LINKBASE_LOAD_MODELS");

    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);
    private const int CanvasLimit = 100;

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private sealed class Factory(string connection) : IDbContextFactory<LinkbaseDbContext>
    {
        // Same as the app's ModelCatalog.ConnectionString: JIT off. Npgsql pools by connection string, so every simulated user
        // shares one pool, as the circuits of a real server do.
        private readonly DbContextOptions<LinkbaseDbContext> _options = new DbContextOptionsBuilder<LinkbaseDbContext>()
            .UseNpgsql(Prepared(connection)).Options;

        public LinkbaseDbContext CreateDbContext() => new(_options);

        private static string Prepared(string connection)
        {
            var builder = new NpgsqlConnectionStringBuilder(connection);
            builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? "-c jit=off" : $"{builder.Options} -c jit=off";
            return builder.ConnectionString;
        }
    }

    private sealed record Op(string Name, int Weight, Func<Random, CancellationToken, Task> Run);

    private sealed record Sample(string Name, bool Ok, double Ms);

    // What one model offers to search for, found once before the users start.
    private sealed class Material
    {
        public required GraphSchema Schema { get; init; }
        public required IReadOnlyDictionary<string, int> Counts { get; init; }
        public required List<GraphNode> Nodes { get; init; }
        public required List<string> Terms { get; init; }
        public required List<string> Types { get; init; }
    }

    private static async Task<Material> DiscoverAsync(GraphRepository graph)
    {
        var schema = await graph.SchemaAsync();
        var counts = await graph.NodeCountsAsync();

        // The biggest types hold most of the traffic, so sample from them.
        var types = counts.Where(c => c.Value > 0 && !c.Key.StartsWith(GraphNode.ModelPrefix, StringComparison.Ordinal))
            .OrderByDescending(c => c.Value).Select(c => c.Key).Take(4).ToList();
        var nodes = new List<GraphNode>();
        foreach (var type in types)
        {
            var found = await graph.RunPatternAsync(new GraphPattern([new PatternNode(type, [])], []), schema, CanvasLimit, 0);
            nodes.AddRange((await graph.SubgraphAsync(found.NodeIds)).Nodes);
        }

        var terms = nodes.SelectMany(n => n.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length >= 4 && w.All(char.IsLetter)).Distinct().Take(300).ToList();
        if (nodes.Count == 0 || terms.Count == 0)
        {
            throw new InvalidOperationException("The model has too few named records to load test.");
        }

        return new Material { Schema = schema, Counts = counts, Nodes = nodes, Terms = terms, Types = types };
    }

    private static List<Op> Operations(GraphRepository graph, Material m)
    {
        static T Pick<T>(List<T> list, Random random) => list[random.Next(list.Count)];
        // Someone follows a link from a record they found by name, never from every record of a type.
        var connection = m.Schema.Connections.FirstOrDefault(c => m.Counts.ContainsKey(c.TargetLabel) && m.Nodes.Any(n => n.Label == c.SourceLabel));
        var starts = connection is null ? [] : m.Nodes.Where(n => n.Label == connection.SourceLabel).ToList();
        var property = m.Schema.Properties.FirstOrDefault(p => p.Label == m.Types[0] && p.Type == GraphValueType.Text);

        var ops = new List<Op>
        {
            new("Open the page: schema and counts", 1, async (_, t) =>
            {
                await graph.SchemaAsync(t);
                await graph.NodeCountsAsync(t);
            }),
            // A person typing three letters: each keystroke asks again, and the last answer is the one that matters.
            new("Keystroke: suggestions", 4, async (r, t) => await graph.MatchPropertiesAsync(null, Pick(m.Terms, r)[..3], t)),
            new("Search: first 100 of a type", 2, async (r, t) =>
                await graph.RunPatternAsync(new GraphPattern([new PatternNode(Pick(m.Types, r), [])], []), m.Schema, CanvasLimit, 0, t)),
            new("Search: name contains, within a type", 2, async (r, t) =>
                await graph.RunPatternAsync(new GraphPattern([new PatternNode(Pick(m.Nodes, r).Label, [new PatternFilter("name", FilterOperator.Contains, Pick(m.Terms, r))])], []), m.Schema, CanvasLimit, 0, t)),
            new("Search: free text, everything", 2, async (r, t) =>
                await graph.RunPatternAsync(new GraphPattern([new PatternNode(null, [new PatternFilter(PatternFilter.AnyField, FilterOperator.Contains, Pick(m.Terms, r))])], []), m.Schema, CanvasLimit, 0, t)),
            new("Expand: a record's neighbours", 4, async (r, t) => await graph.NeighboursAsync(Pick(m.Nodes, r).Id, new GraphFilter(), [], CanvasLimit, t)),
            new("Right-click menu: counts", 1, async (r, t) => await graph.RelationshipCountsAsync(Pick(m.Nodes, r).Id, t)),
            new("Draw: nodes and links for 100 nodes", 2, async (r, t) =>
                await graph.SubgraphAsync([.. Enumerable.Range(0, Math.Min(CanvasLimit, m.Nodes.Count)).Select(_ => Pick(m.Nodes, r).Id).Distinct()], t)),
        };

        if (property is not null)
        {
            ops.Add(new("Keystroke: values of a property", 1, async (r, t) =>
                await graph.PropertyValuesAsync(property.Label, property.Key, Pick(m.Terms, r)[..1], cancellationToken: t)));
        }

        if (connection is not null)
        {
            ops.Add(new("Search: following a relationship from a named record", 1, async (r, t) =>
                await graph.RunPatternAsync(
                    new GraphPattern(
                        [new PatternNode(connection.SourceLabel, [new PatternFilter("name", FilterOperator.Equals, Pick(starts, r).Name)]), new PatternNode(connection.TargetLabel, [])],
                        [new PatternRel(connection.RelType, true)]),
                    m.Schema, CanvasLimit, 0, t)));
        }

        return ops;
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private static string Ms(double value) => value >= 10_000 ? $"{value / 1000:0.0} s" : string.Create(CultureInfo.InvariantCulture, $"{value:0} ms");

    // One level: this many users, each looping until the time is up.
    private static async Task<(List<Sample> Samples, double Seconds, string? FirstError)> RunLevelAsync(
        List<Op> ops, int users, TimeSpan duration, int thinkMs, int seed)
    {
        var samples = new ConcurrentBag<Sample>();
        string? firstError = null;
        var total = ops.Sum(o => o.Weight);
        var clock = Stopwatch.StartNew();
        var until = duration;

        async Task User(int index)
        {
            var random = new Random(seed + index);
            // People don't all arrive in the same millisecond.
            await Task.Delay(random.Next(0, Math.Max(1, thinkMs * 2)));
            var step = 0;
            while (clock.Elapsed < until)
            {
                var op = step++ == 0 ? ops[0] : Choose(ops, random.Next(total));
                using var cancel = new CancellationTokenSource(GiveUpAfter);
                var watch = Stopwatch.StartNew();
                var ok = true;
                try
                {
                    await op.Run(random, cancel.Token);
                }
                catch (Exception e)
                {
                    ok = false;
                    Interlocked.CompareExchange(ref firstError, $"{op.Name}: {(e is OperationCanceledException ? $"gave up after {GiveUpAfter.TotalSeconds:0} s" : e.GetBaseException().Message)}", null);
                }

                samples.Add(new Sample(op.Name, ok, watch.Elapsed.TotalMilliseconds));
                await Task.Delay(random.Next(thinkMs / 2, thinkMs * 3 / 2 + 1));
            }
        }

        await Task.WhenAll(Enumerable.Range(0, users).Select(User));
        return ([.. samples], clock.Elapsed.TotalSeconds, firstError);
    }

    private static Op Choose(List<Op> ops, int ticket)
    {
        foreach (var op in ops)
        {
            ticket -= op.Weight;
            if (ticket < 0)
            {
                return op;
            }
        }

        return ops[^1];
    }

    [LoadFact]
    public async Task Several_users_can_use_each_model_at_once()
    {
        var levels = (Environment.GetEnvironmentVariable("LINKBASE_LOAD_USERS") ?? "1,5,10,25")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
        var duration = TimeSpan.FromSeconds(Setting("LINKBASE_LOAD_SECONDS", 15));
        var think = Setting("LINKBASE_LOAD_THINK_MS", 200);

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Each level runs {duration.TotalSeconds:0} s. Each user pauses about {think} ms between steps. Reads only.");
        var failures = new List<string>();

        foreach (var entry in ModelsSetting!.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = entry.IndexOf('=', StringComparison.Ordinal);
            var name = entry[..split];
            var factory = new Factory(entry[(split + 1)..]);
            var graph = new GraphRepository(factory);

            var material = await DiscoverAsync(graph);
            var ops = Operations(graph, material);
            var records = material.Counts.Sum(c => c.Value);

            // One pass first, so every level measures a warm cache, as the single-user table in the doc does.
            await RunLevelAsync(ops, 1, TimeSpan.FromSeconds(Math.Min(10, duration.TotalSeconds)), 0, 1);

            report.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"### {name}: {records:N0} records").AppendLine();
            report.AppendLine("| Users | Requests | Failed | Per second | Median | 95th | 99th | Slowest |").AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            List<Sample> last = [];
            foreach (var users in levels)
            {
                var (samples, seconds, error) = await RunLevelAsync(ops, users, duration, think, users * 1000);
                var times = samples.Where(s => s.Ok).Select(s => s.Ms).Order().ToList();
                var failed = samples.Count(s => !s.Ok);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {users} | {samples.Count} | {failed} | {samples.Count / seconds:0.0} | {Ms(Percentile(times, .5))} | {Ms(Percentile(times, .95))} | {Ms(Percentile(times, .99))} | {Ms(times.LastOrDefault())} |");
                if (error is not null)
                {
                    failures.Add($"{name}, {users} users: {failed} failed. First: {error}");
                }

                last = samples;
            }

            report.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"At {levels[^1]} users, by step:").AppendLine();
            report.AppendLine("| Step | Count | Median | 95th | Slowest |").AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var group in last.Where(s => s.Ok).GroupBy(s => s.Name).OrderByDescending(g => Percentile([.. g.Select(s => s.Ms).Order()], .95)))
            {
                var times = group.Select(s => s.Ms).Order().ToList();
                report.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {times.Count} | {Ms(Percentile(times, .5))} | {Ms(Percentile(times, .95))} | {Ms(times[^1])} |");
            }

            NpgsqlConnection.ClearAllPools();
        }

        var text = report.ToString();
        output.WriteLine(text);
        if (Environment.GetEnvironmentVariable("LINKBASE_LOAD_REPORT") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, text);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
