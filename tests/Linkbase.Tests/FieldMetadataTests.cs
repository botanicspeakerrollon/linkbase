using Bunit;
using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

// What a field can say beyond its type (a description, a group, several values) and what a relationship can limit (a max),
// and a type's name made from its fields. Made-up people and teams throughout.
internal static class FieldMetadataModel
{
    public const string Json =
        """
        {
          "name": "Test",
          "types": [
            {
              "name": "Person",
              "source": { "file": "people.csv", "key": "id", "name": "{name_first} {name_middle} {name_last}" },
              "fields": [
                { "key": "name_first", "group": "Name", "required": true, "description": "What they are called day to day." },
                { "key": "name_middle", "group": "Name" },
                { "key": "name_last", "group": "Name", "required": true },
                { "key": "title" },
                { "key": "certified_on", "type": "date", "multiple": true, "description": "Each time they passed." },
                { "key": "scores", "type": "number", "multiple": true },
                { "key": "languages", "type": "choice", "options": [ "English", "French", "German" ], "multiple": true },
                { "key": "skills", "multiple": true }
              ]
            },
            { "name": "Team", "source": { "file": "teams.csv", "key": "id", "name": "name" } }
          ],
          "relationships": [
            { "from": "Person", "type": "MEMBER_OF", "to": "Team", "required": true, "max": 1,
              "source": { "file": "membership.csv", "fromColumn": "person", "toColumn": "team" } },
            { "from": "Person", "type": "MENTORS", "to": "Person", "max": 2 }
          ]
        }
        """;

    public const string People =
        "id,name_first,name_middle,name_last,title,certified_on,scores,languages,skills\n"
        + "p1,Zoe,,Adams,Lead,2024-03-01|2025-04-02,3|4.5,English|French,SQL|C#\n"
        + "p2,Ravi,K,Shah,,,,German,\n";

    public static readonly (string, string)[] Files =
    [
        ("people.csv", People),
        ("teams.csv", "id,name\nt1,Alpha\nt2,Beta\n"),
        ("membership.csv", "person,team\np1,t1\np2,t2\n"),
    ];
}

public sealed class FieldMetadataFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"linkbase-metadata-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Write(string json)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "model.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_model_can_describe_group_and_repeat_its_fields_and_limit_its_links()
    {
        var model = ModelFile.Read(Write(FieldMetadataModel.Json), Linkbase.Domains.RegisteredDomains.Registry);

        var person = model.Types[0];
        Assert.Equal("{name_first} {name_middle} {name_last}", person.Template);
        Assert.Equal("Name", person.Fields[0].Group);
        Assert.Equal("What they are called day to day.", person.Fields[0].Description);
        Assert.True(person.Fields.Single(f => f.Key == "scores").IsMany);
        Assert.Equal(FieldType.Number, person.Fields.Single(f => f.Key == "scores").ElementType);
        Assert.Equal(1, model.Relationships[0].Max);
    }

    [Fact]
    public void A_text_list_is_the_same_as_text_with_several_values()
    {
        var model = ModelFile.Read(Write(FieldMetadataModel.Json.Replace("\"key\": \"skills\", \"multiple\": true", "\"key\": \"skills\", \"type\": \"list\"")), Linkbase.Domains.RegisteredDomains.Registry);

        var skills = model.Types[0].Fields.Single(f => f.Key == "skills");
        Assert.True(skills.IsMany);
        Assert.Equal(FieldType.Text, skills.ElementType);
    }

    [Theory]
    [InlineData("\"key\": \"title\"", "\"key\": \"title\", \"group\": \"\"", "has a group of 1 to 40 characters")]
    [InlineData("\"key\": \"title\"", "\"key\": \"title\", \"description\": \"line\\none\"", "has a description with control characters")]
    [InlineData("\"key\": \"title\"", "\"key\": \"title\", \"type\": \"boolean\", \"multiple\": true", "is a boolean, which can't have several values")]
    [InlineData("\"max\": 1", "\"max\": 0", "has a max of 0")]
    [InlineData("{name_first} {name_middle} {name_last}", "{name_first} {nope}", "uses {nope}, which isn't a field of the type")]
    [InlineData("{name_first} {name_middle} {name_last}", "{skills}", "needs to be a field with one value")]
    [InlineData("{name_first} {name_middle} {name_last}", "{name_first} {name_last", "has a stray brace")]
    [InlineData("{name_first} {name_middle} {name_last}", "{name_first} {name_last}{", "has a stray brace")]
    public void Settings_that_make_no_sense_are_refused(string find, string replace, string expected)
    {
        var ex = Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(Write(FieldMetadataModel.Json.Replace(find, replace)), Linkbase.Domains.RegisteredDomains.Registry));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_relationships_field_cant_have_a_group()
    {
        var json = FieldMetadataModel.Json.Replace(
            "\"required\": true, \"max\": 1,", "\"required\": true, \"max\": 1, \"fields\": [ { \"key\": \"role\", \"group\": \"Who\" } ],");

        Assert.Contains("which only a type's fields can have", Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(Write(json), Linkbase.Domains.RegisteredDomains.Registry)).Message);
    }

    [Theory]
    [InlineData("{a} {b} {c}", "Zoe||Adams", "Zoe Adams")]
    [InlineData("{a}, {b}", "Zoe|K|", "Zoe, K")]
    [InlineData("{a}{b}", "||", null)]
    public void A_name_is_made_from_the_fields_it_names_and_skips_empty_ones(string template, string values, string? expected)
    {
        var parts = values.Split('|');
        var given = new Dictionary<string, string?> { ["a"] = parts[0], ["b"] = parts[1], ["c"] = parts.Length > 2 ? parts[2] : null };

        Assert.Equal(expected?.Replace(", ", ", "), NameTemplates.Render(template, given));
    }
}

