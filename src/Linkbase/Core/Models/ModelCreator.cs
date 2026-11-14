using System.Text.RegularExpressions;
using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Linkbase.Core.Models;

// Makes a model in the running app: a database of its own beside the default model's, the migrations applied, and a row in
// the default model's database so every instance and every restart finds it. No existing model or database is touched: a
// name that is taken, or a database that already exists, is refused. See docs/models.md.
public sealed partial class ModelCreator(ModelCatalog catalog, ILogger<ModelCreator> logger)
{
    public const int MaxNameLength = 60;

    // Database names start with this, so a model made here can't share a name with one set up by hand.
    public const string DatabasePrefix = "model_";

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotAlphanumeric();

    // Reads the models made in the app from the default model's database. Until the migrations that add its table have
    // been applied there are none, and a database that can't be reached leaves the list as it was.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = DefaultContext();
            var rows = await db.UserModels.AsNoTracking().OrderBy(m => m.CreatedAt).ThenBy(m => m.Key)
                .Select(m => new { m.Key, m.Name, m.Database })
                .ToListAsync(cancellationToken);
            catalog.SetMade(rows.Select(r => new ModelInfo(r.Key, r.Name, "", [], int.MaxValue) { Database = r.Database }));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            catalog.SetMade([]);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Couldn't read the models made in the app, so the ones already known stay as they are.");
        }
    }

    // The key a name becomes: lower case letters and digits, joined by dashes.
    public static string KeyFor(string name) => NotAlphanumeric().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');

    public static string DatabaseFor(string key) => DatabasePrefix + key.Replace('-', '_');

    // Makes an empty model called this, with no domains: its types come from whatever is imported into it.
    public async Task<ModelInfo> CreateAsync(string? name, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            throw new LinkbaseValidationException($"Give the model a name of up to {MaxNameLength} characters.");
        }

        var key = KeyFor(name);
        if (key.Length == 0 || key.Length > 40)
        {
            throw new LinkbaseValidationException("Give the model a name with letters or numbers in it, and not too long.");
        }

        if (catalog.Models.Any(m => m.Key == key || string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LinkbaseValidationException($"There is already a model called {name}. Pick another name, so nothing in it is changed.");
        }

        var database = DatabaseFor(key);
        var connectionString = catalog.ConnectionString(new ModelInfo(key, name, "", [], int.MaxValue) { Database = database });

        if (!await ModelSetup.CreateDatabaseIfMissingAsync(connectionString, cancellationToken))
        {
            throw new LinkbaseValidationException($"A database called {database} is already there, so it was left alone. Pick another name.");
        }

        try
        {
            await using (var created = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options))
            {
                await created.Database.MigrateAsync(cancellationToken);
            }

            await using var registry = DefaultContext();
            registry.UserModels.Add(new UserModel { Key = key, Name = name, Database = database });
            await registry.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The database was made a moment ago by this call and nothing is in it, so taking it away leaves things as they were.
            await DropAsync(connectionString);
            throw;
        }

        await LoadAsync(cancellationToken);
        return catalog.Find(key) ?? throw new InvalidOperationException($"The model {name} was made but isn't in the catalog.");
    }

    // Takes away a model made by CreateAsync that never got any data: when its first import fails, there is nothing to keep.
    public async Task RemoveEmptyAsync(string key, CancellationToken cancellationToken = default)
    {
        var model = catalog.Find(key);
        if (model?.Database is null)
        {
            throw new LinkbaseValidationException("Only a model made in the app can be removed this way.");
        }

        var connectionString = catalog.ConnectionString(model);
        await using (var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options))
        {
            if (await db.NodeTypes.AnyAsync(cancellationToken) || await db.Nodes.AnyAsync(cancellationToken))
            {
                throw new LinkbaseValidationException($"{model.Name} has data in it, so it was left as it is.");
            }
        }

        await using (var registry = DefaultContext())
        {
            await registry.UserModels.Where(m => m.Key == key).ExecuteDeleteAsync(cancellationToken);
        }

        await DropAsync(connectionString);
        await LoadAsync(cancellationToken);
    }

    private LinkbaseDbContext DefaultContext() =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(catalog.Default)).Options);

    private static async Task DropAsync(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
