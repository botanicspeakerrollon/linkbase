using Linkbase.Core;
using Linkbase.Core.CsvImport;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
public sealed class ModelCreatorTests(PostgresFixture fixture) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private readonly List<string> _made = [];
    private readonly List<string> _files = [];

    public Task InitializeAsync() => Task.CompletedTask;

    // The databases the tests made are on a server that may outlive the run.
    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString);
        await admin.OpenAsync();
        foreach (var database in _made)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }

        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private sealed class Factory(ModelCatalog catalog, CurrentModel current) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(current.Info)).Options);
    }

    // A catalog whose only configured model is a migrated database of its own, standing for the real one.
    private async Task<(ModelCatalog Catalog, ModelCreator Creator, string Existing)> ArrangeAsync()
    {
        var existing = await fixture.CreateModelDatabaseAsync("SELECT 1");
        var catalog = new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:real:Name"] = "Real model",
            ["Models:real:ConnectionString"] = "Real",
            ["ConnectionStrings:Real"] = existing,
        }).Build());
        return (catalog, new ModelCreator(catalog, NullLogger<ModelCreator>.Instance), existing);
    }

    private string Unique(string stem)
    {
        var name = $"{stem} {Guid.NewGuid().ToString("N")[..8]}";
        _made.Add(ModelCreator.DatabaseFor(ModelCreator.KeyFor(name)));
        return name;
    }

    private static async Task<long> RowsAsync(string connection)
    {
        await using var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connection).Options);
        return await db.Nodes.LongCountAsync() + await db.NodeTypes.LongCountAsync();
    }

    private CsvFile Csv(string fileName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkbase-creator-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        _files.Add(path);
        return CsvImportService.Read(path, fileName);
    }

    [Fact]
    public async Task Makes_a_migrated_database_of_its_own_and_lists_the_model()
    {
        var (catalog, creator, existing) = await ArrangeAsync();
        var name = Unique("Supplier risk");

        var model = await creator.CreateAsync(name);

        Assert.Equal(["real", model.Key], catalog.Models.Select(m => m.Key));
        Assert.Equal(name, model.Name);
        Assert.Empty(model.Domains);
        Assert.Equal(ModelCreator.DatabaseFor(model.Key), new NpgsqlConnectionStringBuilder(catalog.ConnectionString(model)).Database);
        Assert.Equal(0, await RowsAsync(catalog.ConnectionString(model)));

        // A second instance reads it from the default model's database.
        var other = new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:real:ConnectionString"] = "Real",
            ["ConnectionStrings:Real"] = existing,
        }).Build());
        await new ModelCreator(other, NullLogger<ModelCreator>.Instance).LoadAsync();
        Assert.Contains(model.Key, other.Models.Select(m => m.Key));
    }

    [Fact]
    public async Task Importing_into_the_new_model_leaves_the_existing_one_untouched()
    {
        var (catalog, creator, existing) = await ArrangeAsync();
        var model = await creator.CreateAsync(Unique("Customers"));
        var current = new CurrentModel(catalog);
        current.Select(model.Key);
        var starter = new CsvModelStarter(new CsvImportService(new Factory(catalog, current)));

        var outcome = await starter.ApplyAsync(CsvModelStarter.Propose([Csv("clients.csv", "id,name\nc1,Acme\nc2,Brill\n")]));

        Assert.True(outcome.Applied);
        Assert.Equal(1 + 2, await RowsAsync(catalog.ConnectionString(model)));
        Assert.Equal(0, await RowsAsync(existing));
    }

    [Fact]
    public async Task Refuses_a_name_that_is_taken_and_a_database_that_already_exists()
    {
        var (catalog, creator, _) = await ArrangeAsync();

        var taken = await Assert.ThrowsAsync<LinkbaseValidationException>(() => creator.CreateAsync("real MODEL"));
        Assert.Contains("already a model", taken.Message);

        // A database of that name is already on the server, so it is not touched or reused.
        var name = Unique("Collides");
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{ModelCreator.DatabaseFor(ModelCreator.KeyFor(name))}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var exists = await Assert.ThrowsAsync<LinkbaseValidationException>(() => creator.CreateAsync(name));
        Assert.Contains("left alone", exists.Message);
        Assert.Single(catalog.Models);

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => creator.CreateAsync("   "));
        await Assert.ThrowsAsync<LinkbaseValidationException>(() => creator.CreateAsync("!!!"));
    }

    [Fact]
    public async Task An_empty_new_model_can_be_taken_away_but_one_with_data_stays()
    {
        var (catalog, creator, _) = await ArrangeAsync();
        var empty = await creator.CreateAsync(Unique("Empty"));
        await creator.RemoveEmptyAsync(empty.Key);
        Assert.Single(catalog.Models);

        var kept = await creator.CreateAsync(Unique("Kept"));
        var current = new CurrentModel(catalog);
        current.Select(kept.Key);
        await new CsvModelStarter(new CsvImportService(new Factory(catalog, current)))
            .ApplyAsync(CsvModelStarter.Propose([Csv("clients.csv", "id,name\nc1,Acme\n")]));

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => creator.RemoveEmptyAsync(kept.Key));
        Assert.Contains(kept.Key, catalog.Models.Select(m => m.Key));

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => creator.RemoveEmptyAsync("real"));
    }

    [Fact]
    public void A_name_becomes_a_key_and_a_database_name()
    {
        Assert.Equal("supplier-risk-2", ModelCreator.KeyFor("  Supplier Risk (2)! "));
        Assert.Equal("model_supplier_risk_2", ModelCreator.DatabaseFor("supplier-risk-2"));
    }
}
