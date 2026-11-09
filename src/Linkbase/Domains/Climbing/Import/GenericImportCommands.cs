using System.Text;
using Linkbase.Core;
using Linkbase.Core.Models;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Domains.Climbing.Import;

//   dotnet run --project src/Linkbase -- climbing import <model> ukc [file]
//   dotnet run --project src/Linkbase -- climbing import <model> mp [file]
//   dotnet run --project src/Linkbase -- climbing import <model> all [--fresh] [--same-database]
//
// The UKC and Mountain Project parsers writing into a model's generic store, as models/climbing/model.json describes it.
// Each file is one import batch, which can be undone like a CSV import. `all` loads both default files and rebuilds the
// trips. With --fresh it first reloads the model file, which empties the store, so a climbing model can be rebuilt from the
// exports alone.
public sealed class GenericImportCommands(
    IDbContextFactory<LinkbaseDbContext> target, TextWriter output, Func<string, string>? resolvePath = null)
{
    private const string ModelFilePath = "models/climbing/model.json";
    private const string DefaultUkcPath = "data/samples/import.csv";
    private const string DefaultMountainProjectPath = "data/samples/ticks.csv";

    public static bool IsCommand(string[] args) => args is ["climbing", "import", _, "ukc" or "mp" or "all", ..];

    // Finds the target model, and refuses to empty the real database unless that was asked for twice.
    public static async Task<int> RunAsync(ModelCatalog catalog, string[] args, TextWriter output)
    {
        var model = catalog.Find(args[2]);
        if (model is null)
        {
            await output.WriteLineAsync($"There's no model '{args[2]}'. Run: model list");
            return 1;
        }

        var sameDatabase = catalog.Models.Where(m => m.Domains.Contains("Climbing")).Any(m => catalog.ConnectionString(m) == catalog.ConnectionString(model));
        if (args.Contains("--fresh") && sameDatabase && !args.Contains("--same-database"))
        {
            await output.WriteLineAsync($"{model.Name} is the climbing model's own database. --fresh empties its generic store; add --same-database to say you mean it.");
            return 1;
        }

        var factory = new ConnectionFactory(catalog.ConnectionString(model));
        try
        {
            return await new GenericImportCommands(factory, output).RunAsync(args[3], args[4..]);
        }
        catch (LinkbaseValidationException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    public async Task<int> RunAsync(string what, string[] rest)
    {
        var path = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var corrections = Corrections.Load(Resolve("data/corrections.json"));
        if (what == "all" && rest.Contains("--fresh"))
        {
            await new ModelLoader(target).LoadAsync(Resolve(ModelFilePath), TextWriter.Null);
            await output.WriteLineAsync("Reloaded the model file. The generic store is empty.");
        }

        var importer = new GenericAscentImportService(target);
        if (what is "ukc" or "all")
        {
            using var reader = new StreamReader(what == "ukc" && path is not null ? path : Resolve(DefaultUkcPath), Encoding.UTF8);
            Print(output, "UKC", await importer.ImportAsync(DataSource.Ukc, null, UkcParser.Parse(reader).Select(corrections.Apply).ToList()));
        }

        if (what is "mp" or "all")
        {
            using var reader = new StreamReader(what == "mp" && path is not null ? path : Resolve(DefaultMountainProjectPath), Encoding.UTF8);
            Print(output, "Mountain Project",
                await importer.ImportAsync(DataSource.MountainProject, null, MountainProjectParser.Parse(reader).Select(corrections.Apply).ToList()));
        }

        var trips = await new GenericTripService(target).RebuildAsync(corrections.TripMerges);
        await output.WriteLineAsync($"Trips: {trips.Trips} trips, {trips.LinkedAscents} ascents linked, {trips.InferredLinks} inferred");
        return 0;
    }

    private static void Print(TextWriter output, string source, ImportSummary summary)
    {
        output.WriteLine($"{source} (batch {summary.BatchId}): {summary.Rows} rows");
        output.WriteLine($"  Areas:    {summary.Areas.Created} created, {summary.Areas.Matched} matched");
        output.WriteLine($"  Crags:    {summary.Crags.Created} created, {summary.Crags.Matched} matched");
        output.WriteLine($"  Routes:   {summary.Routes.Created} created, {summary.Routes.Matched} matched");
        output.WriteLine($"  Ascents:  {summary.Ascents.Created} created, {summary.Ascents.Matched} already imported");
        output.WriteLine($"  Partners: {summary.PartnersCreated} created");
    }

    private string Resolve(string relativePath) => (resolvePath ?? (path => RepoPaths.Resolve(path)))(relativePath);

    internal sealed class ConnectionFactory(string connectionString) : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connectionString).Options);
    }
}
