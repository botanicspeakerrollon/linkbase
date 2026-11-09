using Linkbase.Core.Staging;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// The UKC and Mountain Project parsers writing into the generic store. Everything here is made up. The typed importer is
// the reference: the same files through both must give the same records, links and external ids. See
// docs/climbing-to-generic-plan.md.
[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class GenericImportTests : IDisposable
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

    private async Task<(ModelFactory Typed, ModelFactory Generic)> ImportedAsync()
    {
        var typed = new ModelFactory(await _fixture.CreateModelDatabaseAsync("SELECT 1"));
        var generic = new ModelFactory(await _fixture.CreateModelDatabaseAsync("SELECT 1"));
        var commands = new AdminCommands(
            new AscentImportService(typed), new TripService(typed), new ReconciliationService(typed), TextWriter.Null, InRoot);
        Assert.Equal(0, await commands.RunAsync(["import", "all"]));
        Assert.Equal(0, await new GenericImportCommands(generic, _output, InRoot).RunAsync("all", ["--fresh"]));
        return (typed, generic);
    }

    [Fact]
    public async Task Both_files_give_the_same_store_as_the_typed_importer()
    {
        var (typed, generic) = await ImportedAsync();

        var problems = await new GenericVerifier(typed, generic).VerifyAsync(compareTimestamps: false);

        Assert.Empty(problems);
        var text = _output.ToString();
        Assert.Contains("UKC (batch", text);
        Assert.Contains("Mountain Project (batch", text);
        Assert.Contains("Trips:", text);
        await using var db = generic.CreateDbContext();
        Assert.True(await db.Nodes.AnyAsync(n => n.Type == "Ascent"));
        Assert.True(await db.Nodes.AnyAsync(n => n.Type == "Trip"));
        Assert.True(await db.ExternalRefs.AnyAsync(r => r.NodeId != null && r.Source == DataSource.Ukc));
    }

    [Fact]
    public async Task Importing_the_files_again_adds_nothing()
    {
        var (_, generic) = await ImportedAsync();
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
        await new Linkbase.Core.Models.ModelLoader(generic).LoadAsync(InRoot("models/climbing/model.json"), TextWriter.Null);
        var importer = new GenericAscentImportService(generic);
        using var reader = new StreamReader(InRoot("data/samples/import.csv"));
        var summary = await importer.ImportAsync(DataSource.Ukc, null, UkcParser.Parse(reader).ToList());

        await new ImportStagingService(generic).UndoAsync(summary.BatchId);

        await using var db = generic.CreateDbContext();
        Assert.False(await db.Nodes.AnyAsync(n => n.Type != "Trip"));
        Assert.False(await db.NodeLinks.AnyAsync(n => n.RelType != "ON_TRIP" && n.RelType != "VISITED"));
        Assert.False(await db.ExternalRefs.AnyAsync(r => r.NodeId != null));
    }

    public void Dispose()
    {
        _output.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
