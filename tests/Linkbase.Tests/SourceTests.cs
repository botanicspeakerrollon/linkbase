using Linkbase.Core;
using Linkbase.Core.History;
using Linkbase.Core.Models;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

// What a change says about where it came from: an id, a kind the model allows, and a comment. These use a small model of
// their own, so they don't depend on the example models.
[Trait("Category", "Database")]
public sealed class SourceTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private async Task<(ModelFactory Factory, RecordRepository Records)> LoadAsync(string history)
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-source-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{ "name": "Sources", "types": [ { "name": "Thing" } ], "history": {{history}} }""");
        _files.Add(path);
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        await new ModelLoader(factory).LoadAsync(path, TextWriter.Null);
        return (factory, new RecordRepository(factory));
    }

    private static RecordInput Thing(string name) => new(name, new Dictionary<string, string?>());

    [Fact]
    public async Task A_source_keeps_its_id_kind_and_comment()
    {
        var (factory, records) = await LoadAsync("""{ "sourceKinds": [ "Internet", "Ticket system" ] }""");

        await records.CreateAsync("Thing", "t1", Thing("Alpha"), new ChangeReason("Added", "HELP-114", "ticket SYSTEM", "Second comment on the ticket"));

        var entry = (await new HistoryRepository(factory).ForRecordAsync("thing:t1")).Single();
        Assert.Equal(("HELP-114", "Ticket system", "Second comment on the ticket"), (entry.SourceId, entry.SourceKind, entry.SourceComment));
        await using var db = factory.CreateDbContext();
        Assert.Equal(["Internet", "Ticket system"], (await ChangeRecorder.SettingsAsync(db, default)).SourceKinds);
    }

    [Fact]
    public async Task A_kind_the_model_does_not_list_is_refused()
    {
        var (_, records) = await LoadAsync("""{ "sourceKinds": [ "Internet" ] }""");

        var ex = await Assert.ThrowsAsync<LinkbaseValidationException>(
            () => records.CreateAsync("Thing", "t1", Thing("Alpha"), new ChangeReason("Added", "HELP-114", "Payroll")));

        Assert.Contains("Internet", ex.Message);
        Assert.Null(await records.GetAsync("Thing", "t1"));
    }

    [Fact]
    public async Task Without_source_kinds_the_only_kind_is_internet()
    {
        var (factory, records) = await LoadAsync("""{ "requireReason": false }""");

        await records.CreateAsync("Thing", "t1", Thing("Alpha"), new ChangeReason("Added", "https://example.com/a", "Internet"));
        await Assert.ThrowsAsync<LinkbaseValidationException>(
            () => records.CreateAsync("Thing", "t2", Thing("Beta"), new ChangeReason("Added", "x", "Wiki")));

        var entry = (await new HistoryRepository(factory).ForRecordAsync("thing:t1")).Single();
        Assert.Equal(("https://example.com/a", "Internet"), (entry.SourceId, entry.SourceKind));
    }

    [Fact]
    public async Task A_model_file_with_a_repeated_or_empty_source_kind_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-source-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "name": "Sources", "types": [ { "name": "Thing" } ], "history": { "sourceKinds": [ "Wiki", "wiki", " " ] } }""");
        _files.Add(path);

        var ex = Assert.Throws<LinkbaseValidationException>(() => ModelFile.Read(path));

        Assert.Contains("more than once", ex.Message);
        Assert.Contains("1 to 60", ex.Message);
    }
}
