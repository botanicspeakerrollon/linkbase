using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Import;
using Microsoft.Extensions.Configuration;
using Neo4j.Driver;

namespace Linkbase.Tests;

public class AdminCommandParsingTests
{
    [Theory]
    [InlineData(true, "inspect", "neo4j")]
    [InlineData(true, "export", "neo4j")]
    [InlineData(true, "export", "neo4j", "out.json")]
    [InlineData(true, "import", "ukc")]
    [InlineData(true, "import", "mp", "file.csv")]
    [InlineData(true, "import", "all")]
    [InlineData(true, "rebuild", "trips")]
    [InlineData(true, "report")]
    [InlineData(false)]
    [InlineData(false, "--urls", "http://localhost")]
    [InlineData(false, "import")]
    public void Knows_its_commands(bool expected, params string[] args) => Assert.Equal(expected, AdminCommands.IsCommand(args));
}

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class AdminCommandTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"logbook-admin-{Guid.NewGuid():N}");
    private readonly StringWriter _output = new();

    public AdminCommandTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(Path.Combine(_root, "data", "samples"));
        File.Copy(Path.Combine("Fixtures", "ukc-sample.csv"), Path.Combine(_root, "data", "samples", "import.csv"));
        File.Copy(Path.Combine("Fixtures", "mp-sample.csv"), Path.Combine(_root, "data", "samples", "ticks.csv"));
        File.WriteAllText(
            Path.Combine(_root, "data", "corrections.json"),
            """
            {
              "partnerRenames": [{ "from": "Ann", "to": "Ann Z" }],
              "mountainProjectPartners": [{ "partners": ["Ann Z"], "areas": [] }],
              "tripMerges": []
            }
            """);
    }

    private AdminCommands Commands(IConfiguration? configuration = null) => new(
        new AscentImportService(_fixture.ContextFactory()),
        new TripService(_fixture.ContextFactory()),
        new ReconciliationService(_fixture.ContextFactory()),
        configuration ?? new ConfigurationBuilder().Build(),
        _output,
        relative => Path.Combine(_root, relative));

    [Fact]
    public async Task Import_all_loads_both_files_rebuilds_trips_and_writes_the_report()
    {
        Assert.Equal(0, await Commands().RunAsync(["import", "all"]));

        var text = _output.ToString();
        Assert.Contains("UKC (batch", text);
        Assert.Contains("Mountain Project (batch", text);
        Assert.Contains("Trips:", text);
        Assert.Contains("# Reconciliation report", text);
        Assert.Contains("Not run:", text);
        Assert.True(File.Exists(Path.Combine(_root, "data", "samples", "reconciliation.md")));
    }

    [Fact]
    public async Task Import_one_file_can_name_its_path()
    {
        var path = Path.Combine(_root, "data", "samples", "import.csv");
        Assert.Equal(0, await Commands().RunAsync(["import", "ukc", path]));
        Assert.Contains("UKC (batch", _output.ToString());

        Assert.Equal(0, await Commands().RunAsync(["import", "mp"]));
        Assert.Contains("Mountain Project (batch", _output.ToString());

        Assert.Equal(0, await Commands().RunAsync(["import", "ukc"]));
    }

    [Fact]
    public async Task Rebuild_trips_reports_what_it_did()
    {
        Assert.Equal(0, await Commands().RunAsync(["rebuild", "trips"]));
        Assert.Contains("Trips:", _output.ToString());
    }

    [Fact]
    public async Task Report_includes_the_neo4j_comparison_when_an_export_exists()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "data", "samples", "neo4j-export.json"), """{ "ascents": [], "trips": [] }""");

        Assert.Equal(0, await Commands().RunAsync(["report"]));

        Assert.Contains("Ascents identical: 0 of 0", _output.ToString());
    }

    [Fact]
    public async Task An_empty_export_is_a_clear_failure()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "data", "samples", "neo4j-export.json"), "null");
        await Assert.ThrowsAsync<FormatException>(() => Commands().RunAsync(["report"]));
    }

    [Fact]
    public async Task A_missing_corrections_file_stops_the_import()
    {
        File.Delete(Path.Combine(_root, "data", "corrections.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Commands().RunAsync(["import", "all"]));
    }

    [Fact]
    public async Task An_unknown_command_is_reported()
    {
        Assert.Equal(1, await Commands().RunAsync(["nonsense"]));
        Assert.Contains("Unknown command", _output.ToString());
    }

    [Theory]
    [InlineData("inspect", "neo4j")]
    [InlineData("export", "neo4j")]
    public async Task Neo4j_commands_need_a_password(params string[] args)
    {
        Assert.Equal(1, await Commands().RunAsync(args));
        Assert.Contains("Neo4j:Password is not set", _output.ToString());
    }

    public void Dispose()
    {
        _output.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}

// A real Neo4j, so the inspect and export queries run against the database they were written for.
public sealed class Neo4jFixture : IAsyncLifetime
{
    public const string Password = "test-password-123";
    private const int BoltPort = 7687;

    private readonly IContainer _container = new ContainerBuilder("neo4j:5-community")
        .WithEnvironment("NEO4J_AUTH", $"neo4j/{Password}")
        .WithPortBinding(BoltPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Started\\."))
        .Build();

    public string BoltUri => $"bolt://{_container.Hostname}:{_container.GetMappedPublicPort(BoltPort)}";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var driver = GraphDatabase.Driver(BoltUri, AuthTokens.Basic("neo4j", Password));
        await driver.ExecutableQuery(
            """
            CREATE (country:Country {name: 'Wales'})
            CREATE (crag:Crag {name: 'Test Crag'})-[:IN]->(country)
            CREATE (climb:Climb {id: 'Test Crag | Slab', name: 'Slab', grade: 'VS 4c', protection: 'R', lengthFt: 100})-[:AT]->(crag)
            CREATE (trip:Trip {id: 'Wales 2020-01-01', name: 'Wales · Jan 2020', start: date('2020-01-01'), end: date('2020-01-02')})-[:VISITED]->(country)
            CREATE (partner:Partner {name: 'Ann'})
            CREATE (ascent:Ascent {id: 1, source: 'UKC', style: 'Lead', dateText: '01/Jan/20', notes: 'Fine'})-[:OF]->(climb)
            CREATE (ascent)-[:WITH]->(partner)
            CREATE (ascent)-[:ON_TRIP {inferred: true}]->(trip)
            CREATE (:Ascent {id: 'mp-2', source: 'MountainProject', mpStyle: 'Lead', dateText: '2020-01-02'})-[:OF]->(climb)
            """).ExecuteAsync();
        await driver.ExecutableQuery("CREATE CONSTRAINT crag_name FOR (c:Crag) REQUIRE c.name IS UNIQUE").ExecuteAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[Trait("Category", "Database")]
public sealed class Neo4jCommandTests(Neo4jFixture neo4j) : IClassFixture<Neo4jFixture>
{
    private IConfiguration Configuration(string? uri = null, string password = Neo4jFixture.Password) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Neo4j:Uri"] = uri ?? neo4j.BoltUri,
            ["Neo4j:Password"] = password,
        })
        .Build();

    [Fact]
    public async Task Inspect_summarises_the_graph()
    {
        var output = new StringWriter();

        Assert.Equal(0, await Neo4jInspector.RunAsync(Configuration(), output));

        var text = output.ToString();
        Assert.Contains("neo4j (online) default", text);
        Assert.Contains("Inspecting database: neo4j", text);
        Assert.Contains(":Ascent: 2", text);
        Assert.Contains(":Climb.grade x1", text);
        Assert.Contains(":Ascent -[OF]-> :Climb: 2", text);
        Assert.Contains("ON_TRIP.inferred x1", text);
        Assert.Contains("crag_name", text);
    }

    [Fact]
    public async Task Inspect_reports_a_wrong_password()
    {
        var output = new StringWriter();
        Assert.Equal(1, await Neo4jInspector.RunAsync(Configuration(password: "wrong-password"), output));
        Assert.Contains("Could not connect", output.ToString());
    }

    [Fact]
    public async Task Inspect_reports_an_unreachable_server()
    {
        var output = new StringWriter();
        Assert.Equal(1, await Neo4jInspector.RunAsync(Configuration(uri: "bolt://127.0.0.1:1"), output));
        Assert.Contains("Could not connect", output.ToString());
    }

    [Fact]
    public async Task Inspect_shortens_long_values_in_the_summary()
    {
        await using var driver = GraphDatabase.Driver(neo4j.BoltUri, AuthTokens.Basic("neo4j", Neo4jFixture.Password));
        await driver.ExecutableQuery("CREATE (:LongValue {text: $text})").WithParameters(new { text = new string('x', 200) }).ExecuteAsync();

        var output = new StringWriter();
        await Neo4jInspector.RunAsync(Configuration(), output);

        Assert.Contains(new string('x', 60) + "...", output.ToString());
        Assert.DoesNotContain(new string('x', 61), output.ToString());
    }

    [Fact]
    public async Task Export_writes_ascents_and_trips_that_the_comparison_can_read()
    {
        var path = Path.GetTempFileName();
        try
        {
            var output = new StringWriter();
            Assert.Equal(0, await Neo4jExporter.RunAsync(Configuration(), path, output));
            Assert.Contains("Wrote 2 ascents and 1 trips", output.ToString());

            var export = System.Text.Json.JsonSerializer.Deserialize<Neo4jExport>(await File.ReadAllTextAsync(path), Neo4jExport.JsonOptions)!;
            var ukc = export.Ascents.Single(a => a.Source == "UKC");
            Assert.Equal("Slab", ukc.ClimbName);
            Assert.Equal("Test Crag", ukc.CragName);
            Assert.Equal("R", ukc.Protection);
            Assert.Equal(100, ukc.LengthFt);
            Assert.Equal(["Ann"], ukc.Partners);
            Assert.Equal(new Neo4jTripLink("Wales · Jan 2020", true), ukc.Trips.Single());

            var trip = export.Trips.Single();
            Assert.Equal("2020-01-01", trip.Start);
            Assert.Equal(["Wales"], trip.Visited);

            var comparison = Neo4jComparison.Compare(export, [], []);
            Assert.Equal(2, comparison.AscentsCompared);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
