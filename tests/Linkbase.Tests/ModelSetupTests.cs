using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
public sealed class ModelSetupTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class Factory(ModelCatalog catalog, CurrentModel current) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(current.Info)).Options);
    }

    private (ModelCatalog Catalog, ModelSetup Setup) Arrange(string database, string? seed)
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        var settings = new Dictionary<string, string?>
        {
            ["Models:demo:Name"] = "Demo",
            ["Models:demo:ConnectionString"] = "Demo",
            ["Models:other:Name"] = "Other",
            ["Models:other:ConnectionString"] = "Other",
            ["ConnectionStrings:Demo"] = connection,
            // Never made, which proves a model without a Seed is left alone.
            ["ConnectionStrings:Other"] = new NpgsqlConnectionStringBuilder(connection) { Database = "never_made_" + Guid.NewGuid().ToString("N") }.ConnectionString,
        };
        if (seed is not null)
        {
            settings["Models:demo:Seed"] = seed;
        }

        var catalog = new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var current = new CurrentModel(catalog);
        return (catalog, new ModelSetup(catalog, current, new ModelLoader(new Factory(catalog, current))));
    }

    private static async Task<long> NodeCountAsync(ModelCatalog catalog)
    {
        await using var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(catalog.Find("demo")!)).Options);
        return await db.Nodes.LongCountAsync();
    }

    [Fact]
    public async Task Creates_the_database_migrates_and_loads_the_seed_then_leaves_it_alone()
    {
        var (catalog, setup) = Arrange("setup_" + Guid.NewGuid().ToString("N"), "models/consultancy/model.json");
        var first = new StringWriter();

        await setup.RunAsync(first);

        Assert.Contains("created the database", first.ToString());
        Assert.Contains("loaded", first.ToString());
        var nodes = await NodeCountAsync(catalog);
        Assert.True(nodes > 0);

        var again = new StringWriter();
        await setup.RunAsync(again);

        Assert.Contains("already has data", again.ToString());
        Assert.DoesNotContain("created the database", again.ToString());
        Assert.Equal(nodes, await NodeCountAsync(catalog));
    }

    [Fact]
    public async Task Says_so_when_no_model_has_a_seed()
    {
        var (_, setup) = Arrange("setup_" + Guid.NewGuid().ToString("N"), seed: null);

        await Assert.ThrowsAsync<LinkbaseValidationException>(() => setup.RunAsync(TextWriter.Null));
    }

    [Fact]
    public async Task Says_so_when_the_seed_file_is_missing()
    {
        var (_, setup) = Arrange("setup_" + Guid.NewGuid().ToString("N"), "models/nothing/model.json");

        var error = await Assert.ThrowsAsync<LinkbaseValidationException>(() => setup.RunAsync(TextWriter.Null));

        Assert.Contains("isn't there", error.Message);
    }
}
