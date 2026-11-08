using Bunit;
using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Graph;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class CsvImportTests(PostgresFixture fixture) : IDisposable
{
    private readonly List<string> _files = [];

    private async Task<(ModelFactory Factory, CsvImportService Importer)> ExampleAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        return (factory, new CsvImportService(factory));
    }

    private CsvFile Csv(string fileName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-csv-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        _files.Add(path);
        return CsvImportService.Read(path, fileName);
    }

    [Fact]
    public void Reads_the_columns_and_guesses_their_types()
    {
        var file = Csv("suppliers.csv", "Supplier ID,Name,Founded,Staff,Approved,Notes\ns1,Acme,2001-04-02,12,true,Good\ns2,Brill,1999-12-31,3.5,false,\n");

        Assert.Equal(2, file.Rows);
        Assert.Equal(["s2", "Brill", "1999-12-31", "3.5", "false", ""], file.Preview[1]);
        Assert.Equal(
            [FieldType.Text, FieldType.Text, FieldType.Date, FieldType.Number, FieldType.Boolean, FieldType.Text],
            file.Columns.Select(c => c.Guess));
        Assert.Equal(["Acme", "Brill"], file.Columns[1].Samples);
        Assert.Equal("supplier_id", CsvImportService.FieldKey("Supplier ID"));
        Assert.Equal("f_2024_total", CsvImportService.FieldKey("2024 total"));
        Assert.Equal("name_field", CsvImportService.FieldKey("Name"));
        Assert.Equal("cafe", CsvImportService.FieldKey("Café"));
    }

    [Theory]
    [InlineData("stop", "Stop")]
    [InlineData("stop_id", "Stop")]
    [InlineData("Stop ID", "Stop")]
    [InlineData("stops", "Stop")]
    [InlineData("from_stop_id", "Stop")]
    [InlineData("TO STOP", "Stop")]
    [InlineData("companies", "Company")]
    [InlineData("addresses", "Address")]
    [InlineData("stop_name", null)]
    [InlineData("id", null)]
    [InlineData("", null)]
    public void A_column_named_after_a_type_holds_its_keys(string header, string? type) =>
        Assert.Equal(type, CsvImportService.TypeForColumn(header, ["Stop", "Company", "Address"]));

    [Fact]
    public void A_file_with_two_columns_naming_types_is_guessed_as_links()
    {
        RelationshipDefinition[] model = [new("Service", "CALLS_AT", "Stop"), new("Stop", "TRANSFER_TO", "Stop"), new("Stop", "IN", "Stop")];
        string[] types = ["Stop", "Service", "Line"];

        // The only relationship between them is filled in.
        Assert.Equal(new CsvLinkGuess("Service", 0, "Stop", 1, "CALLS_AT"), CsvImportService.GuessLinks(Csv("calls.csv", "service_id,stop_id\n1,2\n"), types, model));

        // The model has it the other way round, so the ends are swapped.
        Assert.Equal(new CsvLinkGuess("Service", 1, "Stop", 0, "CALLS_AT"), CsvImportService.GuessLinks(Csv("calls.csv", "stop,service\n2,1\n"), types, model));

        // Two relationships between Stop and Stop, so it's left to choose.
        Assert.Equal(new CsvLinkGuess("Stop", 0, "Stop", 1, null), CsvImportService.GuessLinks(Csv("transfers.csv", "from_stop_id,to_stop_id,seconds\n1,2,60\n"), types, model));
        Assert.Equal(["TRANSFER_TO", "IN"], CsvImportService.RelationshipsBetween(model, "Stop", "Stop"));

        // None yet between them: both ends, and a name to type.
        Assert.Equal(new CsvLinkGuess("Line", 0, "Stop", 1, null), CsvImportService.GuessLinks(Csv("x.csv", "line,stop\n1,2\n"), types, model));

        // Only one column names a type, so it's records.
        Assert.Null(CsvImportService.GuessLinks(Csv("stops.csv", "id,name,line\n1,A,2\n"), types, model));
    }

    [Fact]
    public async Task A_file_of_records_goes_to_the_type_it_looks_like()
    {
        var (factory, importer) = await ExampleAsync();
        var types = await new RecordRepository(factory).TypesAsync();

        // Person's fields, and a key that's already a Person, whatever the file is called.
        var people = await importer.GuessTypeAsync(Csv("more-people.csv", "id;name;title;team;joined\np2;Ben;Designer;t1;2022-09-12\np9;Isla;QA;t2;2025-03-03\n"), 0, "MorePeople", types);
        Assert.Equal("Person", people!.Type);
        Assert.Equal("Looks like Person: it has Person's fields title, joined, and one of its keys is already a Person.", people.Reason);

        // Fields alone are enough when they're another type's.
        Assert.Equal("Project", (await importer.GuessTypeAsync(Csv("work.csv", "code,name,budget,status\nx1,New,100,active\n"), 0, "Work", types))!.Type);

        // So is the file's name.
        Assert.Equal("Looks like Team: the file is named after it.", (await importer.GuessTypeAsync(Csv("team.csv", "id,name\nt9,Ops\n"), 0, "Team", types))!.Reason);

        // Nothing like any type, or as much like two of them: a new type.
        Assert.Null(await importer.GuessTypeAsync(Csv("suppliers.csv", "id,name,founded\ns1,Acme,2001-04-02\n"), 0, "Supplier", types));
        Assert.Null(await importer.GuessTypeAsync(Csv("mixed.csv", "id,city,joined\nx,Leeds,2020-01-01\n"), 0, "Mixed", types));
    }

    [Theory]
    [InlineData("Stop", "STOP")]
    [InlineData("SupplierContact", "SUPPLIER_CONTACT")]
    public void Suggested_relationship_names_use_the_types_name(string type, string part) => Assert.Equal(part, CsvImportService.RelName(type));

    [Theory]
    [InlineData("name,id,city", null, 1)]
    [InlineData("name,Key,city", null, 1)]
    [InlineData("name,Supplier ID,city", "Supplier", 1)]
    [InlineData("name,ref,city", "Supplier", 0)]
    public void The_key_column_is_the_one_called_id_or_after_the_type(string header, string? type, int key) =>
        Assert.Equal(key, CsvImportService.GuessKeyColumn(Csv("x.csv", $"{header}\na,b,c\n"), type));

    [Fact]
    public void Semicolons_work_as_well_as_commas()
    {
        var file = Csv("people.csv", "id;name\n1;Ann\n");
        Assert.Equal(["id", "name"], file.Columns.Select(c => c.Header));
    }

    [Fact]
    public void An_empty_file_is_explained() =>
        Assert.Contains("is empty", Assert.Throws<LinkbaseValidationException>(() => Csv("empty.csv", "")).Message);

    [Fact]
    public async Task Imports_a_new_type_of_record_and_undoing_removes_them()
    {
        var (factory, importer) = await ExampleAsync();
        var file = Csv("suppliers.csv", "id,name,founded,staff\ns1,Acme,2001-04-02,12\ns2,Brill,1999-12-31,3\n,No key,,\ns1,Repeat,,\n");
        var mapping = new CsvRecordMapping("Supplier", 0, 1, [new CsvFieldMapping(2, "founded", FieldType.Date), new CsvFieldMapping(3, "staff", FieldType.Number)]);

        var plan = await importer.PlanAsync(file, mapping);
        Assert.Equal((4, 2, 0, 2), (plan.Rows, plan.ToAdd, plan.AlreadyThere, plan.Skipped));
        Assert.True(plan.CanImport);
        Assert.Equal(2, plan.Notes.Count);

        var result = await importer.ImportAsync(file, mapping);

        Assert.Equal(2, result.Added);
        var records = new RecordRepository(factory);
        var type = (await records.TypeAsync("Supplier"))!;
        Assert.Equal([new RecordField("founded", FieldType.Date), new RecordField("staff", FieldType.Number)], type.Fields);
        var acme = (await records.GetAsync("Supplier", "s1"))!;
        Assert.Equal(("Acme", "2001-04-02", "12"), (acme.Name, acme.Values["founded"], acme.Values["staff"]));

        // Importing the same file again adds nothing, as those keys are already here.
        var again = await importer.PlanAsync(file, mapping);
        Assert.Equal((0, 2), (again.ToAdd, again.AlreadyThere));
        Assert.False(again.CanImport);

        var batch = (await importer.RecentAsync(5)).Single();
        Assert.Equal(("suppliers.csv", "Supplier records", 2, ImportStatus.Committed), (batch.File, batch.What, batch.Added, batch.Status));

        await importer.UndoAsync(result.BatchId);
        Assert.Equal(0, (await records.ListAsync("Supplier", null, 0, 10)).Total);
        Assert.Equal(ImportStatus.Undone, (await importer.RecentAsync(5)).Single().Status);

        // The type came with the import, so it goes with it.
        Assert.Null(await records.TypeAsync("Supplier"));
        Assert.Contains("already been undone", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => importer.UndoAsync(result.BatchId))).Message);
    }

    [Fact]
    public async Task Adds_to_an_existing_type_and_leaves_records_already_there_alone()
    {
        var (factory, importer) = await ExampleAsync();
        var file = Csv("more-people.csv", "id,name,title,shoe size\np1,Changed name,Changed,\np9,Ivy Chen,Analyst,6\n");
        var mapping = new CsvRecordMapping("Person", 0, 1, [new CsvFieldMapping(2, "title", FieldType.Text), new CsvFieldMapping(3, "shoe_size", FieldType.Number)]);

        var result = await importer.ImportAsync(file, mapping);

        Assert.Equal(1, result.Added);
        var records = new RecordRepository(factory);
        Assert.Equal("Ava Lindqvist", (await records.GetAsync("Person", "p1"))!.Name);
        Assert.Equal("6", (await records.GetAsync("Person", "p9"))!.Values["shoe_size"]);
        Assert.Contains(new RecordField("shoe_size", FieldType.Number), (await records.TypeAsync("Person"))!.Fields);

        // Undoing takes away only the record it added. Person was already there, so it stays, with its new field.
        await importer.UndoAsync(result.BatchId);
        Assert.Null(await records.GetAsync("Person", "p9"));
        Assert.Equal(8, (await records.ListAsync("Person", null, 0, 50)).Total);
        Assert.Contains(new RecordField("shoe_size", FieldType.Number), (await records.TypeAsync("Person"))!.Fields);
    }

    [Theory]
    [InlineData("supplier", 2, FieldType.Date, "starting with a capital")]
    [InlineData("Supplier", 2, FieldType.Number, "isn't a number")]
    public async Task Explains_what_stops_an_import(string type, int column, FieldType fieldType, string message)
    {
        var (_, importer) = await ExampleAsync();
        var file = Csv("suppliers.csv", "id,name,founded\ns1,Acme,2001-04-02\n");
        var mapping = new CsvRecordMapping(type, 0, 1, [new CsvFieldMapping(column, "founded", fieldType)]);

        var plan = await importer.PlanAsync(file, mapping);

        Assert.False(plan.CanImport);
        Assert.Contains(plan.Problems, p => p.Contains(message, StringComparison.Ordinal));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => importer.ImportAsync(file, mapping));
    }

    [Fact]
    public async Task A_field_keeps_the_type_it_already_has()
    {
        var (_, importer) = await ExampleAsync();
        var file = Csv("people.csv", "id,joined\np9,soon\n");

        var plan = await importer.PlanAsync(file, new CsvRecordMapping("Person", 0, null, [new CsvFieldMapping(1, "joined", FieldType.Text)]));

        Assert.Contains(plan.Problems, p => p.Contains("already has joined as a date", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_choice_field_takes_its_options_from_the_column()
    {
        var (factory, importer) = await ExampleAsync();
        var file = Csv("suppliers.csv", "id,name,tier\ns1,Acme,Gold\ns2,Brill,silver\ns3,Cobb,gold\ns4,Dent,\n");
        var mapping = new CsvRecordMapping("Supplier", 0, 1, [new CsvFieldMapping(2, "tier", FieldType.Choice)]);

        var plan = await importer.PlanAsync(file, mapping);
        Assert.Contains("tier becomes a choice of Gold or silver.", plan.Notes);

        await importer.ImportAsync(file, mapping);

        var records = new RecordRepository(factory);
        Assert.Equal(["Gold", "silver"], (await records.TypeAsync("Supplier"))!.Fields.Single().Options);
        Assert.Equal("Gold", (await records.GetAsync("Supplier", "s3"))!.Values["tier"]);
        Assert.False((await records.GetAsync("Supplier", "s4"))!.Values.ContainsKey("tier"));
    }

    [Fact]
    public async Task An_existing_choice_field_keeps_its_options_and_any_capitalisation_matches()
    {
        var (factory, importer) = await ExampleAsync();
        var file = Csv("projects.csv", "id,name,status\npr8,Intranet,Proposed\npr9,Chatbot,paused\n");
        var mapping = new CsvRecordMapping("Project", 0, 1, [new CsvFieldMapping(2, "status", FieldType.Choice)]);

        var plan = await importer.PlanAsync(file, mapping);
        Assert.Equal(["Row 3: status is \"paused\", which isn't one of proposed, active, live or closed."], plan.Problems);

        await importer.ImportAsync(Csv("projects.csv", "id,name,status\npr8,Intranet,Proposed\n"), mapping);
        Assert.Equal("proposed", (await new RecordRepository(factory).GetAsync("Project", "pr8"))!.Values["status"]);
    }

    [Fact]
    public async Task A_choice_needs_a_few_different_values()
    {
        var (_, importer) = await ExampleAsync();
        var many = string.Concat(Enumerable.Range(1, 51).Select(i => $"s{i},Name {i},v{i}\n"));

        var tooMany = await importer.PlanAsync(Csv("a.csv", "id,name,tier\n" + many), new CsvRecordMapping("Supplier", 0, 1, [new CsvFieldMapping(2, "tier", FieldType.Choice)]));
        var none = await importer.PlanAsync(Csv("b.csv", "id,name,tier\ns1,Acme,\n"), new CsvRecordMapping("Supplier", 0, 1, [new CsvFieldMapping(2, "tier", FieldType.Choice)]));

        Assert.Contains(tooMany.Problems, p => p.Contains("more than 50 different values", StringComparison.Ordinal));
        Assert.Contains(none.Problems, p => p.Contains("no values to make choices from", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Imports_links_between_records_already_here_and_counts_them()
    {
        var (factory, importer) = await ExampleAsync();
        await importer.ImportAsync(Csv("suppliers.csv", "id,name\ns1,Acme\ns2,Brill\n"), new CsvRecordMapping("Supplier", 0, 1, []));
        var file = Csv("supplies.csv", "supplier,project\ns1,pr1\ns1,pr4\ns2,pr1\ns1,pr1\ns9,pr1\ns2,\n");
        var mapping = new CsvLinkMapping("Supplier", 0, "SUPPLIES", "Project", 1);

        var plan = await importer.PlanAsync(file, mapping);
        Assert.Equal((3, 0, 3), (plan.ToAdd, plan.AlreadyThere, plan.Skipped));
        Assert.Contains(plan.Notes, n => n.Contains("no Supplier s9", StringComparison.Ordinal));

        var result = await importer.ImportAsync(file, mapping);

        Assert.Equal(3, result.Added);
        var graph = new GraphRepository(factory);
        Assert.Contains(new ConnectionCount("Supplier", "SUPPLIES", "Project", 3), await graph.ConnectionCountsAsync());
        Assert.Equal(3, (await importer.PlanAsync(file, mapping)).AlreadyThere);

        // A record it added can't be taken away while something added since still links to it.
        var records = new RecordRepository(factory);
        var suppliers = (await importer.RecentAsync(5)).Single(b => b.What == "Supplier records");
        Assert.Contains("added since", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => importer.UndoAsync(suppliers.Id))).Message);

        await importer.UndoAsync(result.BatchId);
        Assert.DoesNotContain(await graph.ConnectionCountsAsync(), c => c.RelType == "SUPPLIES");
        Assert.DoesNotContain(await records.LinkOptionsAsync("Project"), o => o.RelType == "SUPPLIES");
        await importer.UndoAsync(suppliers.Id);
        Assert.Equal(0, (await records.ListAsync("Supplier", null, 0, 10)).Total);
        Assert.Null(await records.TypeAsync("Supplier"));
    }

    [Fact]
    public async Task Links_need_a_proper_name_and_types_that_exist()
    {
        var (_, importer) = await ExampleAsync();
        var file = Csv("links.csv", "a,b\np1,pr1\n");

        var plan = await importer.PlanAsync(file, new CsvLinkMapping("Person", 0, "works on", "Nope", 1));

        Assert.Contains(plan.Problems, p => p.Contains("capitals, digits and underscores", StringComparison.Ordinal));
        Assert.Contains(plan.Problems, p => p.Contains("no type called Nope", StringComparison.Ordinal));
    }

    // The demo files in models/consultancy/import-demo, as its README walks through them, so it stays true.
    [Fact]
    public async Task The_consultancy_demo_does_what_its_readme_says()
    {
        var (factory, importer) = await ExampleAsync();
        string Demo(string file) => RepoPaths.Resolve($"models/consultancy/import-demo/{file}");

        var suppliers = CsvImportService.Read(Demo("suppliers.csv"), "suppliers.csv");
        var supplierMapping = new CsvRecordMapping("Supplier", 0, 1,
            [new CsvFieldMapping(2, "founded", suppliers.Columns[2].Guess), new CsvFieldMapping(3, "staff", suppliers.Columns[3].Guess), new CsvFieldMapping(4, "approved", suppliers.Columns[4].Guess)]);
        Assert.Equal([FieldType.Date, FieldType.Number, FieldType.Boolean], supplierMapping.Fields.Select(f => f.Type));
        Assert.Equal((5, 2), ((await importer.PlanAsync(suppliers, supplierMapping)).ToAdd, (await importer.PlanAsync(suppliers, supplierMapping)).Skipped));
        await importer.ImportAsync(suppliers, supplierMapping);

        var supplies = CsvImportService.Read(Demo("supplies.csv"), "supplies.csv");
        var suppliesMapping = new CsvLinkMapping("Supplier", 0, "SUPPLIES", "Project", 1);
        var suppliesPlan = await importer.PlanAsync(supplies, suppliesMapping);
        Assert.Equal((7, 2), (suppliesPlan.ToAdd, suppliesPlan.Skipped));
        await importer.ImportAsync(supplies, suppliesMapping);

        var people = CsvImportService.Read(Demo("more-people.csv"), "more-people.csv");
        Assert.Equal(["id", "name", "title", "team", "joined", "languages"], people.Columns.Select(c => c.Header));
        var peopleMapping = new CsvRecordMapping("Person", 0, 1,
            [new CsvFieldMapping(2, "title", FieldType.Text), new CsvFieldMapping(4, "joined", FieldType.Date), new CsvFieldMapping(5, "languages", FieldType.Text)]);
        var peoplePlan = await importer.PlanAsync(people, peopleMapping);
        Assert.Equal((3, 1), (peoplePlan.ToAdd, peoplePlan.AlreadyThere));
        await importer.ImportAsync(people, peopleMapping);
        Assert.Equal(3, (await importer.ImportAsync(people, new CsvLinkMapping("Person", 0, "MEMBER_OF", "Team", 3))).Added);

        Assert.Contains(new ConnectionCount("Supplier", "SUPPLIES", "Project", 7), await new GraphRepository(factory).ConnectionCountsAsync());
    }

    [Fact]
    public async Task Only_csv_imports_can_be_undone_here()
    {
        var (_, importer) = await ExampleAsync();
        Assert.Contains("no such import", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => importer.UndoAsync(999_999))).Message);
    }

    public void Dispose()
    {
        foreach (var file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class CsvImportPageTests(PostgresFixture fixture) : UiTestBase(fixture)
{
    [Fact]
    public async Task Uploads_maps_checks_imports_and_undoes()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        Services.AddSingleton<CsvImportService>();
        var cut = RenderPage<CsvImportPage>();

        var upload = cut.FindComponent<InputFile>();
        upload.UploadFiles(InputFileContent.CreateFromText("id,name,staff\ns1,Acme,12\ns2,Brill,3\n", "suppliers.csv"));

        Sees(cut, "suppliers.csv: 2 rows, 3 columns.");
        Sees(cut, "Supplier");
        Press(cut, "Check");
        Sees(cut, "2 new record(s) to add.");

        Press(cut, "Import 2");
        Sees(cut, "Added 2 Supplier record(s).");
        Sees(cut, "Recent imports");
        Press(cut, "See them");
        NavigatedTo("/data/Supplier");
        Assert.Equal(2, (await new RecordRepository(factory).ListAsync("Supplier", null, 0, 10)).Total);

        Press(cut, "Undo");
        Press(Dialogs, "Undo");
        Told("Import undone.");
        Sees(cut, "Undone");
        Assert.Equal(0, (await new RecordRepository(factory).ListAsync("Supplier", null, 0, 10)).Total);
    }

    [Fact]
    public async Task More_of_an_existing_type_is_recognised_from_what_is_in_it()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        Services.AddSingleton<CsvImportService>();
        var cut = RenderPage<CsvImportPage>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(
            File.ReadAllText(RepoPaths.Resolve("models/consultancy/import-demo/more-people.csv")), "more-people.csv"));

        Sees(cut, "Looks like Person");
        // team holds Team keys, so it starts unticked, and its field box is disabled.
        Assert.Contains(
            cut.FindAll("tr").First(r => r.QuerySelector("input[aria-label='Import team']") is not null).QuerySelectorAll("input"),
            i => i.HasAttribute("disabled"));
        Sees(cut, "Ben Okafor");
        Press(cut, "Check");
        Sees(cut, "3 new record(s) to add.");
        Sees(cut, "1 already here, left as they are.");
    }

    [Fact]
    public async Task A_links_file_is_recognised_and_its_relationship_filled_in()
    {
        var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(RepoPaths.Resolve("models/consultancy/model.json"), TextWriter.Null);
        Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        Services.AddSingleton<CsvImportService>();
        var cut = RenderPage<CsvImportPage>();

        // Project comes first in the file, but the model has Person WORKS_ON Project, so the ends are swapped.
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("project_id,person_id\npr1,p1\npr4,p1\n", "assignments.csv"));

        Sees(cut, "assignments.csv: 2 rows, 2 columns.");
        Sees(cut, "WORKS_ON, or type a new one");
        Sees(cut, "value=\"WORKS_ON\"");
        Press(cut, "Check");
        Sees(cut, "2 new link(s) to add.");
    }
}
