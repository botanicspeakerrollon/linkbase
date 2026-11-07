using Linkbase.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class HealthTests(PostgresFixture fixture)
{
    private static ModelCatalog Catalog(params (string Key, string Connection)[] models)
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < models.Length; i++)
        {
            settings[$"Models:{models[i].Key}:Name"] = models[i].Key;
            settings[$"Models:{models[i].Key}:Order"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[$"ConnectionStrings:{models[i].Key}"] = models[i].Connection;
        }

        return new ModelCatalog(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static Task<HealthCheckResult> CheckAsync(ModelCatalog catalog) =>
        new ModelDatabasesHealthCheck(catalog).CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Ready_when_every_models_database_is_migrated()
    {
        var result = await CheckAsync(Catalog(("main", fixture.ConnectionString), ("other", await fixture.CreateModelDatabaseAsync("SELECT 1"))));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("ready", result.Data["other"]);
    }

    [Fact]
    public async Task Not_ready_when_a_database_cant_be_reached_or_needs_migrating()
    {
        var empty = $"empty_{Guid.NewGuid():N}";
        await using (var db = fixture.CreateContext())
        {
#pragma warning disable EF1002 // The name is a generated GUID, and CREATE DATABASE can't take a parameter.
            await db.Database.ExecuteSqlRawAsync($"CREATE DATABASE {empty}");
#pragma warning restore EF1002
        }

        var result = await CheckAsync(Catalog(
            ("main", fixture.ConnectionString),
            ("down", "Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1"),
            ("new", new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = empty }.ConnectionString)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(("ready", "unreachable"), (result.Data["main"], result.Data["down"]));
        Assert.Contains("migration(s) pending", (string)result.Data["new"]);
        Assert.Contains("down is unreachable", result.Description);
    }
}
