using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// Updating records from an import, putting them back by undoing it, and reverting one edit from the history.
[Trait("Category", "Database")]
public sealed class UpdateAndRevertTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private CsvFile Csv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-update-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        _files.Add(path);
        return CsvImportService.Read(path, "suppliers.csv");
    }

    private static CsvRecordMapping Mapping(bool update) =>
        new("Supplier", 0, 1, [new CsvFieldMapping(2, "city", FieldType.Text), new CsvFieldMapping(3, "rating", FieldType.Number)], update);

    // Two suppliers, imported by an earlier file.
    private async Task<(ModelFactory Factory, CsvImportService Importer, RecordRepository Records)> WithSuppliersAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        var importer = new CsvImportService(factory);
        await importer.ImportAsync(Csv("id,name,city,rating\ns1,Acme,Bern,3\ns2,Brill,Basel,4\n"), Mapping(false));
        return (factory, importer, new RecordRepository(factory));
    }

    [Fact]
    public async Task A_file_leaves_records_already_there_unless_asked_to_update_them()
    {
        var (_, importer, records) = await WithSuppliersAsync();
        var file = Csv("id,name,city,rating\ns1,Acme,Zurich,3\n");

        var plan = await importer.PlanAsync(file, Mapping(false));
        Assert.Equal((0, 1, 0), (plan.ToAdd, plan.AlreadyThere, plan.ToUpdate));
        Assert.False(plan.CanImport);
        Assert.Equal("Bern", (await records.GetAsync("Supplier", "s1"))!.Values["city"]);
    }

    [Fact]
    public async Task Updating_changes_only_what_differs_keeps_the_rest_and_undo_puts_it_back()
    {
        var (factory, importer, records) = await WithSuppliersAsync();

        // s1 gets a new city and no name or rating, s2 is the same as it was, and s3 is new.
        var file = Csv("id,name,city,rating\ns1,,Zurich,\ns2,Brill,Basel,4\ns3,Cole,Geneva,5\n");
        var plan = await importer.PlanAsync(file, Mapping(true));
        Assert.Equal((1, 1, 1), (plan.ToAdd, plan.AlreadyThere, plan.ToUpdate));
        Assert.True(plan.CanImport);

        var result = await importer.ImportAsync(file, Mapping(true));
        Assert.Equal((1, 1), (result.Added, result.Updated));

        var s1 = (await records.GetAsync("Supplier", "s1"))!;
        Assert.Equal("Acme", s1.Name);
        Assert.Equal(("Zurich", "3"), (s1.Values["city"], s1.Values["rating"]));
        Assert.NotEqual(s1.CreatedAt, s1.UpdatedAt);

        // The record's history says what changed and which import did it.
        var history = await new HistoryRepository(factory).ForRecordAsync("supplier:s1");
        var entry = history.First(h => h.ImportBatchId == result.BatchId);
        Assert.Equal(JustificationKind.Import, entry.Kind);
        Assert.Equal(("city", "Bern", "Zurich"), entry.Changes.Select(c => (c.FieldKey!, c.OldValue!, c.NewValue!)).Single());

        var undo = await importer.UndoAsync(result.BatchId);
        Assert.True(undo.Complete);
        Assert.Equal(1, undo.Reverted);
        Assert.Equal("Bern", (await records.GetAsync("Supplier", "s1"))!.Values["city"]);
        Assert.Null(await records.GetAsync("Supplier", "s3"));

        // The undo is in the history too, and a second one has nothing left to do.
        Assert.Contains((await new HistoryRepository(factory).ForRecordAsync("supplier:s1")), h => h.Note == $"Import #{result.BatchId} undone");
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => importer.UndoAsync(result.BatchId));
    }

    [Fact]
    public async Task Undo_keeps_a_value_that_was_changed_again_since()
    {
        var (_, importer, records) = await WithSuppliersAsync();
        var result = await importer.ImportAsync(Csv("id,name,city,rating\ns1,Acme,Zurich,5\n"), Mapping(true));
        Assert.Equal(1, result.Updated);

        // Someone edits the city afterwards. The rating wasn't touched, so it goes back.
        await records.UpdateAsync("supplier:s1", new RecordInput("Acme", new Dictionary<string, string?> { ["city"] = "Lausanne", ["rating"] = "5" }), new ChangeReason("Moved"));
        var undo = await importer.UndoAsync(result.BatchId);

        Assert.False(undo.Complete);
        Assert.Contains("City was changed since the import", undo.Kept.Single());
        var s1 = (await records.GetAsync("Supplier", "s1"))!;
        Assert.Equal(("Lausanne", "3"), (s1.Values["city"], s1.Values["rating"]));
        Assert.Equal(ImportStatus.Committed, (await importer.RecentAsync(5)).First(b => b.Id == result.BatchId).Status);
    }

    [Fact]
    public async Task The_review_shows_what_each_record_would_change()
    {
        var (factory, importer, _) = await WithSuppliersAsync();
        var batch = await importer.StageAsync(Csv("id,name,city,rating\ns1,Acme,Zurich,3\ns3,Cole,Geneva,5\n"), Mapping(true));

        var staging = new ImportStagingService(factory);
        var summary = (await staging.GetAsync(batch))!;
        Assert.Equal((2, 1), (summary.Records, summary.Updating));
        var rows = (await staging.ReviewAsync(batch, null, 0, 10)).Rows;
        Assert.Equal("Update s1: city: Bern → Zurich", rows[0].Detail);
        Assert.True(rows[0].Updating);
        Assert.False(rows[1].Updating);

        // Nothing has changed until it is committed.
        Assert.Equal("Bern", (await new RecordRepository(factory).GetAsync("Supplier", "s1"))!.Values["city"]);
        var commit = await staging.CommitAsync(batch);
        Assert.Equal((1, 1), (commit.Records, commit.Updated));
    }

    [Fact]
    public async Task A_model_without_history_cannot_update_because_nothing_could_put_it_back()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        await using (var db = factory.CreateDbContext())
        {
            if (await db.ModelSettings.ExecuteUpdateAsync(m => m.SetProperty(x => x.RecordHistory, false).SetProperty(x => x.RequireReason, false)) == 0)
            {
                db.ModelSettings.Add(new ModelSetting { RecordHistory = false, RequireReason = false });
                await db.SaveChangesAsync();
            }
        }

        var importer = new CsvImportService(factory);
        var file = Csv("id,name,city,rating\ns1,Acme,Bern,3\n");
        Assert.Contains("keep a history", (await importer.PlanAsync(file, Mapping(true))).Problems.Single());
        Assert.Contains("keep a history", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => importer.StageAsync(file, Mapping(true)))).Message);
    }

    // Updating a link's values from an import, and undoing it.

    private static CsvLinkMapping WorksOnMapping(bool update) =>
        new("Person", 0, "WORKS_ON", "Project", 1, [new CsvFieldMapping(2, "role", FieldType.Text), new CsvFieldMapping(3, "days_a_week", FieldType.Number)])
        { UpdateExisting = update };

    private async Task<(ModelFactory Factory, CsvImportService Importer, RecordRepository Records)> WithConsultancyAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        return (factory, new CsvImportService(factory), new RecordRepository(factory));
    }

    [Fact]
    public async Task A_links_file_adds_another_link_unless_asked_to_update_the_one_there()
    {
        // p1 already works on pr2 as "Design lead", 2 days a week. Without asking to update, a file with different values
        // adds a second link rather than changing it, same as an import always has.
        var (_, importer, records) = await WithConsultancyAsync();
        var file = Csv("person,project,role,days_a_week\np1,pr2,Lead,3\n");

        var plan = await importer.PlanAsync(file, WorksOnMapping(false));
        Assert.Equal((1, 0, 0), (plan.ToAdd, plan.AlreadyThere, plan.ToUpdate));

        await importer.ImportAsync(file, WorksOnMapping(false));
        var links = (await records.LinksAsync("person:p1")).Single(g => g.RelType == "WORKS_ON").Links.Where(l => l.OtherKey == "pr2").ToList();
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public async Task Updating_a_links_file_changes_only_its_values_and_undo_puts_them_back()
    {
        var (factory, importer, records) = await WithConsultancyAsync();
        var file = Csv("person,project,role,days_a_week\np1,pr2,Lead,3\n");

        var plan = await importer.PlanAsync(file, WorksOnMapping(true));
        Assert.Equal((0, 0, 1), (plan.ToAdd, plan.AlreadyThere, plan.ToUpdate));

        var result = await importer.ImportAsync(file, WorksOnMapping(true));
        Assert.Equal((0, 1), (result.Added, result.Updated));

        var link = (await records.LinksAsync("person:p1")).Single(g => g.RelType == "WORKS_ON").Links.Single(l => l.OtherKey == "pr2");
        Assert.Equal(("Lead", "3"), (link.Values["role"], link.Values["days_a_week"]));

        var undo = await importer.UndoAsync(result.BatchId);
        Assert.True(undo.Complete);
        Assert.Equal(1, undo.Reverted);
        var back = (await records.LinksAsync("person:p1")).Single(g => g.RelType == "WORKS_ON").Links.Single(l => l.OtherKey == "pr2");
        Assert.Equal(("Design lead", "2"), (back.Values["role"], back.Values["days_a_week"]));
    }

    [Fact]
    public async Task Undo_keeps_a_links_value_that_was_changed_again_since()
    {
        var (_, importer, records) = await WithConsultancyAsync();
        var result = await importer.ImportAsync(Csv("person,project,role,days_a_week\np1,pr2,Lead,3\n"), WorksOnMapping(true));
        Assert.Equal(1, result.Updated);

        var link = (await records.LinksAsync("person:p1")).Single(g => g.RelType == "WORKS_ON").Links.Single(l => l.OtherKey == "pr2");
        await records.UpdateLinkAsync(link.Id, new Dictionary<string, string?> { ["role"] = "Director", ["days_a_week"] = "3" });

        var undo = await importer.UndoAsync(result.BatchId);
        Assert.False(undo.Complete);
        Assert.Contains("link's Role was changed since the import", undo.Kept.Single());

        var now = (await records.LinksAsync("person:p1")).Single(g => g.RelType == "WORKS_ON").Links.Single(l => l.OtherKey == "pr2");
        Assert.Equal(("Director", "2"), (now.Values["role"], now.Values["days_a_week"]));
    }

    [Fact]
    public async Task A_link_updates_only_when_the_two_are_linked_that_way_exactly_once()
    {
        var (_, importer, records) = await WithConsultancyAsync();

        // A second WORKS_ON link between p1 and pr2 means it's no longer clear which one the file would mean, so asking
        // to update adds a third link instead of changing either.
        await records.AddLinkAsync("person:p1", "WORKS_ON", "project:pr2", new Dictionary<string, string?> { ["role"] = "Reviewer", ["days_a_week"] = "1" });
        var file = Csv("person,project,role,days_a_week\np1,pr2,Lead,3\n");

        var plan = await importer.PlanAsync(file, WorksOnMapping(true));
        Assert.Equal((1, 0), (plan.ToAdd, plan.ToUpdate));

        await importer.ImportAsync(file, WorksOnMapping(true));
        var links = (await records.LinksAsync("person:p1")).Single(g => g.RelType == "WORKS_ON").Links.Where(l => l.OtherKey == "pr2").ToList();
        Assert.Equal(3, links.Count);
    }

    [Fact]
    public async Task A_model_without_history_cannot_update_links_either()
    {
        var (factory, _, _) = await WithConsultancyAsync();
        await using (var db = factory.CreateDbContext())
        {
            if (await db.ModelSettings.ExecuteUpdateAsync(m => m.SetProperty(x => x.RecordHistory, false).SetProperty(x => x.RequireReason, false)) == 0)
            {
                db.ModelSettings.Add(new ModelSetting { RecordHistory = false, RequireReason = false });
                await db.SaveChangesAsync();
            }
        }

        var importer = new CsvImportService(factory);
        var file = Csv("person,project,role,days_a_week\np1,pr2,Lead,3\n");
        Assert.Contains("keep a history", (await importer.PlanAsync(file, WorksOnMapping(true))).Problems.Single());
        Assert.Contains("keep a history", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => importer.StageAsync(file, WorksOnMapping(true)))).Message);
    }

    // Reverting an edit.

    private async Task<(ModelFactory Factory, RecordRepository Records, ChangeReverter Reverter)> WithPersonAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        var records = new RecordRepository(factory);
        await records.CreateAsync("Person", "z1", TestModels.Person("Zoe Adams", new() { ["title"] = "Designer" }), new ChangeReason("Added"));
        return (factory, records, new ChangeReverter(factory, records));
    }

    private static async Task<long> LatestAsync(ModelFactory factory, string id) =>
        (await new HistoryRepository(factory).ForRecordAsync(id))[0].ChangeSetId;

    [Fact]
    public async Task Reverting_an_edit_puts_the_old_values_back_as_an_edit_of_its_own()
    {
        var (factory, records, reverter) = await WithPersonAsync();
        await records.UpdateAsync("person:z1", TestModels.Person("Zoe Adams", new() { ["title"] = "Lead designer" }), new ChangeReason("Promoted"));

        await reverter.RevertAsync(await LatestAsync(factory, "person:z1"), "Wrong person");

        Assert.Equal("Designer", (await records.GetAsync("Person", "z1"))!.Values["title"]);
        var history = await new HistoryRepository(factory).ForRecordAsync("person:z1");
        Assert.Equal("Reverted change #" + history[1].ChangeSetId + ": Wrong person", history[0].Note);

        // It can't be reverted twice.
        var again = await Assert.ThrowsAsync<LinkbaseValidationException>(() => reverter.RevertAsync(history[1].ChangeSetId));
        Assert.Contains("already back", again.Message);
    }

    [Fact]
    public async Task A_revert_refuses_when_a_value_has_been_changed_again()
    {
        var (factory, records, reverter) = await WithPersonAsync();
        await records.UpdateAsync("person:z1", TestModels.Person("Zoe Adams", new() { ["title"] = "Lead designer" }), new ChangeReason("Promoted"));
        var promoted = await LatestAsync(factory, "person:z1");
        await records.UpdateAsync("person:z1", TestModels.Person("Zoe Adams", new() { ["title"] = "Director" }), new ChangeReason("Promoted again"));

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => reverter.RevertAsync(promoted));
        Assert.Contains("Title is now 'Director'", ex.Message);
        Assert.Equal("Director", (await records.GetAsync("Person", "z1"))!.Values["title"]);
    }

    [Fact]
    public async Task Reverting_a_removed_link_links_the_records_again_and_reverting_an_added_one_removes_it()
    {
        var (factory, records, reverter) = await WithPersonAsync();
        await records.AddLinkAsync("person:z1", "WORKS_ON", "project:pr1", new Dictionary<string, string?> { ["role"] = "Designer", ["days_a_week"] = "2" }, new ChangeReason("Staffed"));
        var staffed = await LatestAsync(factory, "person:z1");
        var link = (await records.LinksAsync("person:z1")).Single(g => g.RelType == "WORKS_ON").Links.Single();
        await records.RemoveLinkAsync(link.Id, new ChangeReason("Left"));

        await reverter.RevertAsync(await LatestAsync(factory, "person:z1"));
        var back = (await records.LinksAsync("person:z1")).Single(g => g.RelType == "WORKS_ON").Links.Single();
        Assert.Equal(("Designer", "2"), (back.Values["role"], back.Values["days_a_week"]));

        // Undoing the first link now removes this one, since it has the same values.
        await reverter.RevertAsync(staffed);
        Assert.DoesNotContain(await records.LinksAsync("person:z1"), g => g.RelType == "WORKS_ON");
    }

    [Fact]
    public async Task Reverting_a_delete_brings_the_record_back_with_its_values()
    {
        var (factory, records, reverter) = await WithPersonAsync();
        await records.AddLinkAsync("person:z1", "WORKS_ON", "project:pr1", new Dictionary<string, string?> { ["role"] = "Designer" }, new ChangeReason("Staffed"));
        var works = (await records.LinksAsync("person:z1")).Single(g => g.RelType == "WORKS_ON").Links.Single();
        await records.RemoveLinkAsync(works.Id, new ChangeReason("Left"));
        await records.DeleteAsync("person:z1", new ChangeReason("Left the company"));
        Assert.Null(await records.GetAsync("Person", "z1"));

        await using var db = factory.CreateDbContext();
        var deleted = db.ChangeSets.Where(s => s.Justification.Note == "Left the company").Select(s => s.Id).Single();
        await reverter.RevertAsync(deleted);

        var back = (await records.GetAsync("Person", "z1"))!;
        Assert.Equal(("Zoe Adams", "Designer"), (back.Name, back.Values["title"]));
    }

    [Fact]
    public async Task Imports_and_changes_to_several_records_are_not_reverted_this_way()
    {
        var (factory, records, reverter) = await WithPersonAsync();
        var importer = new CsvImportService(factory);
        var result = await importer.ImportAsync(Csv("id,name,city,rating\ns1,Acme,Bern,3\n"), Mapping(false));
        await using var db = factory.CreateDbContext();
        var import = db.ChangeSets.Where(s => s.Justification.ImportBatchId == result.BatchId).Select(s => s.Id).First();

        Assert.Contains("imports page", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => reverter.RevertAsync(import))).Message);
        Assert.Contains("no such change", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => reverter.RevertAsync(999_999))).Message);
    }
}
