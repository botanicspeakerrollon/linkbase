using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class ImportStagingTests(PostgresFixture fixture) : IDisposable
{
    private readonly List<string> _files = [];

    private async Task<(ModelFactory Factory, CsvImportService Importer, ImportStagingService Staging)> ExampleAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        return (factory, new CsvImportService(factory), new ImportStagingService(factory));
    }

    private CsvFile Csv(string fileName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-staging-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        _files.Add(path);
        return CsvImportService.Read(path, fileName);
    }

    private static readonly CsvRecordMapping Suppliers = new("Supplier", 0, 1, [new CsvFieldMapping(2, "city", FieldType.Text), new CsvFieldMapping(3, "rating", FieldType.Number)]);

    [Fact]
    public async Task Staging_changes_nothing_until_it_is_committed()
    {
        var (factory, importer, staging) = await ExampleAsync();
        var file = Csv("suppliers.csv", "id,name,city,rating\ns1,Acme,Bern,4\ns2,Brill,Basel,3\ns3,Cobalt,,5\n");

        var batchId = await importer.StageAsync(file, Suppliers);

        var records = new RecordRepository(factory);
        Assert.Null(await records.TypeAsync("Supplier"));
        var batch = (await staging.GetAsync(batchId))!;
        Assert.Equal((ImportStatus.Staged, 3, 0, 3), (batch.Status, batch.Records, batch.Links, batch.Included));
        Assert.Equal("Supplier records", batch.What);

        var page = await staging.ReviewAsync(batchId, null, 0, 2);
        Assert.Equal(3, page.Total);
        Assert.Equal(["Acme", "Brill"], page.Rows.Select(r => r.Title));
        Assert.Equal("s1 · city: Bern · rating: 4", page.Rows[0].Detail);
        Assert.Equal(ImportStatus.Staged, (await importer.RecentAsync(5)).Single().Status);

        var committed = await staging.CommitAsync(batchId);

        Assert.Equal(3, committed.Records);
        Assert.Equal(3, (await records.ListAsync("Supplier", null, 0, 10)).Total);
        Assert.Equal(("Cobalt", "5"), ((await records.GetAsync("Supplier", "s3"))!.Name, (await records.GetAsync("Supplier", "s3"))!.Values["rating"]));
        Assert.Equal(ImportStatus.Committed, (await staging.GetAsync(batchId))!.Status);
        Assert.Empty((await staging.ReviewAsync(batchId, null, 0, 10)).Rows);

        await importer.UndoAsync(batchId);
        Assert.Null(await records.TypeAsync("Supplier"));
    }

    [Fact]
    public async Task Items_left_out_of_the_review_are_not_imported()
    {
        var (factory, importer, staging) = await ExampleAsync();
        var batchId = await importer.StageAsync(Csv("suppliers.csv", "id,name,city,rating\ns1,Acme,Bern,4\ns2,Brill,Basel,3\ns3,Cobalt,,5\n"), Suppliers);
        var page = await staging.ReviewAsync(batchId, StagedKind.Record, 0, 10);

        await staging.SetIncludedAsync(batchId, [page.Rows[1].Id], false);
        Assert.Equal(2, (await staging.GetAsync(batchId))!.Included);
        Assert.Equal([true, false, true], (await staging.ReviewAsync(batchId, null, 0, 10)).Rows.Select(r => r.Included));

        await staging.SetAllIncludedAsync(batchId, null, false);
        Assert.Equal(0, (await staging.GetAsync(batchId))!.Included);
        await staging.SetAllIncludedAsync(batchId, StagedKind.Record, true);
        await staging.SetIncludedAsync(batchId, [page.Rows[0].Id], false);

        var committed = await staging.CommitAsync(batchId);

        var records = new RecordRepository(factory);
        Assert.Equal(2, committed.Records);
        Assert.Equal(["Brill", "Cobalt"], (await records.ListAsync("Supplier", null, 0, 10)).Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task A_discarded_import_adds_nothing_and_cannot_change_again()
    {
        var (factory, importer, staging) = await ExampleAsync();
        var batchId = await importer.StageAsync(Csv("suppliers.csv", "id,name,city,rating\ns1,Acme,Bern,4\n"), Suppliers);

        await staging.DiscardAsync(batchId);

        Assert.Null(await new RecordRepository(factory).TypeAsync("Supplier"));
        Assert.Equal(ImportStatus.Undone, (await staging.GetAsync(batchId))!.Status);
        await using var db = factory.CreateDbContext();
        Assert.False(await db.StagedItems.AnyAsync());
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => staging.CommitAsync(batchId));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => staging.SetAllIncludedAsync(batchId, null, true));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => staging.DiscardAsync(batchId));
    }

    [Fact]
    public async Task Staged_links_are_counted_and_skip_what_changed_since()
    {
        var (factory, importer, staging) = await ExampleAsync();
        var records = new RecordRepository(factory);
        var file = Csv("assignments.csv", "person,project\np1,pr1\np1,pr3\np2,pr1\np2,pr2\n");
        var mapping = new CsvLinkMapping("Person", 0, "WORKS_ON", "Project", 1);
        var before = (await records.LinksAsync("person:p1")).Where(g => g.RelType == "WORKS_ON").Sum(g => g.Total);

        var batchId = await importer.StageAsync(file, mapping);

        var batch = (await staging.GetAsync(batchId))!;
        Assert.Equal(StagedKind.Link, (await staging.ReviewAsync(batchId, null, 0, 10)).Rows[0].Kind);
        Assert.Equal(batch.Links, (await staging.ReviewAsync(batchId, StagedKind.Link, 0, 10)).Total);

        // One of the two ends goes before the commit, so that link is skipped rather than failing the import.
        await using (var db = factory.CreateDbContext())
        {
            await db.StagedItems.Where(i => i.TargetId == "project:pr3").ExecuteUpdateAsync(s => s.SetProperty(i => i.TargetId, "project:gone"));
        }

        var committed = await staging.CommitAsync(batchId);

        Assert.Equal((3, 2), (batch.Links, committed.Links));
        var after = (await records.LinksAsync("person:p1")).Where(g => g.RelType == "WORKS_ON").Sum(g => g.Total);
        Assert.True(after >= before);
        await importer.UndoAsync(batchId);
        Assert.Equal(before, (await records.LinksAsync("person:p1")).Where(g => g.RelType == "WORKS_ON").Sum(g => g.Total));
    }

    [Fact]
    public async Task Only_a_generic_sources_batches_are_handled_here()
    {
        var (_, _, staging) = await ExampleAsync();

        await Assert.ThrowsAsync<LinkbaseValidationException>(() =>
            staging.StageAsync(new StagingRequest(DataSource.WebImport, "x", "x", ModelChanges.None), [], []));
        Assert.Null(await staging.GetAsync(12345));
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }
}
