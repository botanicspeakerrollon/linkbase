using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Linkbase.Tests;

// Change events for other systems (INT-1): one per committed change set, written in its transaction, with or without history,
// naming what changed but no values, in commit order. See docs/history/change-hooks-plan.md.
[Trait("Category", "Database")]
public sealed class ChangeEventTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
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

    private async Task<(ModelFactory Factory, RecordRepository Records)> LoadAsync(JsonObject? history = null)
    {
        var model = JsonNode.Parse(File.ReadAllText(Example), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        model["folder"] = Path.GetDirectoryName(Example);
        if (history is not null)
        {
            model["history"] = history;
        }

        var path = Path.Combine(Path.GetTempPath(), $"linkbase-events-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, model.ToJsonString());
        _files.Add(path);
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory, Linkbase.Domains.RegisteredDomains.Registry).LoadAsync(path, TextWriter.Null);
        return (factory, new RecordRepository(factory, RecordHooks.None, Linkbase.Core.Security.FieldPolicy.Unrestricted));
    }

    private static async Task<List<ChangeEvent>> EventsAsync(ModelFactory factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.ChangeEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private static JsonArray Entities(ChangeEvent e) => JsonNode.Parse(e.Entities)!.AsArray();

    [Fact]
    public async Task A_save_raises_one_event_naming_what_changed_without_its_values()
    {
        var (factory, records) = await LoadAsync();
        var loaded = Assert.Single(await EventsAsync(factory));
        Assert.Equal(JustificationKind.Import, loaded.Kind);
        Assert.StartsWith("model:", (string?)Entities(loaded)[0]!["id"]);

        await records.CreateAsync("Person", "z1", TestModels.Person("Zoe Adams", new() { ["title"] = "Designer" }), new ChangeReason("Hired"));
        await records.UpdateAsync("person:z1", TestModels.Person("Zoe Adams-Hill", new() { ["title"] = "Lead designer" }), new ChangeReason("Promoted"));
        await records.AddLinkAsync("person:z1", "MEMBER_OF", "team:t1", null, new ChangeReason("Joined"));

        var events = (await EventsAsync(factory)).Skip(1).ToList();
        Assert.Equal(3, events.Count);
        await using var db = factory.CreateDbContext();
        foreach (var e in events)
        {
            Assert.Equal(JustificationKind.Manual, e.Kind);
            Assert.NotNull(e.ChangeSetId);
            Assert.True(await db.ChangeSets.AnyAsync(s => s.Id == e.ChangeSetId));
            Assert.DoesNotContain("Designer", e.Entities, StringComparison.Ordinal);
            Assert.DoesNotContain("Zoe", e.Entities, StringComparison.Ordinal);
        }

        // jsonb reorders keys, so compare the parsed entry rather than the text.
        var created = Assert.Single(Entities(events[0]));
        Assert.Equal(("node", "person:z1", "create"), ((string?)created!["kind"], (string?)created["id"], (string?)created["op"]));
        var updated = Assert.Single(Entities(events[1]));
        Assert.Equal("update", (string?)updated!["op"]);
        var fields = updated["fields"]!.AsArray().Select(f => (string)f!).ToList();
        Assert.Contains("name", fields);
        Assert.Contains("title", fields);
        Assert.Equal(1, events[1].EntityCount);
        var link = Assert.Single(Entities(events[2]));
        Assert.Equal(("link", "person:z1", "MEMBER_OF", "team:t1", "create"),
            ((string?)link!["kind"], (string?)link["source"], (string?)link["rel"], (string?)link["target"], (string?)link["op"]));
    }

    [Fact]
    public async Task A_model_without_history_still_raises_events()
    {
        var (factory, records) = await LoadAsync(new JsonObject { ["record"] = false });

        await records.CreateAsync("Person", "z1", TestModels.Person("Zoe Adams", new()));
        await records.UpdateAsync("person:z1", TestModels.Person("Zoe Adams-Hill", new()));
        await records.DeleteAsync("person:z1");

        var events = await EventsAsync(factory);
        Assert.Equal(["update", "create", "update", "delete"], events.Select(e => (string)Entities(e)[0]!["op"]!));
        Assert.All(events, e => Assert.Null(e.ChangeSetId));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Changes.AnyAsync());
    }

    [Fact]
    public async Task A_change_that_fails_or_rolls_back_raises_nothing()
    {
        var (factory, records) = await LoadAsync();
        var before = (await EventsAsync(factory)).Count;

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Person", "z1", TestModels.Person("Zoe Adams", new())));
        await using (var db = factory.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await ChangeRecorder.WriteAsync(db, HistorySettings.Default, JustificationKind.Manual, new ChangeReason("Tried"),
                [ChangeRecorder.NodeDeleted("person:p1", "A", new Dictionary<string, string>())]);
            await transaction.RollbackAsync();
        }

        Assert.Equal(before, (await EventsAsync(factory)).Count);
    }

    [Fact]
    public async Task An_import_is_one_event_naming_its_batch_rather_than_each_record()
    {
        var (factory, _) = await LoadAsync();
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-events-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "id,name,city\ns1,Acme,Bern\ns2,Brill,Basel\ns3,Corte,Chur\n");
        _files.Add(path);
        var importer = new CsvImportService(factory);
        var staging = new ImportStagingService(factory);
        var batchId = await importer.StageAsync(CsvImportService.Read(path, "suppliers.csv"), new CsvRecordMapping("Supplier", 0, 1, [new CsvFieldMapping(2, "city", FieldType.Text)]));
        var before = (await EventsAsync(factory)).Count;

        await staging.CommitAsync(batchId);
        await staging.UndoAsync(batchId);

        var events = (await EventsAsync(factory)).Skip(before).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal((JustificationKind.Import, batchId), (e.Kind, e.ImportBatchId)));
        var committed = Entities(events[0]);
        Assert.Contains(committed, e => (string?)e!["kind"] == "import_batch" && (string?)e["op"] == "create" && (string?)e["id"] == batchId.ToString());
        Assert.Contains(committed, e => (string?)e!["kind"] == "definition" && (string?)e["id"] == "type:Supplier");
        Assert.DoesNotContain(committed, e => (string?)e!["kind"] == "node");
        Assert.Contains(Entities(events[1]), e => (string?)e!["kind"] == "import_batch" && (string?)e["op"] == "delete");
    }

    // The second writer can't get its event's id until the first commits, so ids are in commit order and a reader going by id
    // never passes over an event that commits late.
    [Fact]
    public async Task A_second_writer_waits_for_the_first_to_commit_so_ids_follow_commit_order()
    {
        var (factory, _) = await LoadAsync();
        await using var first = factory.CreateDbContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        await ChangeRecorder.WriteAsync(first, HistorySettings.Default, JustificationKind.Manual, new ChangeReason("First"),
            [ChangeRecorder.NodeDeleted("person:a", "A", new Dictionary<string, string>())]);

        var second = Task.Run(async () =>
        {
            await using var db = factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await ChangeRecorder.WriteAsync(db, HistorySettings.Default, JustificationKind.Manual, new ChangeReason("Second"),
                [ChangeRecorder.NodeDeleted("person:b", "B", new Dictionary<string, string>())]);
            await transaction.CommitAsync();
        });

        await WaitForAdvisoryLockWaiterAsync(factory);
        Assert.False(second.IsCompleted);
        await firstTransaction.CommitAsync();
        await second;

        var events = (await EventsAsync(factory)).Skip(1).ToList();
        Assert.Equal(["person:a", "person:b"], events.Select(e => (string)Entities(e)[0]!["id"]!));
        Assert.True(events[0].Id < events[1].Id);
    }

    private static async Task WaitForAdvisoryLockWaiterAsync(ModelFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        for (var i = 0; i < 100; i++)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The second writer never waited for the event lock.");
    }
}

