using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Linkbase.Tests;

// One container per test run, with the real migrations applied. Tests share the database,
// so each one creates its own uniquely named rows rather than assuming an empty table.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public LinkbaseDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    public IDbContextFactory<LinkbaseDbContext> ContextFactory() => new TestContextFactory(this);

    private sealed class TestContextFactory(PostgresFixture fixture) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() => fixture.CreateContext();
    }

    // A separate, migrated database, with whatever the SQL puts in the generic store: a model the app has never heard of.
    // Nothing in it reaches the shared database that other tests use.
    public async Task<string> CreateModelDatabaseAsync(string insertRowsSql)
    {
        var name = $"model_{Guid.NewGuid():N}";
        await using (var admin = CreateContext())
        {
#pragma warning disable EF1002 // The name is a generated GUID, and CREATE DATABASE can't take a parameter.
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {name}");
#pragma warning restore EF1002
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        await using var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync();

        // Straight to Npgsql, because EF would read the braces in the JSON as parameters.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = insertRowsSql;
        await command.ExecuteNonQueryAsync();
        return connectionString;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