[Trait("Category", "Database")]
public sealed class FieldMetadataTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"linkbase-metadata-db-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Write(string json, params (string Name, string Content)[] files)
    {
        Directory.CreateDirectory(_folder);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(_folder, name), content);
        }

        var path = Path.Combine(_folder, "model.json");
        File.WriteAllText(path, json);
        return path;
    }

    private async Task<(ModelFactory Factory, RecordRepository Records)> LoadedAsync()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, Write(FieldMetadataModel.Json, FieldMetadataModel.Files), TextWriter.Null);
        return (factory, new RecordRepository(factory));
    }

    [Fact]
    public async Task Loading_keeps_descriptions_groups_and_what_a_name_is_made_from()
    {
        var (_, records) = await LoadedAsync();

        var person = (await records.TypeAsync("Person"))!;

        Assert.Equal("{name_first} {name_middle} {name_last}", person.NameTemplate);
        var first = person.Fields.Single(f => f.Key == "name_first");
        Assert.Equal("Name", first.Group);
        Assert.Equal("What they are called day to day.", first.Description);
        Assert.Equal(["Name", "Name", "Name"], person.Fields.Take(3).Select(f => f.Group));
        Assert.Null(person.Fields.Single(f => f.Key == "title").Group);
        Assert.Equal(("Zoe Adams", "Ravi K Shah"),
            ((await records.GetAsync("Person", "p1"))!.Name, (await records.GetAsync("Person", "p2"))!.Name));
        Assert.Equal([new LinkLimit("MEMBER_OF", "Team", 1), new LinkLimit("MENTORS", "Person", 2)], person.LinkLimits.OrderBy(l => l.RelType));
    }

    [Fact]
    public async Task Loading_stores_each_value_of_a_several_valued_field_in_its_own_type()
    {
        var (_, records) = await LoadedAsync();

        var zoe = (await records.GetAsync("Person", "p1"))!;

        Assert.Equal("2024-03-01\n2025-04-02", zoe.Values["certified_on"]);
        Assert.Equal("3\n4.5", zoe.Values["scores"]);
        Assert.Equal("English\nFrench", zoe.Values["languages"]);
        Assert.Equal("SQL\nC#", zoe.Values["skills"]);
        var type = (await records.TypeAsync("Person"))!;
        Assert.True(type.Fields.Single(f => f.Key == "certified_on").Multiple);
        Assert.Equal(FieldType.Date, type.Fields.Single(f => f.Key == "certified_on").Type);
        Assert.Equal(FieldType.List, type.Fields.Single(f => f.Key == "skills").Type);
    }

    [Fact]
    public async Task A_value_that_doesnt_fit_one_of_the_several_stops_the_load()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var people = FieldMetadataModel.People.Replace("English|French", "English|Klingon");
        var path = Write(FieldMetadataModel.Json, [("people.csv", people), .. FieldMetadataModel.Files[1..]]);

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => TestModels.LoadAsync(factory, path, TextWriter.Null));

        Assert.Contains("\"Klingon\"", ex.Message);
    }

    [Fact]
    public async Task Loading_refuses_a_record_with_more_links_than_the_max()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var path = Write(FieldMetadataModel.Json, [.. FieldMetadataModel.Files[..2], ("membership.csv", "person,team\np1,t1\np1,t2\np2,t2\n")]);

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => TestModels.LoadAsync(factory, path, TextWriter.Null));

        Assert.Contains("at most 1 Person MEMBER_OF Team link(s), but 1 have more, such as p1", ex.Message);
    }

    [Fact]
    public async Task A_new_record_gets_its_name_from_its_fields_and_ignores_one_typed()
    {
        var (_, records) = await LoadedAsync();
        var input = new RecordInput("Ignored", new Dictionary<string, string?> { ["name_first"] = "Mina", ["name_last"] = "Okoye" })
        {
            Links = [new NewLink("MEMBER_OF", "team:t1")],
        };

        var key = await records.CreateAsync("Person", null, input);

        Assert.Equal("Mina Okoye", (await records.GetAsync("Person", key))!.Name);
        Assert.Equal("mina-okoye", key);
    }

    [Fact]
    public async Task A_name_made_from_fields_needs_one_of_them_and_follows_edits()
    {
        var (_, records) = await LoadedAsync();
        var empty = new RecordInput("", new Dictionary<string, string?>()) { Links = [new NewLink("MEMBER_OF", "team:t1")] };

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Person", "x", empty));
        Assert.Contains("to make its name", ex.Message);

        await records.UpdateAsync("person:p2", new RecordInput("", new Dictionary<string, string?> { ["name_first"] = "Ravi", ["name_last"] = "Patel" }));
        Assert.Equal("Ravi Patel", (await records.GetAsync("Person", "p2"))!.Name);
    }

    [Fact]
    public async Task Several_values_are_checked_one_by_one_and_stored_typed()
    {
        var (_, records) = await LoadedAsync();
        RecordInput Person(string dates = "", string scores = "", string languages = "") => new("", new Dictionary<string, string?>
        {
            ["name_first"] = "Ana",
            ["name_last"] = "Lopez",
            ["certified_on"] = dates,
            ["scores"] = scores,
            ["languages"] = languages,
        })
        { Links = [new NewLink("MEMBER_OF", "team:t1")] };

        var key = await records.CreateAsync("Person", null, Person("2024-01-02\n2025-02-03", "7\n8.5", "French\ngerman"));
        var saved = (await records.GetAsync("Person", key))!.Values;
        Assert.Equal("2024-01-02\n2025-02-03", saved["certified_on"]);
        Assert.Equal("7\n8.5", saved["scores"]);
        Assert.Equal("French\nGerman", saved["languages"]);

        Assert.Contains("must be a date", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Person", "b1", Person("2024-01-02\nlater")))).Message);
        Assert.Contains("must be a number", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Person", "b2", Person(scores: "1\ntwo")))).Message);
        Assert.Contains("must be one of", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Person", "b3", Person(languages: "Klingon")))).Message);
    }

    [Fact]
    public async Task Filters_on_several_values_match_when_any_does()
    {
        var (_, records) = await LoadedAsync();

        async Task<string[]> Keys(string field, FilterOp op, string value) =>
            (await records.ListAsync("Person", null, null, [new RecordFilter(field, op, value)], 0, 20)).Rows.Select(r => r.Key).Order().ToArray();

        Assert.Equal(["p1"], await Keys("languages", FilterOp.Is, "French"));
        Assert.Equal(["p1"], await Keys("languages", FilterOp.Is, "english"));
        Assert.Equal(["p2"], await Keys("languages", FilterOp.Is, "German"));
        Assert.Equal(["p1"], await Keys("scores", FilterOp.AtLeast, "4"));
        Assert.Equal(["p1"], await Keys("scores", FilterOp.AtMost, "3"));
        Assert.Equal(["p1"], await Keys("certified_on", FilterOp.AtLeast, "2025-01-01"));
        Assert.Empty(await Keys("certified_on", FilterOp.AtLeast, "2026-01-01"));
        Assert.Equal(["p1"], await Keys("skills", FilterOp.Contains, "c#"));
    }

    [Fact]
    public async Task A_link_over_the_max_is_refused_when_adding_and_when_creating()
    {
        var (_, records) = await LoadedAsync();

        // p1 is in Alpha already, so a second team is one too many.
        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.AddLinkAsync("person:p1", "MEMBER_OF", "team:t2"));
        Assert.Contains("at most 1 Person MEMBER_OF Team link(s)", ex.Message);
        Assert.Contains("Remove one first", ex.Message);

        var input = new RecordInput("", new Dictionary<string, string?> { ["name_first"] = "Ana", ["name_last"] = "Lopez" })
        {
            Links = [new NewLink("MEMBER_OF", "team:t1"), new NewLink("MEMBER_OF", "team:t2")],
        };
        Assert.Contains("at most 1", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.CreateAsync("Person", null, input))).Message);

        // Two mentors are fine, a third isn't. Removing a link makes room.
        await records.AddLinkAsync("person:p1", "MENTORS", "person:p2");
        var key = await records.CreateAsync("Person", null, new RecordInput("", new Dictionary<string, string?> { ["name_first"] = "Ana", ["name_last"] = "Lopez" })
        {
            Links = [new NewLink("MEMBER_OF", "team:t1")],
        });
        await records.AddLinkAsync("person:p1", "MENTORS", $"person:{key}");
        Assert.Contains("at most 2", (await Assert.ThrowsAsync<LinkbaseValidationException>(() => records.AddLinkAsync("person:p1", "MENTORS", "person:p1"))).Message);
        var first = (await records.LinksAsync("person:p1")).Single(g => g.RelType == "MENTORS").Links[0];
        await records.RemoveLinkAsync(first.Id);
        await records.AddLinkAsync("person:p1", "MENTORS", "person:p1");
    }
}

