using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Core.Staging;
using Linkbase.Data;
using Npgsql;

namespace Linkbase.Tests;

// A type's hierarchy: what the model file may say, and that loading, the record forms and committing an import follow it.
// The demo climbing logbook is the working example: countries, regions in countries, and areas in regions or areas.
[Trait("Category", "Database")]
public sealed class HierarchyTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private static readonly string Demo = RepoPaths.Resolve("models/climbing-demo/model.json");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"linkbase-hierarchy-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private const string Levels =
        """{ "kind": "top", "root": true }, { "kind": "mid", "under": [ "top" ] }, { "kind": "low", "under": [ "mid", "low" ] }""";

    private static string Model(string levels = Levels, string by = "kind", string via = "IN", bool required = false, bool kindRequired = true) =>
        $$"""
        {
          "name": "Test",
          "types": [ {
            "name": "Area", "source": { "file": "areas.csv", "key": "id", "name": "name" },
            "fields": [ { "key": "kind", "type": "choice", "options": [ "top", "mid", "low" ], "required": {{kindRequired.ToString().ToLowerInvariant()}} } ],
            "hierarchy": { "by": "{{by}}", "via": "{{via}}", "levels": [ {{levels}} ] }
          } ],
          "relationships": [
            { "from": "Area", "type": "IN", "to": "Area", "required": {{required.ToString().ToLowerInvariant()}}, "source": { "file": "areas.csv", "fromColumn": "id", "toColumn": "parent" } },
            { "from": "Area", "type": "IN", "to": "Area", "source": { "file": "extra.csv", "fromColumn": "id", "toColumn": "parent" } }
          ]
        }
        """;

    private string Write(string model, string areas = "id,name,kind,parent\n", string extra = "id,parent\n")
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "areas.csv"), areas);
        File.WriteAllText(Path.Combine(_folder, "extra.csv"), extra);
        var path = Path.Combine(_folder, "model.json");
        File.WriteAllText(path, model);
        return path;
    }

    [Fact]
    public void A_valid_hierarchy_is_read()
    {
        var model = ModelFile.Read(Write(Model()), Linkbase.Domains.RegisteredDomains.Registry);

        var rules = model.Types.Single().Hierarchy!.ToRules("Area");
        Assert.Equal(["top", "mid", "low"], rules.Levels.Select(l => l.Kind));
        Assert.True(rules.Level("top")!.Root);
        Assert.Equal(["mid", "low"], rules.Level("low")!.Under);
    }

    [Theory]
    [InlineData("""{ "kind": "mid", "under": [ "top" ] }, { "kind": "top", "under": [ "low" ] }, { "kind": "low", "under": [ "mid" ] }""", "needs a level with \"root\": true")]
    [InlineData("""{ "kind": "top", "root": true, "under": [ "mid" ] }, { "kind": "mid", "under": [ "top" ] }, { "kind": "low", "under": [ "mid" ] }""", "is a root, so it can't be under anything")]
    [InlineData("""{ "kind": "top", "root": true }, { "kind": "mid" }, { "kind": "low", "under": [ "mid" ] }""", "level 'mid' needs \"under\"")]
    [InlineData("""{ "kind": "top", "root": true }, { "kind": "mid", "under": [ "top" ] }""", "has no level for 'kind' option 'low'")]
    [InlineData("""{ "kind": "top", "root": true }, { "kind": "mid", "under": [ "top" ] }, { "kind": "low", "under": [ "mid" ] }, { "kind": "huge", "under": [ "top" ] }""", "level 'huge', which isn't an option of 'kind'")]
    [InlineData("""{ "kind": "top", "root": true }, { "kind": "mid", "under": [ "top" ] }, { "kind": "low", "under": [ "tall" ] }""", "is under 'tall', which isn't a level")]
    [InlineData("""{ "kind": "top", "root": true }, { "kind": "mid", "under": [ "low" ] }, { "kind": "low", "under": [ "mid" ] }""", "can't be reached from a root")]
    [InlineData("""{ "kind": "top", "root": true }, { "kind": "top", "root": true }, { "kind": "mid", "under": [ "top" ] }, { "kind": "low", "under": [ "mid" ] }""", "level 'top' more than once")]
    [InlineData("", "needs levels")]
    public void A_hierarchy_that_doesnt_add_up_is_refused(string levels, string expected)
    {
        var ex = Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(Write(Model(levels)), Linkbase.Domains.RegisteredDomains.Registry));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void The_field_and_relationship_have_to_exist_and_suit()
    {
        Assert.Contains("needs to be a choice field", Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(Write(Model(by: "nope")), Linkbase.Domains.RegisteredDomains.Registry)).Message);
        Assert.Contains("needs to be required", Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(Write(Model(kindRequired: false)), Linkbase.Domains.RegisteredDomains.Registry)).Message);
        Assert.Contains("the model has no relationship Area OUT Area", Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(Write(Model(via: "OUT")), Linkbase.Domains.RegisteredDomains.Registry)).Message);
        Assert.Contains("can't be required", Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(Write(Model(required: true)), Linkbase.Domains.RegisteredDomains.Registry)).Message);
    }

    [Theory]
    [InlineData("id,name,kind,parent\na,A,top,b\nb,B,top,\n", "", "at the top level are in another Area, such as a")]
    [InlineData("id,name,kind,parent\na,A,top,\nb,B,low,a\n", "", "of a level they can't be in, such as b")]
    [InlineData("id,name,kind,parent\na,A,top,\nb,B,mid,\n", "", "aren't in any Area, such as b")]
    [InlineData("id,name,kind,parent\na,A,top,\nb,B,top,\nm,M,mid,a\n", "id,parent\nm,b\n", "in more than one Area, such as m")]
    [InlineData("id,name,kind,parent\na,A,top,\nb,B,low,c\nc,C,low,b\n", "", "in themselves through the IN links, such as b, c")]
    public async Task Loading_refuses_data_that_breaks_the_hierarchy(string areas, string extra, string expected)
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var path = Write(Model(), areas, extra.Length == 0 ? "id,parent\n" : extra);

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => TestModels.LoadAsync(factory, path, TextWriter.Null));

        Assert.Contains("The Area hierarchy isn't followed", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Loading_accepts_a_tree_and_keeps_the_rules()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var path = Write(Model(), "id,name,kind,parent\na,A,top,\nb,B,mid,a\nc,C,low,b\nd,D,low,c\n");

        await TestModels.LoadAsync(factory, path, TextWriter.Null);

        var area = (await new RecordRepository(factory).TypeAsync("Area"))!;
        Assert.Equal("kind", area.Hierarchy!.FieldKey);
        Assert.Equal("IN", area.Hierarchy.RelType);
        Assert.Equal(["top", "mid", "low"], area.Hierarchy.Levels.Select(l => l.Kind));
        Assert.Equal(["mid", "low"], area.Hierarchy.Level("low")!.Under);
    }

    [Fact]
    public async Task The_demo_logbook_loads_with_its_hierarchy()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, Demo, TextWriter.Null);

        var area = (await new RecordRepository(factory).TypeAsync("Area"))!;

        Assert.Equal(["country", "region", "area"], area.Hierarchy!.Levels.Select(l => l.Kind));
        Assert.Equal(["region", "area"], area.Hierarchy.Level("area")!.Under);
        Assert.Null((await new RecordRepository(factory).TypeAsync("Crag"))!.Hierarchy);
    }

    private async Task<RecordRepository> DemoAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, Demo, TextWriter.Null);
        return new RecordRepository(factory);
    }

    private static RecordInput Input(string name, string kind, params string[] parents) =>
        new(name, new Dictionary<string, string?> { ["kind"] = kind }) { Links = parents.Select(p => new NewLink("IN", p)).ToList() };

    private static async Task<string> RefusedAsync(Task task) => (await Assert.ThrowsAsync<LinkbaseValidationException>(() => task)).Message;

    [Fact]
    public async Task A_new_record_needs_a_parent_of_a_level_it_may_be_in()
    {
        var records = await DemoAsync();

        Assert.Contains("A region needs a country to be in", await RefusedAsync(records.CreateAsync("Area", null, Input("Nowhere", "region"))));
        Assert.Contains("A region can only be in a country, not a region", await RefusedAsync(records.CreateAsync("Area", null, Input("Odd", "region", "area:peak"))));
        Assert.Contains("A country is at the top", await RefusedAsync(records.CreateAsync("Area", null, Input("Odd", "country", "area:gb"))));
        Assert.Contains("only one Area", await RefusedAsync(records.CreateAsync("Area", null, Input("Odd", "region", "area:gb", "area:fr"))));

        // Nothing was left behind by the refusals.
        Assert.Equal(0, (await records.ListAsync("Area", "Odd", 0, 10)).Total + (await records.ListAsync("Area", "Nowhere", 0, 10)).Total);

        await records.CreateAsync("Area", "atlantis", Input("Atlantis", "country"));
        await records.CreateAsync("Area", "deep", Input("Deep", "region", "area:atlantis"));
        await records.CreateAsync("Area", "trench", Input("Trench", "area", "area:deep"));
        await records.CreateAsync("Area", "ledge", Input("Ledge", "area", "area:trench"));
        Assert.NotNull(await records.GetAsync("Area", "ledge"));
    }

    [Fact]
    public async Task A_parent_created_with_the_record_is_checked_too()
    {
        var records = await DemoAsync();
        var bad = Input("Ledge", "area") with
        {
            Creates = [new NewLinkedRecord("IN", "Area", "nowhere", Input("Nowhere", "region"))],
        };

        Assert.Contains("A region needs a country", await RefusedAsync(records.CreateAsync("Area", null, bad)));
        Assert.Null(await records.GetAsync("Area", "nowhere"));
    }

    [Fact]
    public async Task A_record_has_one_parent_that_can_be_moved_and_never_circles()
    {
        var records = await DemoAsync();

        // Stanage is in the Peak District already.
        Assert.Contains("is in a Area already", await RefusedAsync(records.AddLinkAsync("area:stan", "IN", "area:lakes")));
        Assert.Contains("A record can't be in itself", await RefusedAsync(records.AddLinkAsync("area:stan", "IN", "area:stan")));
        Assert.Contains("A country is at the top", await RefusedAsync(records.AddLinkAsync("area:gb", "IN", "area:fr")));
        Assert.Contains("A region can only be in a country", await RefusedAsync(records.AddLinkAsync("area:peak", "IN", "area:lakes")));

        // To move it, take it out of the first and put it in the second.
        var current = (await records.LinksAsync("area:stan")).Single(g => g.RelType == "IN" && g.Outgoing).Links.Single();
        Assert.Equal("area:peak", current.OtherId);
        await records.RemoveLinkAsync(current.Id);
        await records.AddLinkAsync("area:stan", "IN", "area:lakes");
        Assert.Equal("area:lakes", (await records.LinksAsync("area:stan")).Single(g => g.RelType == "IN" && g.Outgoing).Links.Single().OtherId);

        // Sub-areas nest, but not round in a circle.
        await records.CreateAsync("Area", "outer", Input("Outer", "area", "area:lakes"));
        await records.CreateAsync("Area", "inner", Input("Inner", "area", "area:outer"));
        await records.RemoveLinkAsync((await records.LinksAsync("area:outer")).Single(g => g.RelType == "IN" && g.Outgoing).Links.Single().Id);
        Assert.Contains("would make a circle", await RefusedAsync(records.AddLinkAsync("area:outer", "IN", "area:inner")));
    }

    [Fact]
    public async Task Changing_a_level_has_to_suit_what_its_in_and_whats_in_it()
    {
        var records = await DemoAsync();
        RecordInput Edit(string name, string kind) => new(name, new Dictionary<string, string?> { ["kind"] = kind });

        // The Peak District is a region with areas in it, and can't become an area, because it's in a country.
        Assert.Contains("because it's in a country", await RefusedAsync(records.UpdateAsync("area:peak", Edit("Peak District", "area"))));

        // Stanage is an area in a region, so it can't become a country there, or a region with areas that can't be in one.
        Assert.Contains("A country is at the top", await RefusedAsync(records.UpdateAsync("area:stan", Edit("Stanage", "country"))));

        // A region that has been taken out of its country, to move it, has areas in it that can't be in a country.
        await records.RemoveLinkAsync((await records.LinksAsync("area:peak")).Single(g => g.RelType == "IN" && g.Outgoing).Links.Single().Id);
        Assert.Contains("because there are area records in it", await RefusedAsync(records.UpdateAsync("area:peak", Edit("Peak District", "country"))));

        // Renaming, and a change that fits, are fine.
        await records.UpdateAsync("area:peak", Edit("Peak District National Park", "region"));
        await records.UpdateAsync("area:peak", Edit("Peak District National Park", "area"));
        Assert.Equal("area", (await records.GetAsync("Area", "peak"))!.Values["kind"]);
    }

    private static StagedRecord Staged(string key, string name, string kind) => new("Area", key, name, $$"""{"kind":"{{kind}}"}""");

    private static StagedLink InArea(string child, string parent) => new("Area", "IN", $"area:{child}", "Area", $"area:{parent}");

    private static StagingRequest Request() => new(DataSource.Csv, "areas.csv", "areas", ModelChanges.None);

    [Fact]
    public async Task Committing_an_import_follows_the_hierarchy()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, Demo, TextWriter.Null);
        var records = new RecordRepository(factory);
        var staging = new ImportStagingService(factory);

        // A region put in a region is refused, the commit rolls back, and the batch is still there to untick it from.
        var bad = await staging.StageAsync(Request(), [Staged("rr", "Odd", "region")], [InArea("rr", "peak")]);
        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => staging.CommitAsync(bad));
        Assert.Contains("The Area hierarchy isn't followed", ex.Message);
        Assert.Contains("Untick", ex.Message);
        Assert.Null(await records.GetAsync("Area", "rr"));

        // A second parent for an area that has one is refused as well.
        var two = await staging.StageAsync(Request(), [], [InArea("stan", "lakes")]);
        Assert.Contains("in more than one Area, such as stan", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => staging.CommitAsync(two))).Message);

        // Records that follow it go in, and so does an import of records alone, whose parents come later.
        var good = await staging.StageAsync(Request(), [Staged("fresh", "Fresh Edge", "area")], [InArea("fresh", "peak")]);
        Assert.Equal(1, (await staging.CommitAsync(good)).Records);
        var loose = await staging.StageAsync(Request(), [Staged("loose", "Loose Region", "region")], []);
        await staging.CommitAsync(loose);
        Assert.NotNull(await records.GetAsync("Area", "fresh"));
        Assert.NotNull(await records.GetAsync("Area", "loose"));

        // Something wrong that was there already doesn't stop an unrelated import.
        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("INSERT INTO node_link (source_id, rel_type, target_id) VALUES ('area:peak', 'IN', 'area:lakes')", connection);
            await command.ExecuteNonQueryAsync();
        }

        var unrelated = await staging.StageAsync(Request(), [Staged("more", "More Edge", "area")], [InArea("more", "fresh")]);
        await staging.CommitAsync(unrelated);
        Assert.NotNull(await records.GetAsync("Area", "more"));
    }
}
