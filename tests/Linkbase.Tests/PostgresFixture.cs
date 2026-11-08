using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Linkbase.Tests;

// One database per test run, with the real migrations applied. Tests share it, so each one creates its own uniquely named
// rows rather than assuming an empty table.
//
// Normally it's a Postgres container from Testcontainers. Where Docker isn't available, such as a CI runner without
// Docker-in-Docker, LINKBASE_TEST_CONNECTION names a Postgres server instead (a GitLab `services: postgres`, say), whose
// user can create databases. The run then creates its own databases there and drops them all at the end.
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ServerVariable = "LINKBASE_TEST_CONNECTION";

    private readonly string? _server = Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } server ? server : null;
    private readonly PostgreSqlContainer? _container;
    private readonly List<string> _created = [];
    private string _connectionString = "";

    public PostgresFixture()
    {
        if (_server is null)
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        }
    }

    public string ConnectionString => _connectionString;

    public LinkbaseDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(_connectionString).Options);

    public IDbContextFactory<LinkbaseDbContext> ContextFactory() => new TestContextFactory(this);

    private sealed class TestContextFactory(PostgresFixture fixture) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() => fixture.CreateContext();
    }

    // A separate, migrated database, with whatever the SQL puts in the generic store: a model the app has never heard of.
    // Nothing in it reaches the shared database that other tests use.
    public async Task<string> CreateModelDatabaseAsync(string insertRowsSql)
    {
        var connectionString = await CreateDatabaseAsync($"model_{Guid.NewGuid():N}");
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

    // A database with no tables at all, as an app pointed at a new server would find.
    public Task<string> CreateEmptyDatabaseAsync() => CreateDatabaseAsync($"empty_{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        else
        {
            _connectionString = await CreateDatabaseAsync($"linkbase_test_{Guid.NewGuid():N}");
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        // Everything this run created, so a shared server is left as it was found.
        await using var connection = new NpgsqlConnection(_server);
        await connection.OpenAsync();
        foreach (var name in _created)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // On the container, or on the given server, connecting through whichever database is already there.
    private async Task<string> CreateDatabaseAsync(string name)
    {
        var admin = _server ?? _connectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            // The name is a generated GUID, and CREATE DATABASE can't take a parameter.
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        if (_container is null)
        {
            _created.Add(name);
        }

        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
