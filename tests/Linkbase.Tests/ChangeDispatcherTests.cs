using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.Hooks;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

// The change hook pattern (INT-1), shown with the hook that writes to standard output: each hook takes events in order after its
// own position, a hook that throws is given them again, and only one dispatcher serves a hook at a time. See
// docs/history/change-hooks-plan.md.
[Trait("Category", "Database")]
public sealed class ChangeDispatcherTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private static readonly string Example = RepoPaths.Resolve("models/consultancy/model.json");
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private sealed class RecordingHook : IChangeHook
    {
        public List<IReadOnlyList<ChangeNotice>> Batches { get; } = [];
        public bool Fail { get; set; }
        public Task? Wait { get; set; }

        public async Task HandleAsync(IReadOnlyList<ChangeNotice> notices, CancellationToken cancellationToken)
        {
            Batches.Add(notices);
            if (Wait is not null)
            {
                await Wait;
            }

            if (Fail)
            {
                throw new InvalidOperationException("The bus is down.");
            }
        }

        public List<long> Ids => Batches.SelectMany(b => b).Select(n => n.Id).ToList();
    }

    private async Task<(ModelFactory Factory, RecordRepository Records)> LoadAsync()
    {
        var model = JsonNode.Parse(File.ReadAllText(Example), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        model["folder"] = Path.GetDirectoryName(Example);
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-dispatch-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, model.ToJsonString());
        _files.Add(path);
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory, Linkbase.Domains.RegisteredDomains.Registry).LoadAsync(path, TextWriter.Null);
        return (factory, new RecordRepository(factory, RecordHooks.None, Linkbase.Core.Security.FieldPolicy.Unrestricted));
    }

    private static ChangeDispatcher Dispatcher(ModelFactory factory, params ConfiguredHook[] hooks) =>
        new(hooks, () => ["m"], _ => factory.CreateDbContext(), NullLogger.Instance);

    private static ConfiguredHook Hook(string key, IChangeHook hook, int batch = 100) => new(key, [], batch, hook);

    private static Task<string> SaveAsync(RecordRepository records, string id) =>
        records.CreateAsync("Person", id, TestModels.Person($"Person {id}", []), new ChangeReason("Hired"));

    [Fact]
    public async Task A_new_hook_starts_at_the_newest_event_then_takes_later_ones_once_and_in_order()
    {
        var (factory, records) = await LoadAsync();
        var hook = new RecordingHook();
        var dispatcher = Dispatcher(factory, Hook("a", hook));

        // The model load's event was there before the hook, so it isn't replayed.
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(default));

        await SaveAsync(records, "z1");
        await SaveAsync(records, "z2");
        Assert.Equal(2, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(default));

        Assert.Equal(2, hook.Ids.Count);
        Assert.True(hook.Ids[0] < hook.Ids[1]);
        Assert.All(hook.Batches.SelectMany(b => b), n => Assert.Equal("m", n.Model));
    }

    [Fact]
    public async Task A_hook_that_throws_is_given_the_same_events_again_and_does_not_hold_up_another()
    {
        var (factory, records) = await LoadAsync();
        var broken = new RecordingHook { Fail = true };
        var fine = new RecordingHook();
        var dispatcher = Dispatcher(factory, Hook("broken", broken), Hook("fine", fine));
        await dispatcher.DispatchOnceAsync(default);

        await SaveAsync(records, "z1");
        // Only the fine hook took it. The broken one was offered it, and is offered it again on the next pass.
        Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(2, broken.Batches.Count);
        Assert.Equal(broken.Ids[0], broken.Ids[1]);

        broken.Fail = false;
        Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(fine.Ids.Single(), broken.Ids.Last());
        Assert.Single(fine.Ids);
    }

    [Fact]
    public async Task A_hook_is_given_no_more_than_its_batch_size_at_a_time()
    {
        var (factory, records) = await LoadAsync();
        var hook = new RecordingHook();
        var dispatcher = Dispatcher(factory, Hook("a", hook, batch: 2));
        await dispatcher.DispatchOnceAsync(default);

        foreach (var id in new[] { "z1", "z2", "z3" })
        {
            await SaveAsync(records, id);
        }

        Assert.Equal(2, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal([2, 1], hook.Batches.Select(b => b.Count));
    }

    [Fact]
    public async Task Only_one_dispatcher_serves_a_hook_at_a_time()
    {
        var (factory, records) = await LoadAsync();
        var release = new TaskCompletionSource();
        var hook = new RecordingHook { Wait = release.Task };
        await Dispatcher(factory, Hook("a", hook)).DispatchOnceAsync(default);
        await SaveAsync(records, "z1");

        var first = Dispatcher(factory, Hook("a", hook)).DispatchOnceAsync(default);
        while (hook.Batches.Count == 0)
        {
            await Task.Delay(20);
        }

        // The first is inside the hook with the lock, so a second dispatcher leaves the hook to it.
        Assert.Equal(0, await Dispatcher(factory, Hook("a", hook)).DispatchOnceAsync(default));
        release.SetResult();
        Assert.Equal(1, await first);
        Assert.Single(hook.Ids);
    }

    [Fact]
    public async Task The_console_hook_writes_one_line_of_json_per_event_without_values()
    {
        var (factory, records) = await LoadAsync();
        var output = new StringWriter();
        var dispatcher = Dispatcher(factory, Hook("log", new ConsoleChangeHook("log", output)));
        await dispatcher.DispatchOnceAsync(default);

        await records.CreateAsync("Person", "z1", TestModels.Person("Zoe Adams", new() { ["title"] = "Designer" }), new ChangeReason("Hired"));
        await dispatcher.DispatchOnceAsync(default);

        var line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var json = JsonNode.Parse(line)!;
        Assert.Equal("log", (string?)json["hook"]);
        Assert.Equal("m", (string?)json["model"]);
        Assert.Equal("manual", (string?)json["kind"]);
        Assert.Equal("person:z1", (string?)json["entities"]![0]!["id"]);
        Assert.DoesNotContain("Zoe", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Designer", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_names_hooks_by_kind_and_an_unknown_kind_is_refused()
    {
        var good = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hooks:audit:Kind"] = "console", ["Hooks:audit:Models:0"] = "default", ["Hooks:audit:BatchSize"] = "5000",
        }).Build();
        var hook = Assert.Single(ChangeHookKinds.Build(good, null!));
        Assert.Equal("audit", hook.Key);
        Assert.Equal(["default"], hook.Models);
        Assert.Equal(ChangeHookKinds.MaxBatchSize, hook.BatchSize);
        Assert.IsType<ConsoleChangeHook>(hook.Hook);

        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Hooks:x:Kind"] = "kafka" }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => ChangeHookKinds.Parse(bad));
        Assert.Contains("Hooks:x:Kind", error.Message);
        Assert.Contains("console", error.Message);
    }
}
