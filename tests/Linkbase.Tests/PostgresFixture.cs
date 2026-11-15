using Linkbase.Core;
using Linkbase.Core.Models;
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
// Every test class asks for a PostgresFixture, which is only a handle on the one server for the whole run (PostgresServer),
// so classes run in parallel instead of queueing behind a shared collection. The server is torn down when the process exits.
//
// Normally it's a Postgres container from Testcontainers. Where Docker isn't available, such as a CI runner without
// Docker-in-Docker, LINKBASE_TEST_CONNECTION names a Postgres server instead (a GitLab `services: postgres`, say), whose
// user can create databases. The run then creates its own databases there and drops them all at the end.
internal sealed class PostgresServer
{
    public const string ServerVariable = "LINKBASE_TEST_CONNECTION";

    private readonly string? _server = Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } server ? server : null;
    private readonly PostgreSqlContainer? _container;
    private readonly List<string> _created = [];
    private string _connectionString = "";

    public PostgresServer()
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

    private sealed class TestContextFactory(PostgresServer fixture) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() => fixture.CreateContext();
    }

    // Databases made for a test are copies of a template that was migrated once, because applying every migration took longer
    // than the test. A template can't be copied while anything is connected to it, so its connections aren't pooled.
    private readonly SemaphoreSlim _templateLock = new(1, 1);
    private readonly Dictionary<string, string> _templates = [];

    private async Task<string> TemplateAsync(string key, Func<string, Task> prepare)
    {
        await _templateLock.WaitAsync();
        try
        {
            if (!_templates.TryGetValue(key, out var name))
            {
                name = $"tmpl_{Guid.NewGuid():N}";
                await prepare(await CreateDatabaseAsync(name) + ";Pooling=false");
                _templates[key] = name;
            }

            return name;
        }
        finally
        {
            _templateLock.Release();
        }
    }

    private static async Task MigrateAsync(string connectionString)
    {
        await using var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync();
        await WithoutRequiredReasonsAsync(connectionString);
    }

    // The app asks for a reason with every change unless the model says otherwise, and most tests aren't about that. They get
    // a database where history is recorded but no reason is needed. HistoryTests turn it on.
    internal static async Task WithoutRequiredReasonsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO model_setting (id, record_history, require_reason) VALUES (1, true, false) ON CONFLICT (id) DO UPDATE SET require_reason = false", connection);
        await command.ExecuteNonQueryAsync();
    }

    // A separate, migrated database, with whatever the SQL puts in the generic store: a model the app has never heard of.
    // Nothing in it reaches the shared database that other tests use.
    public async Task<string> CreateModelDatabaseAsync(string insertRowsSql)
    {
        var template = await TemplateAsync("migrated", MigrateAsync);

        // Not pooled: every test makes a database of its own, and idle pooled connections to hundreds of them would use up
        // the server's connections.
        var connectionString = await CreateDatabaseAsync($"model_{Guid.NewGuid():N}", template) + ";Pooling=false";

        // Straight to Npgsql, because EF would read the braces in the JSON as parameters.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = insertRowsSql;
        await command.ExecuteNonQueryAsync();
        return connectionString;
    }

    // A migrated database with the climbing model file loaded and no records.
    public async Task<string> CreateClimbingDatabaseAsync()
    {
        var template = await TemplateAsync("climbing", async connectionString =>
        {
            await MigrateAsync(connectionString);
            await new ModelLoader(new ModelFactory(connectionString), Linkbase.Domains.RegisteredDomains.Registry).LoadAsync(RepoPaths.Resolve("models/climbing/model.json"), TextWriter.Null);
            await WithoutRequiredReasonsAsync(connectionString);
        });
        return await CreateDatabaseAsync($"model_{Guid.NewGuid():N}", template) + ";Pooling=false";
    }

    // A database with no tables at all, as an app pointed at a new server would find.
    public Task<string> CreateEmptyDatabaseAsync() => CreateDatabaseAsync($"empty_{Guid.NewGuid():N}");

    public async Task StartAsync()
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
        await WithoutRequiredReasonsAsync(_connectionString);
    }

    public async Task StopAsync()
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
    private async Task<string> CreateDatabaseAsync(string name, string? template = null)
    {
        var admin = _server ?? _connectionString;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            // The name is a generated GUID, and CREATE DATABASE can't take a parameter.
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}{(template is null ? "" : $" TEMPLATE {template}")}", connection);
            await create.ExecuteNonQueryAsync();
        }

        if (_container is null)
        {
            _created.Add(name);
        }

        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly Lazy<Task<PostgresServer>> Server = new(StartAsync);

    private PostgresServer _server = null!;

    private static async Task<PostgresServer> StartAsync()
    {
        var server = new PostgresServer();
        await server.StartAsync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => server.StopAsync().GetAwaiter().GetResult();
        return server;
    }

    public async Task InitializeAsync() => _server = await Server.Value;

    public Task DisposeAsync() => Task.CompletedTask;

    public string ConnectionString => _server.ConnectionString;

    public LinkbaseDbContext CreateContext() => _server.CreateContext();

    public IDbContextFactory<LinkbaseDbContext> ContextFactory() => _server.ContextFactory();

    public Task<string> CreateModelDatabaseAsync(string insertRowsSql) => _server.CreateModelDatabaseAsync(insertRowsSql);

    public Task<string> CreateClimbingDatabaseAsync() => _server.CreateClimbingDatabaseAsync();

    public Task<string> CreateEmptyDatabaseAsync() => _server.CreateEmptyDatabaseAsync();
}
