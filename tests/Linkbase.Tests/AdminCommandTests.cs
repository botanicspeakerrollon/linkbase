using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Domains.Climbing.Import;

namespace Linkbase.Tests;

public class AdminCommandParsingTests
{
    [Theory]
    [InlineData(true, "import", "ukc")]
    [InlineData(true, "import", "mp", "file.csv")]
    [InlineData(true, "import", "all")]
    [InlineData(true, "rebuild", "trips")]
    [InlineData(true, "report")]
    [InlineData(false)]
    [InlineData(false, "--urls", "http://localhost")]
    [InlineData(false, "import")]
    [InlineData(false, "export", "neo4j")]
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

    private AdminCommands Commands() => new(
        new AscentImportService(_fixture.ContextFactory()),
        new TripService(_fixture.ContextFactory()),
        new ReconciliationService(_fixture.ContextFactory()),
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
        Assert.DoesNotContain("review", text, StringComparison.OrdinalIgnoreCase);
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

    public void Dispose()
    {
        _output.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
