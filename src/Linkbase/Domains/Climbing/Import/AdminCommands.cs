using Linkbase.Core;
using System.Text;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;

namespace Linkbase.Domains.Climbing.Import;

// One-off data jobs, run instead of the web app: dotnet run --project src/Linkbase -- import all
public sealed class AdminCommands(
    AscentImportService importer,
    TripService trips,
    ReconciliationService reconciliation,
    TextWriter output,
    Func<string, string>? resolvePath = null)
{
    private const string DefaultUkcPath = "data/samples/import.csv";
    private const string DefaultMountainProjectPath = "data/samples/ticks.csv";
    private const string DefaultCorrectionsPath = "data/corrections.json";
    private const string ReportPath = "data/samples/reconciliation.md";

    public static bool IsCommand(string[] args) => args is
        ["import", "ukc" or "mp" or "all", ..] or ["rebuild", "trips"] or ["report"];

    public async Task<int> RunAsync(string[] args)
    {
        switch (args)
        {
            case ["import", "ukc", .. var rest]:
                await ImportUkcAsync(rest.FirstOrDefault() ?? Resolve(DefaultUkcPath), LoadCorrections());
                return 0;
            case ["import", "mp", .. var rest]:
                await ImportMountainProjectAsync(rest.FirstOrDefault() ?? Resolve(DefaultMountainProjectPath), LoadCorrections());
                return 0;
            case ["import", "all"]:
                var corrections = LoadCorrections();
                await ImportUkcAsync(Resolve(DefaultUkcPath), corrections);
                await ImportMountainProjectAsync(Resolve(DefaultMountainProjectPath), corrections);
                await RebuildTripsAsync(corrections);
                await WriteReportAsync();
                return 0;
            case ["rebuild", "trips"]:
                await RebuildTripsAsync(LoadCorrections());
                return 0;
            case ["report"]:
                await WriteReportAsync();
                return 0;
            default:
                output.WriteLine($"Unknown command: {string.Join(' ', args)}");
                return 1;
        }
    }

    // Importing without corrections would load the wrong partners and trips, so a missing file stops the run.
    private Corrections LoadCorrections() => Corrections.Load(Resolve(DefaultCorrectionsPath));

    private string Resolve(string relativePath) => (resolvePath ?? (path => RepoPaths.Resolve(path)))(relativePath);

    private async Task ImportUkcAsync(string path, Corrections corrections)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var rows = UkcParser.Parse(reader).Select(corrections.Apply).ToList();
        Print(output, "UKC", await importer.ImportAsync(DataSource.Ukc, null, rows));
    }

    private async Task ImportMountainProjectAsync(string path, Corrections corrections)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var rows = MountainProjectParser.Parse(reader).Select(corrections.Apply).ToList();
        Print(output, "Mountain Project", await importer.ImportAsync(DataSource.MountainProject, null, rows));
    }

    private async Task RebuildTripsAsync(Corrections corrections)
    {
        var summary = await trips.RebuildAsync(corrections.TripMerges);
        output.WriteLine($"Trips: {summary.Trips} trips, {summary.LinkedAscents} ascents linked, {summary.InferredLinks} inferred");
    }

    internal static void Print(TextWriter output, string source, ImportSummary summary)
    {
        output.WriteLine($"{source} (batch {summary.BatchId}): {summary.Rows} rows");
        output.WriteLine($"  Areas:    {summary.Areas.Created} created, {summary.Areas.Matched} matched");
        output.WriteLine($"  Crags:    {summary.Crags.Created} created, {summary.Crags.Matched} matched");
        output.WriteLine($"  Routes:   {summary.Routes.Created} created, {summary.Routes.Matched} matched");
        output.WriteLine($"  Ascents:  {summary.Ascents.Created} created, {summary.Ascents.Matched} already imported");
        output.WriteLine($"  Partners: {summary.PartnersCreated} created");
    }

    private async Task WriteReportAsync()
    {
        var report = await reconciliation.BuildReportAsync();
        var text = new StringBuilder();
        text.AppendLine("# Reconciliation report");
        text.AppendLine();
        text.AppendLine("| Entity | Total | UKC refs | Mountain Project refs | No ref |");
        text.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var e in report.Entities)
        {
            text.AppendLine($"| {e.Entity} | {e.Total} | {e.Ukc} | {e.MountainProject} | {e.WithoutRef} |");
        }

        text.AppendLine();
        text.AppendLine($"- Partners: {report.Partners}, with {report.PartnerLinks} ascent links");
        text.AppendLine($"- Trips: {report.Trips}, with {report.TripLinks} ascent links, {report.InferredTripLinks} inferred");
        text.AppendLine($"- Partial dates: {report.MonthOnlyAscents} month-only, {report.YearOnlyAscents} year-only");

        var reportPath = Resolve(ReportPath);
        await File.WriteAllTextAsync(reportPath, text.ToString());
        output.WriteLine(text.ToString());
        output.WriteLine($"Written to {reportPath}");
    }
}
