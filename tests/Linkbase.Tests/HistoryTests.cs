using System.Text.Json.Nodes;
using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// The audit trail: a reason with every change unless the model says otherwise, what changed, and the model file's settings.
// These load models with the real loader, so reasons are required unless the model file turns them off.
[Trait("Category", "Database")]
public sealed class HistoryTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
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

    // The example consultancy with the given history section, or the model file's defaults if none is given.
    private string ModelWith(JsonObject? history)
    {
        var model = JsonNode.Parse(File.ReadAllText(Example), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        model["folder"] = Path.GetDirectoryName(Example);
        if (history is not null)
        {
            model["history"] = history;
        }

        var path = Path.Combine(Path.GetTempPath(), $"linkbase-history-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, model.ToJsonString());
        _files.Add(path);
        return path;
    }

    private async Task<(ModelFactory Factory, RecordRepository Records)> LoadAsync(JsonObject? history = null)
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(ModelWith(history), TextWriter.Null);
        return (factory, new RecordRepository(factory));
    }

    private static RecordInput Person(string name, string? title = null) =>
        new(name, new Dictionary<string, string?> { ["title"] = title });

    private static async Task<List<Change>> ChangesAsync(ModelFactory factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.Changes.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
    }

    [Fact]
    public async Task A_change_needs_a_reason_by_default_and_the_reason_is_kept()
    {
        var (factory, records) = await LoadAsync();
        await using (var db = factory.CreateDbContext())
        {
            var settings = await ChangeRecorder.SettingsAsync(db, default);
            Assert.Equal((true, true), (settings.Record, settings.RequireReason));
            Assert.Equal(["Internet"], settings.SourceKinds);
        }

        var missing = await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Person", "z1", Person("Zoe Adams")));
        Assert.Contains("why", missing.Message);
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Person", "z1", Person("Zoe Adams"), new ChangeReason("   ")));
        Assert.Null(await records.GetAsync("Person", "z1"));

        await records.CreateAsync("Person", "z1", Person("Zoe Adams", "Designer"), new ChangeReason("Joined in March", "https://example.com/hr/zoe"));

        await using var check = factory.CreateDbContext();
        var justification = await check.Justifications.SingleAsync(j => j.Note == "Joined in March");
        Assert.Equal((JustificationKind.Manual, "https://example.com/hr/zoe"), (justification.Kind, justification.SourceId));
        var created = await check.Changes.SingleAsync(c => c.EntityId == "person:z1");
        Assert.Equal((ChangeEntityKind.Node, ChangeOp.Create), (created.EntityKind, created.Op));
        Assert.Contains("Zoe Adams", created.NewValue);
        Assert.Contains("Designer", created.NewValue);
    }

    [Fact]
    public async Task A_source_that_is_a_web_address_has_to_be_http_or_https()
    {
        var (_, records) = await LoadAsync();

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(
            () => records.CreateAsync("Person", "z1", Person("Zoe Adams"), new ChangeReason("Joined", "ftp://example.com/zoe")));

        Assert.Contains("http", ex.Message);
        Assert.Null(await records.GetAsync("Person", "z1"));
    }

    [Fact]
    public async Task An_edit_keeps_each_field_that_changed_with_its_old_and_new_value()
    {
        var (factory, records) = await LoadAsync();
        await records.CreateAsync("Person", "z1", Person("Zoe Adams", "Designer"), new ChangeReason("Added"));

        await records.UpdateAsync("person:z1", Person("Zoe Adams-Hill", "Designer"), new ChangeReason("Married"));
        await records.UpdateAsync("person:z1", Person("Zoe Adams-Hill", "Lead designer"), new ChangeReason("Promoted"));
        await records.UpdateAsync("person:z1", Person("Zoe Adams-Hill", "Lead designer"), new ChangeReason("Nothing changed"));

        var edits = (await ChangesAsync(factory)).Where(c => c is { EntityKind: ChangeEntityKind.Node, Op: ChangeOp.Update }).ToList();
        Assert.Equal([("name", "Zoe Adams", "Zoe Adams-Hill"), ("title", "Designer", "Lead designer")], edits.Select(c => (c.FieldKey!, c.OldValue!, c.NewValue!)));
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Justifications.AnyAsync(j => j.Note == "Nothing changed"));

        var history = await new HistoryRepository(factory).ForRecordAsync("person:z1");
        Assert.Equal(["Promoted", "Married", "Added"], history.Select(h => h.Note));
    }

    [Fact]
    public async Task Links_and_deletes_are_kept_too()
    {
        var (factory, records) = await LoadAsync();
        await records.CreateAsync("Person", "z1", Person("Zoe Adams"), new ChangeReason("Added"));

        await records.AddLinkAsync("person:z1", "MEMBER_OF", "team:t1", null, new ChangeReason("Joined the team"));
        var link = (await records.LinksAsync("person:z1")).Single(g => g.RelType == "MEMBER_OF").Links.Single();
        await records.AddLinkAsync("person:z1", "WORKS_ON", "project:pr1", new Dictionary<string, string?> { ["role"] = "Designer", ["days_a_week"] = "2" }, new ChangeReason("Staffed"));
        var works = (await records.LinksAsync("person:z1")).Single(g => g.RelType == "WORKS_ON").Links.Single();
        await records.UpdateLinkAsync(works.Id, new Dictionary<string, string?> { ["role"] = "Designer", ["days_a_week"] = "3" }, new ChangeReason("More days"));
        await records.RemoveLinkAsync(link.Id, new ChangeReason("Moved teams"));
        await records.RemoveLinkAsync(works.Id, new ChangeReason("Left the project"));
        await records.DeleteAsync("person:z1", new ChangeReason("Left the company"));

        var changes = await ChangesAsync(factory);
        Assert.Contains(changes, c => c is { EntityKind: ChangeEntityKind.Link, Op: ChangeOp.Create, RelType: "MEMBER_OF", RelatedId: "team:t1" });
        Assert.Contains(changes, c => c is { EntityKind: ChangeEntityKind.Link, Op: ChangeOp.Update, FieldKey: "days_a_week", OldValue: "2", NewValue: "3" });
        Assert.Equal(2, changes.Count(c => c is { EntityKind: ChangeEntityKind.Link, Op: ChangeOp.Delete }));
        var deleted = changes.Single(c => c is { EntityKind: ChangeEntityKind.Node, Op: ChangeOp.Delete });
        Assert.Contains("Zoe Adams", deleted.OldValue);

        // The history of a record that's gone is still there, and the project's page shows the links to it.
        var history = await new HistoryRepository(factory).ForRecordAsync("person:z1");
        Assert.Equal("Left the company", history[0].Note);
        var project = await new HistoryRepository(factory).ForRecordAsync("project:pr1");
        Assert.Equal(["Left the project", "More days", "Staffed"], project.Select(h => h.Note));
        Assert.Equal("person:z1", project[0].Changes.Single().OtherName);
    }

    [Fact]
    public async Task Deleting_a_record_keeps_the_required_links_and_ids_that_went_with_it()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(Library.Write(), TextWriter.Null);
        var records = new RecordRepository(factory);
        await records.CreateAsync("Book", "x1", new RecordInput("Quiet Ice", new Dictionary<string, string?> { ["isbn"] = "333" })
        {
            Links = [new NewLink("WRITTEN_BY", "author:a3")],
        }, new ChangeReason("Acquired"));
        await new ExternalRefRepository(factory).AddAsync("book:x1", DataSource.Manual, "ref-9");

        await records.DeleteAsync("book:x1", new ChangeReason("Withdrawn"));

        var changes = await ChangesAsync(factory);
        Assert.Contains(changes, c => c is { EntityKind: ChangeEntityKind.Link, Op: ChangeOp.Create, RelType: "WRITTEN_BY", RelatedId: "author:a3" });
        Assert.Contains(changes, c => c is { EntityKind: ChangeEntityKind.Link, Op: ChangeOp.Delete, RelType: "WRITTEN_BY", RelatedId: "author:a3" });
        Assert.Contains(changes, c => c is { EntityKind: ChangeEntityKind.ExternalRef, Op: ChangeOp.Delete, OldValue: "ref-9" });
        var author = await new HistoryRepository(factory).ForRecordAsync("author:a3");
        Assert.Equal(["Withdrawn", "Acquired"], author.Select(h => h.Note));
        Assert.Equal("book:x1", author[0].Changes.Single().OtherName);
    }

    [Fact]
    public async Task A_model_can_make_the_reason_optional()
    {
        var (factory, records) = await LoadAsync(new JsonObject { ["requireReason"] = false });

        await records.CreateAsync("Person", "z1", Person("Zoe Adams"));
        await records.CreateAsync("Person", "z2", Person("Zed Hall"), new ChangeReason("Hired"));

        Assert.Equal(new HistorySettings(true, false), await records.HistorySettingsAsync());
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Justifications.CountAsync(j => j.Note != null && j.Kind == JustificationKind.Manual));
        Assert.Equal(2, await db.Changes.CountAsync(c => c.EntityKind == ChangeEntityKind.Node && c.Op == ChangeOp.Create));
    }

    [Fact]
    public async Task A_model_can_turn_history_off()
    {
        var (factory, records) = await LoadAsync(new JsonObject { ["record"] = false });

        await records.CreateAsync("Person", "z1", Person("Zoe Adams"));
        await records.UpdateAsync("person:z1", Person("Zoe Adams-Hill"));
        await records.DeleteAsync("person:z1");

        Assert.Equal(new HistorySettings(false, false), await records.HistorySettingsAsync());
        await using var db = factory.CreateDbContext();
        Assert.False(await db.Changes.AnyAsync());
        Assert.False(await db.Justifications.AnyAsync());
    }

    [Fact]
    public void History_cant_be_off_and_still_require_a_reason()
    {
        var path = ModelWith(new JsonObject { ["record"] = false, ["requireReason"] = true });

        var ex = Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(path));

        Assert.Contains("History can't both be off and require a reason", ex.Message);
        Assert.False(ModelFile.Read(ModelWith(new JsonObject { ["record"] = false })).RequireReason);
        Assert.True(ModelFile.Read(ModelWith(null)).RequireReason);
        Assert.True(ModelFile.Read(ModelWith(null)).RecordHistory);
    }

    [Fact]
    public async Task Turning_history_off_keeps_what_was_recorded_and_a_reload_keeps_it_too()
    {
        var (factory, records) = await LoadAsync();
        await records.CreateAsync("Person", "z1", Person("Zoe Adams"), new ChangeReason("Added"));
        var before = (await ChangesAsync(factory)).Count;

        await new ModelLoader(factory).LoadAsync(ModelWith(new JsonObject { ["record"] = false }), TextWriter.Null);
        await records.CreateAsync("Person", "z2", Person("Zed Hall"));

        Assert.Equal(before, (await ChangesAsync(factory)).Count);
        Assert.Contains((await ChangesAsync(factory)), c => c.EntityId == "person:z1");

        await new ModelLoader(factory).LoadAsync(ModelWith(null), TextWriter.Null);
        Assert.True((await records.HistorySettingsAsync()).NeedsReason);
        await using var db = factory.CreateDbContext();
        Assert.Contains(await db.Justifications.Select(j => j.Note).ToListAsync(), n => n != null && n.StartsWith("Loaded the Example consultancy model", StringComparison.Ordinal));
        Assert.Contains(await db.Changes.ToListAsync(), c => c.EntityId == "person:z1");
    }

    [Fact]
    public async Task An_import_is_one_entry_and_its_records_show_it_in_their_history()
    {
        var (factory, _) = await LoadAsync();
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-history-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "id,name,city\ns1,Acme,Bern\ns2,Brill,Basel\n");
        _files.Add(path);
        var importer = new CsvImportService(factory);
        var staging = new ImportStagingService(factory);
        var batchId = await importer.StageAsync(CsvImportService.Read(path, "suppliers.csv"), new CsvRecordMapping("Supplier", 0, 1, [new CsvFieldMapping(2, "city", FieldType.Text)]));

        await staging.CommitAsync(batchId);

        var changes = await ChangesAsync(factory);
        var entry = changes.Single(c => c.EntityKind == ChangeEntityKind.ImportBatch && c.Op == ChangeOp.Create);
        Assert.Equal(batchId.ToString(), entry.EntityId);
        Assert.Contains("\"records\":2", entry.NewValue);
        Assert.Contains(changes, c => c is { EntityKind: ChangeEntityKind.Definition, Op: ChangeOp.Create, EntityId: "type:Supplier" });
        Assert.DoesNotContain(changes, c => c.EntityKind == ChangeEntityKind.Node);

        var history = await new HistoryRepository(factory).ForRecordAsync("supplier:s1");
        var added = Assert.Single(history);
        Assert.Equal((JustificationKind.Import, batchId), (added.Kind, added.ImportBatchId));
        Assert.Equal($"Added by import #{batchId}", added.Note);

        await staging.UndoAsync(batchId);
        Assert.Contains(await ChangesAsync(factory), c => c.EntityKind == ChangeEntityKind.ImportBatch && c.Op == ChangeOp.Delete && c.EntityId == batchId.ToString());
    }

    [Fact]
    public async Task A_modules_rebuild_is_one_derived_entry_listing_only_what_differs()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"linkbase-history-owned-{Guid.NewGuid():N}");
        try
        {
            var factory = await OwnedModel.LoadAsync(fixture, folder);
            var records = new RecordRepository(factory);
            var first = new DerivedRecord("2024-wales", "Wales, May 2024", new Dictionary<string, string?> { ["country"] = "Wales" });
            var peaks = new DerivedRecord("2024-peaks", "Peaks, May 2024", new Dictionary<string, string?>());

            await records.ReplaceDerivedAsync("Climbing", "Trip", [first, peaks], []);
            await records.ReplaceDerivedAsync("Climbing", "Trip", [first with { Name = "Wales, Spring 2024" }], []);
            await records.ReplaceDerivedAsync("Climbing", "Trip", [first with { Name = "Wales, Spring 2024" }], []);

            await using var db = factory.CreateDbContext();
            var sets = await db.ChangeSets.Include(s => s.Justification).Where(s => s.Justification.Kind == JustificationKind.Derived).OrderBy(s => s.Id).ToListAsync();
            Assert.Equal(2, sets.Count);
            Assert.All(sets, s => Assert.Equal("Climbing rebuilt its Trip records", s.Justification.Note));
            var changes = await ChangesAsync(factory);
            var latest = changes.Where(c => c.ChangeSetId == sets[^1].Id).ToList();
            Assert.Contains(latest, c => c is { Op: ChangeOp.Update, FieldKey: "name", NewValue: "Wales, Spring 2024" });
            Assert.Contains(latest, c => c is { Op: ChangeOp.Delete, EntityId: "trip:2024-peaks" });
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task What_caused_a_rebuild_is_kept_with_it()
    {
        var (factory, _) = await LoadAsync();
        await using var db = factory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var cause = await ChangeRecorder.WriteAsync(db, JustificationKind.Manual, new ChangeReason("Edited"), [ChangeRecorder.NodeDeleted("person:p1", "A", new Dictionary<string, string>())]);

        long? derived;
        using (ChangeRecorder.Causing(cause))
        {
            derived = await ChangeRecorder.WriteAsync(db, JustificationKind.Derived, new ChangeReason("Rebuilt"), [ChangeRecorder.NodeDeleted("person:p2", "B", new Dictionary<string, string>())],
                causeChangeSetId: ChangeRecorder.Cause);
        }

        Assert.Equal(cause, (await db.ChangeSets.SingleAsync(s => s.Id == derived)).CauseChangeSetId);
        Assert.Null(ChangeRecorder.Cause);
    }
}