// How a change set becomes an event's list of what changed.
public sealed class ChangeEventEntitiesTests
{
    [Fact]
    public void Field_edits_to_one_record_are_one_entry_and_a_long_list_is_cut_but_counted()
    {
        var (json, count) = ChangeRecorder.EventEntities(
        [
            new PendingChange(ChangeEntityKind.Node, "person:a", ChangeOp.Update, "name", OldValue: "A", NewValue: "B"),
            new PendingChange(ChangeEntityKind.Node, "person:a", ChangeOp.Update, "title", OldValue: "x", NewValue: "y"),
            ChangeRecorder.LinkDeleted("person:a", "MEMBER_OF", "team:t", new Dictionary<string, string>()),
            ChangeRecorder.NodeDeleted("person:b", "B", new Dictionary<string, string> { ["secret"] = "do not send" }),
        ]);

        Assert.Equal(3, count);
        Assert.DoesNotContain("do not send", json, StringComparison.Ordinal);
        Assert.Equal(
            """[{"kind":"node","id":"person:a","op":"update","fields":["name","title"]},{"kind":"link","source":"person:a","rel":"MEMBER_OF","target":"team:t","op":"delete"},{"kind":"node","id":"person:b","op":"delete"}]""",
            json);

        var many = Enumerable.Range(0, ChangeRecorder.EventEntityLimit + 5).Select(i => ChangeRecorder.NodeDeleted($"person:{i}", "P", new Dictionary<string, string>())).ToList();
        var (cut, total) = ChangeRecorder.EventEntities(many);
        Assert.Equal(ChangeRecorder.EventEntityLimit + 5, total);
        Assert.Equal(ChangeRecorder.EventEntityLimit, JsonNode.Parse(cut)!.AsArray().Count);
    }
}
