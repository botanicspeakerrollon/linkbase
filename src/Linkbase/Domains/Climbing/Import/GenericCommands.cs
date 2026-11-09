using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Import;

//   dotnet run --project src/Linkbase -- climbing to-generic <target model> [--same-database]
//   dotnet run --project src/Linkbase -- climbing verify-generic <target model>
//
// to-generic copies the climbing model's typed tables into the target model's generic store, as models/climbing/model.json
// describes it, and checks the result. verify-generic only checks. Both read the typed tables of the first model that uses
// the Climbing module and never change them. Converting into the same database is the real switch, so it has to be asked for.
public sealed class GenericCommands(ModelCatalog catalog, TextWriter output, Func<string, string>? resolvePath = null)
{
    private const string ModelFilePath = "models/climbing/model.json";

    public static bool IsCommand(string[] args) =>
        args is ["climbing", "to-generic", _] or ["climbing", "to-generic", _, "--same-database"] or ["climbing", "verify-generic", _];

    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            var sourceModel = catalog.Models.FirstOrDefault(m => m.Domains.Contains("Climbing")) ?? catalog.Default;
            var targetModel = catalog.Find(args[2]) ?? throw new LinkbaseValidationException($"There's no model '{args[2]}'. Run: model list");
            var sameDatabase = catalog.ConnectionString(sourceModel) == catalog.ConnectionString(targetModel);
            if (args[1] == "to-generic" && sameDatabase && !args.Contains("--same-database"))
            {
                throw new LinkbaseValidationException(
                    $"{targetModel.Name} is the same database as {sourceModel.Name}. Converting into it replaces its generic store; add --same-database to say you mean it.");
            }

            var source = new ConnectionFactory(catalog.ConnectionString(sourceModel));
            var target = new ConnectionFactory(catalog.ConnectionString(targetModel));
            if (args[1] == "to-generic")
            {
                var modelFile = (resolvePath ?? (path => RepoPaths.Resolve(path)))(ModelFilePath);
                await output.WriteLineAsync($"Copying {sourceModel.Name} into the generic store of {targetModel.Name}");
                var summary = await new GenericConverter(source, target).ConvertAsync(modelFile, output);
                foreach (var (type, count) in summary.Nodes.OrderBy(n => n.Key))
                {
                    await output.WriteLineAsync($"  {type}: {count:N0}");
                }

                foreach (var (link, count) in summary.Links.OrderBy(n => n.Key))
                {
                    await output.WriteLineAsync($"  {link}: {count:N0}");
                }

                await output.WriteLineAsync($"  external ids: {summary.Refs:N0}");
            }

            var problems = await new GenericVerifier(source, target).VerifyAsync();
            if (problems.Count == 0)
            {
                await output.WriteLineAsync($"{targetModel.Name} matches the typed tables.");
                return 0;
            }

            await output.WriteLineAsync($"{targetModel.Name} differs from the typed tables:");
            foreach (var problem in problems)
            {
                await output.WriteLineAsync($"  {problem}");
            }

            return 1;
        }
        catch (LinkbaseValidationException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private sealed class ConnectionFactory(string connectionString) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
    }
}
