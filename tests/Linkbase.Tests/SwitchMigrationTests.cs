using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Linkbase.Tests;

// SwitchClimbingToGenericStore drops the old climbing tables, so it must refuse while a logbook is still in them. These run it
// against a database stopped one migration short, with made-up rows.
[Trait("Category", "Database")]
[Collection(PostgresCollection.Name)]
public sealed class SwitchMigrationTests(PostgresFixture fixture)
{
    private const string Before = "20261109090745_ClimbingInsightsOnGenericStore";

    private static LinkbaseDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);

    private async Task<string> DatabaseBeforeTheSwitchAsync()
    {
        var connectionString = await fixture.CreateEmptyDatabaseAsync() + ";Pooling=false";
        await using var db = Context(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(Before);
        return connectionString;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT to_regclass('public.{table}') IS NOT NULL";
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Refuses_while_the_logbook_is_only_in_the_old_tables()
    {
        var connectionString = await DatabaseBeforeTheSwitchAsync();
        await ExecuteAsync(connectionString, "INSERT INTO area (user_id, name, kind) VALUES (1, 'Made-up country', 'country')");

        await using var db = Context(connectionString);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync());

        var postgres = error as PostgresException ?? error.InnerException as PostgresException;
        Assert.NotNull(postgres);
        Assert.Contains("Copy the logbook first", postgres.MessageText);
        Assert.True(await TableExistsAsync(connectionString, "area"));
        Assert.True(await TableExistsAsync(connectionString, "graph_nodes_climbing"));
    }

    [Fact]
    public async Task Refuses_while_external_ids_still_point_at_the_old_tables()
    {
        var connectionString = await DatabaseBeforeTheSwitchAsync();
        await ExecuteAsync(connectionString, "INSERT INTO external_ref (entity_type, entity_id, source, external_id) VALUES ('crag', 1, 'ukc', 'made-up')");

        await using var db = Context(connectionString);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync());

        Assert.Contains("External ids still point at the old tables", (error as PostgresException ?? error.InnerException as PostgresException)!.MessageText);
        Assert.True(await TableExistsAsync(connectionString, "area"));
    }

    [Fact]
    public async Task Runs_once_the_records_are_in_the_generic_store_and_drops_the_old_tables()
    {
        var connectionString = await DatabaseBeforeTheSwitchAsync();
        await ExecuteAsync(connectionString, """
            INSERT INTO area (user_id, name, kind) VALUES (1, 'Made-up country', 'country');
            INSERT INTO external_ref (entity_type, entity_id, source, external_id) VALUES ('crag', 1, 'ukc', 'made-up');
            INSERT INTO node_type (name, sort_order) VALUES ('Area', 1);
            INSERT INTO node (id, user_id, type, key, name, props) VALUES ('area:1', 1, 'Area', '1', 'Made-up country', '{}');
            INSERT INTO external_ref (node_id, node_type, source, external_id) VALUES ('area:1', 'Area', 'ukc', 'made-up-new');
            """);

        await using var db = Context(connectionString);
        await db.GetService<IMigrator>().MigrateAsync();

        Assert.False(await TableExistsAsync(connectionString, "area"));
        Assert.False(await TableExistsAsync(connectionString, "graph_nodes_climbing"));
        Assert.True(await TableExistsAsync(connectionString, "graph_nodes"));

        // The old-style reference went, and the node's own stayed.
        await using var check = Context(connectionString);
        var refs = await check.ExternalRefs.AsNoTracking().ToListAsync();
        Assert.Equal("made-up-new", Assert.Single(refs).ExternalId);
        Assert.Equal(["area:1"], await check.GraphNodes.AsNoTracking().Select(n => n.NodeId).ToListAsync());
    }

    [Fact]
    public async Task Runs_on_a_database_that_never_held_climbing()
    {
        var connectionString = await DatabaseBeforeTheSwitchAsync();

        await using var db = Context(connectionString);
        await db.GetService<IMigrator>().MigrateAsync();

        Assert.False(await TableExistsAsync(connectionString, "ascent"));
    }
}
