using Linkbase.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Models;

//   dotnet run --project src/Linkbase -- model list
//   dotnet run --project src/Linkbase -- model migrate               Apply migrations to every model's database
//   dotnet run --project src/Linkbase -- model setup                 Create, migrate and fill the models that name a Seed file
//   dotnet run --project src/Linkbase -- model load <file> [model]   Load a model file into a model, the first by default
public sealed class ModelCommands(IServiceProvider services, TextWriter output)
{
    public static bool IsCommand(string[] args) => args is ["model", "list"] or ["model", "migrate"] or ["model", "setup"] or ["model", "load", _] or ["model", "load", _, _];

    public async Task<int> RunAsync(string[] args)
    {
        var catalog = services.GetRequiredService<ModelCatalog>();
        try
        {
            switch (args)
            {
                case ["model", "list"]:
                    foreach (var model in catalog.Models)
                    {
                        await output.WriteLineAsync($"{model.Key}: {model.Name} ({(model.Domains.Count == 0 ? "generic" : string.Join(", ", model.Domains))})");
                    }

                    return 0;

                case ["model", "setup"]:
                    await new ModelSetup(catalog, services.GetRequiredService<CurrentModel>(), services.GetRequiredService<ModelLoader>()).RunAsync(output);
                    return 0;

                case ["model", "migrate"]:
                    foreach (var model in catalog.Models)
                    {
                        await using var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(model)).Options);
                        var pending = (await db.Database.GetPendingMigrationsAsync()).Count();
                        await db.Database.MigrateAsync();
                        await output.WriteLineAsync($"{model.Name}: {(pending == 0 ? "up to date" : $"applied {pending}")}");
                    }

                    return 0;

                default:
                    var target = args.Length > 3 ? catalog.Find(args[3]) ?? throw new LinkbaseValidationException($"There's no model '{args[3]}'. Run: model list") : catalog.Default;
                    services.GetRequiredService<CurrentModel>().Select(target.Key);
                    await output.WriteLineAsync($"Loading into {target.Name}");
                    // dotnet run starts in the project folder, so a path that isn't there is taken from the repo root.
                    var path = File.Exists(args[2]) ? args[2] : RepoPaths.Resolve(args[2]);
                    var summary = await services.GetRequiredService<ModelLoader>().LoadAsync(path, output);
                    await output.WriteLineAsync($"Loaded {summary.Types} types, {summary.Nodes:N0} nodes and {summary.Links:N0} links in {summary.Took.TotalSeconds:N0} s.");
                    return 0;
            }
        }
        catch (LinkbaseValidationException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 1;
        }
    }
}
