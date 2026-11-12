using Linkbase.Core.Staging;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// The UKC and Mountain Project parsers writing into the generic store. Everything here is made up. See
// docs/history/climbing-to-generic-plan.md.
[Trait("Category", "Database")]
public sealed class GenericImportTests : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"logbook-generic-import-{Guid.NewGuid():N}");
    private readonly StringWriter _output = new();

    public GenericImportTests(PostgresFixture fixture)
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
        Directory.CreateDirectory(Path.Combine(_root, "models", "climbing"));
        File.Copy(Linkbase.Core.RepoPaths.Resolve("models/climbing/model.json"), Path.Combine(_root, "models", "climbing", "model.json"));
    }

    private string InRoot(string relative) => Path.Combine(_root, relative);

    private async Task<ModelFactory> ImportedAsync()
    {
        var generic = new ModelFactory(await _fixture.CreateModelDatabaseAsync("SELECT 1"));
        Assert.Equal(0, await new GenericImportCommands(generic, _output, InRoot).RunAsync("all", ["--fresh"]));
        return generic;
    }

    [Fact]
    public async Task Both_files_give_a_connected_store()
    {
        var generic = await ImportedAsync();

        var text = _output.ToString();
        Assert.Contains("UKC (batch", text);
        Assert.Contains("Mountain Project (batch", text);
        Assert.Contains("Trips:", text);
        await using var db = generic.CreateDbContext();
        Assert.True(await db.Nodes.AnyAsync(n => n.Type == "Ascent"));
        Assert.True(await db.Nodes.AnyAsync(n => n.Type == "Trip"));
        Assert.True(await db.ExternalRefs.AnyAsync(r => r.Source == DataSource.Ukc));

        // Every ascent is of a route, every route is at a crag and every crag is in an area, as the model requires.
        Assert.False(await db.Nodes.AnyAsync(n => n.Type == "Ascent" && !db.NodeLinks.Any(l => l.SourceId == n.Id && l.RelType == "OF")));
        Assert.False(await db.Nodes.AnyAsync(n => n.Type == "Route" && !db.NodeLinks.Any(l => l.SourceId == n.Id && l.RelType == "AT")));
        Assert.False(await db.Nodes.AnyAsync(n => n.Type == "Crag" && !db.NodeLinks.Any(l => l.SourceId == n.Id && l.RelType == "IN")));
    }

    [Fact]
    public async Task Importing_the_files_again_adds_nothing()
    {
        var generic = await ImportedAsync();
        await using var before = generic.CreateDbContext();
        var nodes = await before.Nodes.CountAsync();
        var links = await before.NodeLinks.CountAsync();

        Assert.Equal(0, await new GenericImportCommands(generic, TextWriter.Null, InRoot).RunAsync("all", []));

        await using var after = generic.CreateDbContext();
        Assert.Equal(nodes, await after.Nodes.CountAsync());
        Assert.Equal(links, await after.NodeLinks.CountAsync());
    }

    [Fact]
    public async Task A_batch_can_be_undone_like_a_csv_import()
    {
        var generic = new ModelFactory(await _fixture.CreateModelDatabaseAsync("SELECT 1"));
        await TestModels.LoadAsync(generic, InRoot("models/climbing/model.json"), TextWriter.Null);
        var importer = new GenericAscentImportService(generic);
        using var reader = new StreamReader(InRoot("data/samples/import.csv"));
        var summary = await importer.ImportAsync(DataSource.Ukc, null, UkcParser.Parse(reader).ToList());

        await new ImportStagingService(generic).UndoAsync(summary.BatchId);

        await using var db = generic.CreateDbContext();
        Assert.False(await db.Nodes.AnyAsync(n => n.Type != "Trip"));
        Assert.False(await db.NodeLinks.AnyAsync(n => n.RelType != "ON_TRIP" && n.RelType != "VISITED"));
        Assert.False(await db.ExternalRefs.AnyAsync());
    }

    public void Dispose()
    {
        _output.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
