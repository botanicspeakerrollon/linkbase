using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Linkbase.Core.Models;

// `model setup`: makes every model that names a seed file (Models:<key>:Seed) ready to use from nothing. It creates the
// model's database if the server doesn't have it, applies the migrations, and loads the seed file if the model is empty.
// Safe to run again: a model that already has types or records is never loaded over, and a model without a Seed, such as
// someone's real logbook, is not touched at all.
public sealed class ModelSetup(ModelCatalog catalog, CurrentModel current, ModelLoader loader)
{
    public async Task RunAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        var seeded = catalog.Models.Where(m => m.Seed is not null).ToList();
        if (seeded.Count == 0)
        {
            throw new LinkbaseValidationException("No model names a seed file, so there is nothing to set up. Add Models:<key>:Seed. See docs/models.md.");
        }

        foreach (var model in seeded)
        {
            var path = ResolveSeed(model);
            var connectionString = catalog.ConnectionString(model);

            if (await CreateDatabaseIfMissingAsync(connectionString, cancellationToken))
            {
                await output.WriteLineAsync($"{model.Name}: created the database");
            }

            await using (var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options))
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
                await db.Database.MigrateAsync(cancellationToken);
                await output.WriteLineAsync($"{model.Name}: {(pending == 0 ? "up to date" : $"applied {pending} migrations")}");

                if (await db.NodeTypes.AnyAsync(cancellationToken) || await db.Nodes.AnyAsync(cancellationToken))
                {
                    await output.WriteLineAsync($"{model.Name}: already has data, left as it is");
                    await EnableRowSecurityAsync(model, connectionString, output, cancellationToken);
                    continue;
                }
            }

            current.Select(model.Key);
            var summary = await loader.LoadAsync(path, TextWriter.Null, cancellationToken);
            await output.WriteLineAsync($"{model.Name}: loaded {summary.Types} types, {summary.Nodes:N0} nodes and {summary.Links:N0} links in {summary.Took.TotalSeconds:N0} s");
            await EnableRowSecurityAsync(model, connectionString, output, cancellationToken);
        }
    }

    // For a model that asks for it (Models:<key>:RowSecurity) and whose file has a security section.
    private static async Task EnableRowSecurityAsync(ModelInfo model, string connectionString, TextWriter output, CancellationToken cancellationToken)
    {
        if (!model.RowSecurity)
        {
            return;
        }

        await using var owner = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
        var section = await owner.ModelSettings.Select(s => s.Security).FirstOrDefaultAsync(cancellationToken);
        if (!(Security.LabelCodec.FromJson(section)?.Enabled ?? false))
        {
            await output.WriteLineAsync($"{model.Name}: asks for row security but its file has no security section, so it stays off");
            return;
        }

        if (!await Security.RowSecurity.IsEnabledAsync(owner, cancellationToken))
        {
            await Security.RowSecurity.EnableAsync(owner, cancellationToken);
            await output.WriteLineAsync($"{model.Name}: row security on");
        }
    }

    // A seed path is found as given, then beside the app (where the image keeps its models folder), then from the repo root.
    private static string ResolveSeed(ModelInfo model)
    {
        var seed = model.Seed!;
        if (File.Exists(seed))
        {
            return seed;
        }

        var besideApp = Path.Combine(AppContext.BaseDirectory, seed);
        if (File.Exists(besideApp))
        {
            return besideApp;
        }

        var fromRepo = SafeRepoPath(seed);
        return fromRepo is not null && File.Exists(fromRepo)
            ? fromRepo
            : throw new LinkbaseValidationException($"The seed file for {model.Name} isn't there: {seed}");
    }

    private static string? SafeRepoPath(string relative)
    {
        try
        {
            return RepoPaths.Resolve(relative);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    // Connects through the server's own `postgres` database, since the model's doesn't exist yet. The user needs CREATEDB.
    internal static async Task<bool> CreateDatabaseIfMissingAsync(string connectionString, CancellationToken cancellationToken)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var name = target.Database ?? throw new LinkbaseValidationException("A model's connection string needs a Database.");
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };

        try
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
            exists.Parameters.AddWithValue("name", name);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return false;
            }

            // CREATE DATABASE can't take a parameter, so the name is quoted, with any quote doubled.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", connection);
            await create.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new LinkbaseValidationException($"The database user can't create '{name}'. Create it yourself, or use a user with CREATEDB.");
        }
    }
}
