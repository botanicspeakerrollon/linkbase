using Npgsql;
using Linkbase.Core;
using Linkbase.Core.Graph;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
public sealed class ModelLoaderTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private static readonly string Example = RepoPaths.Resolve("models/consultancy/model.json");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"linkbase-model-{Guid.NewGuid():N}");

    private sealed class Factory(string connectionString) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
    }

    private async Task<Factory> NewDatabaseAsync() => new(await fixture.CreateModelDatabaseAsync("SELECT 1"));

    private string WriteModel(string json, params (string Name, string Content)[] files)
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

    [Fact]
    public async Task Loads_the_demo_climbing_logbook()
    {
        var factory = await NewDatabaseAsync();

        var summary = await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/climbing-demo/model.json"), TextWriter.Null);

        Assert.Equal(6, summary.Types);
        Assert.Equal(2277, summary.Nodes);
        Assert.Equal(4920, summary.Links);
    }

    [Fact]
    public async Task Loads_the_model_of_this_code_base()
    {
        var factory = await NewDatabaseAsync();

        var summary = await TestModels.LoadAsync(factory, RepoPaths.Resolve("models/linkbase-code/model.json"), TextWriter.Null);

        // The files are made from whatever is tracked, so the counts change with the code. It has to load and be joined up.
        Assert.Equal(4, summary.Types);
        Assert.True(summary.Nodes > 300);
        Assert.True(summary.Links > summary.Nodes);
    }

    [Fact]
    public async Task Loads_the_example_model_and_explore_sees_it()
    {
        var factory = await NewDatabaseAsync();
        var output = new StringWriter();

        var summary = await TestModels.LoadAsync(factory, Example, output);

        Assert.Equal(4, summary.Types);
        Assert.Equal(20, summary.Nodes);
        Assert.Equal(26, summary.Links);
        Assert.Contains("Person: 8 from people.csv", output.ToString());

        var graph = new GraphRepository(factory);
        var schema = await graph.SchemaAsync();
        Assert.Equal(["Person", "Project", "Client", "Team"], schema.Labels);
        Assert.Contains(new GraphConnection("Person", "WORKS_ON", "Project"), schema.Connections);
        Assert.Contains(new GraphPropertyInfo("Project", "budget", GraphValueType.Number), schema.Properties);
        Assert.Contains(new GraphTypeInfo("Team", "#4338ca", "Groups"), schema.Types);
        var status = schema.Properties.Single(p => p.Label == "Project" && p.Key == "status");
        Assert.Equal(new GraphPropertyInfo("Project", "status", GraphValueType.Choice) { Options = ["proposed", "active", "live", "closed"] }, status);

        Assert.Contains(new GraphPropertyInfo("Person", "skills", GraphValueType.List), schema.Properties);

        // A list matches when any of its values does.
        GraphPattern People(FilterOperator op, string value) =>
            new([new PatternNode("Person", [new PatternFilter("skills", op, value)])], []);
        Assert.Equal(["person:p3", "person:p4", "person:p7"], (await graph.RunPatternAsync(People(FilterOperator.Equals, "postgres"), schema, 10, 0)).NodeIds.Order());
        Assert.Equal(["person:p1", "person:p2"], (await graph.RunPatternAsync(People(FilterOperator.Contains, "fig"), schema, 10, 0)).NodeIds.Order());
        Assert.Empty((await graph.RunPatternAsync(People(FilterOperator.Equals, "post"), schema, 10, 0)).NodeIds);
        Assert.Equal(["Postgres"], await graph.PropertyValuesAsync("Person", "skills", "post", list: true));
        Assert.Contains(new PropertyMatch("Person", "skills", "User research"), await graph.MatchPropertiesAsync(null, "research"));

        var dev = (await graph.SubgraphAsync(["person:p4"])).Nodes.Single();
        Assert.Equal("Dev Raman", dev.Name);
        Assert.Equal("2021-06-01", dev.Property("joined"));
        Assert.Equal("C#, Postgres", dev.Property("skills"));

        var projects = await graph.NeighboursAsync("person:p4", new GraphFilter { Properties = [new PatternFilter("budget", FilterOperator.GreaterThan, "100000")] }, [], 10);
        Assert.Equal(["Patient portal", "Engineering"], projects.Nodes.Select(n => n.Name).Order().Reverse());

        var found = await graph.RunPatternAsync(new GraphPattern([new PatternNode(null, [new PatternFilter(PatternFilter.AnyField, FilterOperator.Contains, "Leeds")])], []), schema, 10, 0);
        Assert.Equal(["client:c2"], found.NodeIds);

        // The loader counts each kind of link as it goes, most first.
        Assert.Equal(
            [new ConnectionCount("Person", "WORKS_ON", "Project", 13), new ConnectionCount("Person", "MEMBER_OF", "Team", 8), new ConnectionCount("Project", "FOR", "Client", 5)],
            await graph.ConnectionCountsAsync());
    }

    [Fact]
    public async Task Loading_again_replaces_the_model_and_keeps_ids()
    {
        var factory = await NewDatabaseAsync();
        await TestModels.LoadAsync(factory, Example, TextWriter.Null);
        var path = WriteModel(
            """{ "name": "Small", "types": [{ "name": "Team", "source": { "file": "teams.csv", "key": "id", "name": "name" } }] }""",
            ("teams.csv", "id,name\nt1,Design\nt9,New team\n"));

        var summary = await TestModels.LoadAsync(factory, path, TextWriter.Null);

        Assert.Equal(2, summary.Nodes);
        await using var db = factory.CreateDbContext();
        Assert.Equal(["team:t1", "team:t9"], await db.Nodes.OrderBy(n => n.Id).Select(n => n.Id).ToListAsync());
        Assert.Equal(["Team"], await db.NodeTypes.Select(t => t.Name).ToListAsync());
        Assert.Empty(await db.NodeLinks.ToListAsync());
        Assert.NotEmpty(await db.Database.SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_node_search_text'").ToListAsync());
    }

    [Fact]
    public async Task Skips_empty_keys_keeps_the_first_of_a_repeated_key_and_drops_links_to_missing_nodes()
    {
        var factory = await NewDatabaseAsync();
        var path = WriteModel(
            """
            {
              "name": "Edges",
              "types": [{ "name": "Thing", "source": { "file": "things.csv", "key": "id" } }],
              "relationships": [{ "from": "Thing", "type": "NEXT", "to": "Thing", "source": { "file": "next.csv", "fromColumn": "a", "toColumn": "b" } }]
            }
            """,
            ("things.csv", "id\n1\n1\n\n2\n"),
            ("next.csv", "a,b\n1,2\n2,99\n"));

        var summary = await TestModels.LoadAsync(factory, path, TextWriter.Null);

        Assert.Equal(2, summary.Nodes);
        Assert.Equal(1, summary.Links);
        await using var db = factory.CreateDbContext();
        Assert.Equal("1", await db.Nodes.Where(n => n.Id == "thing:1").Select(n => n.Name).SingleAsync());
    }

    [Theory]
    [InlineData("""{ "name": "", "types": [] }""", "needs a name")]
    [InlineData("""{ "name": "X", "types": [] }""", "at least one type")]
    [InlineData("""{ "name": "X", "types": [{ "name": "lower", "source": { "file": "a.csv", "key": "id" } }] }""", "starting with a capital")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "id" }, "fields": [{ "key": "Bad Key" }] }] }""", "lower case letters")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "id" } }], "relationships": [{ "from": "A", "type": "to", "to": "B", "source": { "file": "a.csv", "fromColumn": "id", "toColumn": "id" } }] }""", "isn't in the model")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "id" } }, { "name": "A", "source": { "file": "a.csv", "key": "id" } }] }""", "more than once")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "nope" } }] }""", "has no column 'nope'")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "missing.csv", "key": "id" } }] }""", "isn't in")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "id" }, "fields": [{ "key": "n", "type": "number", "column": "word" }] }] }""", "Check that field's type")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "id" }, "fields": [{ "key": "word", "type": "choice" }] }] }""", "needs between 1 and 50 options")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "id" }, "fields": [{ "key": "word", "type": "choice", "options": ["hi", "Hi"] }] }] }""", "the option 'hi' more than once")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "id" }, "fields": [{ "key": "word", "options": ["hello"] }] }] }""", "only a choice field can have")]
    [InlineData("""{ "name": "X", "types": [{ "name": "A", "source": { "file": "a.csv", "key": "id" }, "fields": [{ "key": "word", "type": "choice", "options": ["Hello", "Bye"] }] }] }""", "word has \"hello\", which isn't one of its options: Hello or Bye")]
    [InlineData("not json", "isn't a valid model file")]
    public async Task Explains_what_is_wrong_and_changes_nothing(string json, string message)
    {
        var factory = await NewDatabaseAsync();
        await TestModels.LoadAsync(factory, Example, TextWriter.Null);
        var path = WriteModel(json, ("a.csv", "id,word\n1,hello\n"));

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => TestModels.LoadAsync(factory, path, TextWriter.Null));

        Assert.Contains(message, ex.Message);
        await using var db = factory.CreateDbContext();
        Assert.Equal(20, await db.Nodes.CountAsync());
    }

    [Fact]
    public async Task Required_fields_and_links_are_kept_with_the_model()
    {
        var factory = await NewDatabaseAsync();

        await TestModels.LoadAsync(factory, Library.Write(), TextWriter.Null);

        var book = (await new RecordRepository(factory).TypeAsync("Book"))!;
        Assert.Equal([new RecordField("isbn", FieldType.Text) { Required = true }], book.Fields);
        Assert.Equal([new RequiredLink("WRITTEN_BY", "Author")], book.RequiredLinks);
        Assert.Empty((await new RecordRepository(factory).TypeAsync("Author"))!.RequiredLinks);
    }

    [Theory]
    [InlineData("id,title,isbn,author\nb1,Tidal Atlas,111,a1\nb2,Salt Roads,,a2\n,No key,,a1\n", "books.csv: isbn is required, but 1 row(s) have none, such as row 3.")]
    [InlineData("id,title,isbn,author\nb1,Tidal Atlas,111,a1\nb2,Salt Roads,222,\nb3,Lost,333,a9\n", "Each Book needs at least one Book WRITTEN_BY Author link, but 2 have none, such as b2, b3.")]
    public async Task A_required_value_or_link_thats_missing_stops_the_load(string books, string message)
    {
        var factory = await NewDatabaseAsync();
        await TestModels.LoadAsync(factory, Example, TextWriter.Null);

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(() => TestModels.LoadAsync(factory, Library.Write(books), TextWriter.Null));

        Assert.Equal(message, ex.Message);
        await using var db = factory.CreateDbContext();
        Assert.Equal(20, await db.Nodes.CountAsync());
    }

    private static ServiceProvider Services(params (string Key, string Connection)[] models)
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < models.Length; i++)
        {
            settings[$"Models:{models[i].Key}:Name"] = $"Model {models[i].Key}";
            settings[$"Models:{models[i].Key}:Order"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[$"ConnectionStrings:{models[i].Key}"] = models[i].Connection;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<ModelCatalog>();
        services.AddScoped<CurrentModel>();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Linkbase.Core.Security.RowSecurityState>();
        services.AddScoped<Linkbase.Core.Security.ViewerConnectionInterceptor>();
        services.AddSingleton<Linkbase.Core.Security.ViewerDirectories>();
        services.AddScoped<Linkbase.Core.Security.ICurrentUserAccessor, Linkbase.Core.Security.ConfiguredUserAccessor>();
        services.AddScoped<Linkbase.Core.Security.IViewerResolver, Linkbase.Core.Security.DirectoryViewerResolver>();
        services.AddScoped<IDbContextFactory<LinkbaseDbContext>, ModelDbContextFactory>();
        services.AddScoped<ModelLoader>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_commands_list_migrate_and_load_into_the_model_named()
    {
        var first = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var second = await fixture.CreateModelDatabaseAsync("SELECT 1");
        await using var services = Services(("first", first), ("second", second));
        using var scope = services.CreateScope();
        var output = new StringWriter();
        var commands = new ModelCommands(scope.ServiceProvider, output);

        Assert.Equal(0, await commands.RunAsync(["model", "list"]));
        Assert.Contains("first: Model first (generic)", output.ToString());
        Assert.Equal(0, await commands.RunAsync(["model", "migrate"]));
        Assert.Contains("Model second: up to date", output.ToString());

        Assert.Equal(0, await commands.RunAsync(["model", "load", Example, "second"]));
        Assert.Contains("Loading into Model second", output.ToString());
        Assert.Contains("Loaded 4 types, 20 nodes and 26 links", output.ToString());
        await using (var db = new Factory(second).CreateDbContext())
        {
            Assert.Equal(20, await db.Nodes.CountAsync());
        }

        await using (var db = new Factory(first).CreateDbContext())
        {
            Assert.Equal(0, await db.Nodes.CountAsync());
        }

        Assert.Equal(1, await commands.RunAsync(["model", "load", Example, "nope"]));
        Assert.Contains("There's no model 'nope'", output.ToString());
        Assert.Equal(1, await commands.RunAsync(["model", "load", WriteModel("not json")]));
        Assert.True(ModelCommands.IsCommand(["model", "load", "x"]));
        Assert.False(ModelCommands.IsCommand(["model", "load"]));
    }

    [Fact]
    public async Task The_security_command_turns_row_security_on_and_off_and_needs_a_security_section()
    {
        var connection = await fixture.CreateModelDatabaseAsync("SELECT 1");
        await using var services = Services(("only", connection));
        using var scope = services.CreateScope();
        var output = new StringWriter();
        var commands = new ModelCommands(scope.ServiceProvider, output);
        Assert.Equal(0, await commands.RunAsync(["model", "migrate"]));
        Assert.Equal(0, await commands.RunAsync(["model", "load", RepoPaths.Resolve("models/archimate/model.json"), "only"]));

        Assert.Equal(1, await commands.RunAsync(["model", "security", "on"]));
        Assert.Contains("has no security section", output.ToString());

        Assert.Equal(0, await commands.RunAsync(["model", "load", Example, "only"]));
        Assert.Equal(0, await commands.RunAsync(["model", "security", "on", "only"]));
        await using (var owner = new Factory(connection).CreateDbContext())
        {
            Assert.True(await Linkbase.Core.Security.RowSecurity.IsEnabledAsync(owner));
        }

        Assert.Equal(0, await commands.RunAsync(["model", "security", "off"]));
        await using (var owner = new Factory(connection).CreateDbContext())
        {
            Assert.False(await Linkbase.Core.Security.RowSecurity.IsEnabledAsync(owner));
        }

        Assert.False(ModelCommands.IsCommand(["model", "security", "maybe"]));
    }

    [Fact]
    public void Without_models_configured_there_is_one_using_the_linkbase_connection()
    {
        var catalog = new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Linkbase"] = "Host=x" }).Build());
        var model = catalog.Models.Single();
        Assert.Equal("Linkbase", model.ConnectionStringName);
        Assert.Equal(["Climbing"], model.Domains);
        Assert.Equal("Host=x;Options=\"-c jit=off\"", catalog.ConnectionString(model));
        Assert.Same(model, catalog.Find("nope") ?? catalog.Default);
    }

    [Fact]
    public void Jit_is_turned_off_without_losing_options_already_set()
    {
        var catalog = new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Linkbase"] = "Host=x;Options=-c statement_timeout=5s",
        }).Build());
        var builder = new NpgsqlConnectionStringBuilder(catalog.ConnectionString(catalog.Default));
        Assert.Equal("-c statement_timeout=5s -c jit=off", builder.Options);
    }

    [Fact]
    public void Models_come_in_order_and_an_unknown_choice_falls_back_to_the_first()
    {
        var catalog = new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:rail:Name"] = "Swiss rail",
            ["Models:rail:Order"] = "2",
            ["Models:climbing:Name"] = "Climbing",
            ["Models:climbing:ConnectionString"] = "Linkbase",
            ["Models:climbing:Domains:0"] = "Climbing",
            ["Models:climbing:Order"] = "1",
        }).Build());

        Assert.Equal(["climbing", "rail"], catalog.Models.Select(m => m.Key));
        var current = new CurrentModel(catalog);
        current.Select("rail");
        Assert.Equal("Swiss rail", current.Info.Name);
        Assert.Empty(current.Domains);
        current.Select("old-cookie");
        Assert.Equal("climbing", current.Info.Key);
        Assert.Equal("Climbing", current.Domains.Single().Key);
        Assert.Throws<InvalidOperationException>(() => catalog.ConnectionString(catalog.Find("rail")!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

// A small made-up library, where every book needs an author.
internal static class Library
{
    public const string Model = """
        {
          "name": "Library",
          "types": [
            { "name": "Book", "source": { "file": "books.csv", "key": "id", "name": "title" }, "fields": [ { "key": "isbn", "required": true } ] },
            { "name": "Author", "source": { "file": "authors.csv", "key": "id", "name": "name" } }
          ],
          "relationships": [
            { "from": "Book", "type": "WRITTEN_BY", "to": "Author", "required": true, "source": { "file": "books.csv", "fromColumn": "id", "toColumn": "author" } }
          ]
        }
        """;

    public static (string Name, string Content)[] Files(string books = "id,title,isbn,author\nb1,Tidal Atlas,111,a1\nb2,Salt Roads,222,a2\n") =>
        [("books.csv", books), ("authors.csv", "id,name\na1,Mara Quill\na2,Odo Venn\na3,Pia Lark\n")];

    // Writes the model to a new folder and returns the model file's path.
    public static string Write(string? books = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"linkbase-library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        foreach (var (name, content) in books is null ? Files() : Files(books))
        {
            File.WriteAllText(Path.Combine(folder, name), content);
        }

        var path = Path.Combine(folder, "model.json");
        File.WriteAllText(path, Model);
        return path;
    }
}