// A CSV file fills a field of several dates the way a model file does: split by |, each checked on its own.
[Trait("Category", "Database")]
public sealed class FieldMetadataImportTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"linkbase-metadata-csv-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_csv_file_can_fill_a_field_of_several_dates_and_says_which_one_is_wrong()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "courses.csv"), "id,name,held_on\nc1,Rope work,\n");
        File.WriteAllText(Path.Combine(_folder, "model.json"),
            """
            { "name": "Test", "types": [ { "name": "Course", "source": { "file": "courses.csv", "key": "id", "name": "name" },
              "fields": [ { "key": "held_on", "type": "date", "multiple": true } ] } ] }
            """);
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(factory, Path.Combine(_folder, "model.json"), TextWriter.Null);
        var importer = new Linkbase.Core.CsvImport.CsvImportService(factory);
        var mapping = new Linkbase.Core.CsvImport.CsvRecordMapping("Course", 0, 1, [new Linkbase.Core.CsvImport.CsvFieldMapping(2, "held_on", FieldType.Date)]);

        string Csv(string content)
        {
            var path = Path.Combine(_folder, $"{Guid.NewGuid():N}.csv");
            File.WriteAllText(path, content);
            return path;
        }

        var bad = Linkbase.Core.CsvImport.CsvImportService.Read(Csv("id,name,held_on\nc2,Knots,2024-01-02|soon\n"), "bad.csv");
        var refused = await importer.PlanAsync(bad, mapping);
        Assert.False(refused.CanImport);
        Assert.Contains(refused.Problems, p => p.Contains("held_on has \"soon\"", StringComparison.Ordinal));

        var good = Linkbase.Core.CsvImport.CsvImportService.Read(Csv("id,name,held_on\nc2,Knots,2024-01-02|2025-03-04\nc3,Belay,2026-05-06\n"), "good.csv");
        await importer.ImportAsync(good, mapping);

        var records = new RecordRepository(factory);
        Assert.Equal("2024-01-02\n2025-03-04", (await records.GetAsync("Course", "c2"))!.Values["held_on"]);

        // A single date is still a list of one, so filtering on the field finds both.
        var found = await records.ListAsync("Course", null, null, [new RecordFilter("held_on", FilterOp.AtLeast, "2025-01-01")], 0, 10);
        Assert.Equal(["c2", "c3"], found.Rows.Select(r => r.Key).Order());
    }
}

