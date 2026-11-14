using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Records;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
public sealed class CsvModelStarterTests(PostgresFixture fixture) : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly List<string> _files = [];

    private CsvFile Csv(string fileName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-starter-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        _files.Add(path);
        return CsvImportService.Read(path, fileName);
    }

    private IReadOnlyList<CsvFile> Files() =>
    [
        Csv("clients.csv", "id,name,founded\nc1,Acme,2001-04-02\nc2,Brill,1999-12-31\n"),
        Csv("projects.csv", "id,title,client_id,budget\np1,Portal,c1,1000\np2,Site,c2,250.5\np3,Orphan,,10\n"),
        Csv("people.csv", "id,name\nz1,Zoe Adams\nz2,Dev Raman\n"),
        Csv("assignments.csv", "person,project\nz1,p1\nz1,p2\nz2,p1\n"),
    ];

    [Fact]
    public void Guesses_types_fields_and_relationships()
    {
        var proposed = CsvModelStarter.Propose(Files());

        var projects = proposed[1];
        Assert.False(projects.IsLinks);
        Assert.Equal("Project", projects.Type);
        Assert.Equal(0, projects.KeyColumn);
        Assert.Equal(["title", "budget"], projects.Fields.Select(f => f.Key));
        Assert.Equal([FieldType.Number], projects.Fields.Where(f => f.Key == "budget").Select(f => f.Type));

        // client_id is a link to Client, not a field.
        var link = Assert.Single(projects.Links);
        Assert.Equal(("Client", "HAS_CLIENT"), (link.ToType, link.RelType));

        // A file of two key columns and nothing else is links between them.
        var assignments = proposed[3];
        Assert.True(assignments.IsLinks);
        Assert.Equal(("Person", "Project"), (assignments.FromType, assignments.ToType));
        Assert.False(proposed[0].IsLinks);
        Assert.Equal(1, proposed[0].NameColumn);
    }

    [Fact]
    public async Task Creates_the_model_in_a_blank_database()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var starter = new CsvModelStarter(new CsvImportService(factory));
        var proposed = CsvModelStarter.Propose(Files());

        Assert.Empty(await starter.CheckAsync(proposed));
        var outcome = await starter.ApplyAsync(proposed);

        Assert.True(outcome.Applied);
        Assert.Empty(outcome.Problems);
        await using var db = await ((IDbContextFactory<LinkbaseDbContext>)factory).CreateDbContextAsync();
        Assert.Equal(["Client", "Person", "Project"], await db.NodeTypes.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).ToListAsync());
        Assert.Equal(7, await db.Nodes.CountAsync());
        // Two projects have a client, and the empty client_id on the third is skipped.
        Assert.Equal(2, await db.NodeLinks.CountAsync(l => l.RelType == "HAS_CLIENT"));
        Assert.Equal(3, await db.NodeLinks.CountAsync(l => l.RelType == "HAS_PROJECT"));
        Assert.Equal("Zoe Adams", await db.Nodes.Where(n => n.Id == "person:z1").Select(n => n.Name).SingleAsync());
    }

    [Fact]
    public async Task A_mistake_in_a_file_changes_nothing()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var starter = new CsvModelStarter(new CsvImportService(factory));
        var proposed = CsvModelStarter.Propose(Files());
        proposed[1].Fields.First(f => f.Key == "title").Type = FieldType.Number;

        var outcome = await starter.ApplyAsync(proposed);

        Assert.False(outcome.Applied);
        Assert.Contains(outcome.Problems, p => p.StartsWith("projects.csv:", StringComparison.Ordinal) && p.Contains("title"));
        await using var db = await ((IDbContextFactory<LinkbaseDbContext>)factory).CreateDbContextAsync();
        Assert.Equal(0, await db.Nodes.CountAsync());
    }

    [Fact]
    public async Task Two_files_for_one_type_are_refused()
    {
        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var starter = new CsvModelStarter(new CsvImportService(factory));
        var proposed = CsvModelStarter.Propose([Csv("clients.csv", "id,name\n1,A\n"), Csv("client.csv", "id,name\n2,B\n")]);

        var problems = await starter.CheckAsync(proposed);

        Assert.Contains(problems, p => p.Contains("both Client"));
    }

    // The demo files in models/surveillance-demo, as its README walks through them, so it stays true.
    [Fact]
    public async Task The_surveillance_demo_files_make_the_model_its_readme_describes()
    {
        var files = new[] { "people", "companies", "accounts", "trades", "alerts", "insiders", "major_holders", "associates" }
            .Select(n => CsvImportService.Read(RepoPaths.Resolve($"models/surveillance-demo/{n}.csv"), $"{n}.csv"))
            .ToList();
        var proposed = CsvModelStarter.Propose(files);

        Assert.Equal(["Person", "Company", "Account", "Trade", "Alert"], proposed.Take(5).Select(p => p.Type));
        Assert.All(proposed.Take(5), p => Assert.False(p.IsLinks));
        Assert.Equal([true, true, true], proposed.Skip(5).Select(p => p.IsLinks));
        Assert.Equal(1, proposed[0].NameColumn);
        Assert.Equal([("Person", "HAS_PERSON")], proposed[2].Links.Select(l => (l.ToType, l.RelType)));
        Assert.Equal([("Account", "HAS_ACCOUNT"), ("Company", "HAS_COMPANY")], proposed[3].Links.Select(l => (l.ToType, l.RelType)));
        Assert.Equal([("Trade", "HAS_TRADE")], proposed[4].Links.Select(l => (l.ToType, l.RelType)));
        Assert.Equal([("Person", "Company"), ("Person", "Company"), ("Person", "Person")], proposed.Skip(5).Select(p => (p.FromType, p.ToType)));

        proposed[5].RelType = "INSIDER_OF";
        proposed[6].RelType = "MAJOR_HOLDER_OF";
        proposed[7].RelType = "ASSOCIATE_OF";

        var factory = new ModelFactory(await fixture.CreateModelDatabaseAsync("SELECT 1"));
        var starter = new CsvModelStarter(new CsvImportService(factory));
        Assert.Empty(await starter.CheckAsync(proposed));
        var outcome = await starter.ApplyAsync(proposed);

        Assert.True(outcome.Applied);
        Assert.Empty(outcome.Problems);
        await using var db = await ((IDbContextFactory<LinkbaseDbContext>)factory).CreateDbContextAsync();
        var counts = await db.Nodes.GroupBy(n => n.Type).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N);
        Assert.Equal(new Dictionary<string, int> { ["Person"] = 10, ["Company"] = 6, ["Account"] = 10, ["Trade"] = 18, ["Alert"] = 6 }, counts);
        var links = await db.NodeLinks.GroupBy(l => l.RelType).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N);
        Assert.Equal(
            new Dictionary<string, int> { ["HAS_PERSON"] = 9, ["HAS_ACCOUNT"] = 18, ["HAS_COMPANY"] = 18, ["HAS_TRADE"] = 5, ["INSIDER_OF"] = 5, ["MAJOR_HOLDER_OF"] = 4, ["ASSOCIATE_OF"] = 4 },
            links);
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }
}