// The pages and forms show a field's description and group, and a type with a name template doesn't ask for a name.
[Trait("Category", "Database")]
public sealed class FieldMetadataPageTests(PostgresFixture fixture) : UiTestBase(fixture), IClassFixture<PostgresFixture>
{
    private async Task UseModelAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"linkbase-metadata-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var (name, content) in FieldMetadataModel.Files)
            {
                File.WriteAllText(Path.Combine(folder, name), content);
            }

            File.WriteAllText(Path.Combine(folder, "model.json"), FieldMetadataModel.Json);
            var factory = new ModelFactory(await Fixture.CreateModelDatabaseAsync("SELECT 1"));
            await TestModels.LoadAsync(factory, Path.Combine(folder, "model.json"), TextWriter.Null);
            Services.AddSingleton<IDbContextFactory<LinkbaseDbContext>>(factory);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_record_page_shows_descriptions_under_group_headings_and_lists_every_value()
    {
        await UseModelAsync();

        var cut = RenderPage<RecordPage>(p => p.Add(x => x.Type, "Person").Add(x => x.Key, "p1"));

        Sees(cut, "Zoe Adams");
        Sees(cut, "What they are called day to day.");
        Sees(cut, "Each time they passed.");
        Assert.Single(cut.FindAll("th.group-heading"));
        Assert.Equal("Name", cut.Find("th.group-heading").TextContent.Trim());
        Sees(cut, "2024-03-01, 2025-04-02");
        Sees(cut, "English, French");
    }

    [Fact]
    public async Task The_form_has_no_name_box_when_the_name_is_made_from_fields_and_shows_the_hints()
    {
        await UseModelAsync();
        var cut = RenderPage<RecordListPage>(p => p.Add(x => x.Type, "Person"));

        Press(cut, "Add Person");

        Sees(Dialogs, "Its name is made from Name first, Name middle, Name last.");
        Sees(Dialogs, "What they are called day to day.");
        Sees(Dialogs, "One date per line, as yyyy-mm-dd.");
        Assert.DoesNotContain(Dialogs.FindAll("label"), l => l.TextContent.Trim() == "Name");
    }
}
