using Linkbase.Core;
using Linkbase.Core.Explore;
using Linkbase.Core.Graph;
using Linkbase.Core.Models;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Domains.Climbing.Features;
using Linkbase.Domains.Climbing.Import;
using Linkbase.Domains.Climbing.Import.Web;
using Microsoft.EntityFrameworkCore;
using MudBlazor;

namespace Linkbase.Domains.Climbing;

public sealed class ClimbingDomain : IDomainModule
{
    public string Key => "Climbing";

    public string Name => "Climbing Logbook";

    public string Icon => Icons.Material.Filled.Terrain;

    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new("logbook", "Logbook", Icons.Material.Outlined.MenuBook),
        new("import", "Import", Icons.Material.Outlined.CloudDownload),
        new("insights", "Insights", Icons.Material.Outlined.Insights),
    ];

    public string? GuideHref => "guide/climbing";

    public IReadOnlyList<LabelStyle> LabelStyles { get; } =
    [
        new("Area", "#0e7490", Icons.Material.Filled.Map),
        new("Area", "#2563eb", Icons.Material.Filled.Public) { Kind = "country" },
        new("Crag", "#a16207", Icons.Material.Filled.Terrain),
        new("Route", "#7c3aed", Icons.Material.Filled.Timeline)
        {
            // Routes show their grade.
            Caption = node => node.Property("grade") is { } grade ? $"{node.Name} {grade}" : node.Name,
        },
        new("Ascent", "#15803d", Icons.Material.Filled.Flag)
        {
            // Captioned by date and style, because an ascent's name is its route's.
            Caption = AscentCaption,
        },
        new("Partner", "#c2410c", Icons.Material.Filled.Person),
        new("Trip", "#be185d", Icons.Material.Filled.Flight),
    ];

    public IReadOnlyList<string> LabelsNamedAfterParent { get; } = ["Ascent"];

    public IReadOnlyList<string> LongTextProperties { get; } = ["notes", "description", "style_raw"];


    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<GenericAscentImportService>();
        services.AddScoped<GenericTripService>();
        services.AddScoped<Linkbase.Core.Records.IRecordHook>(provider => new ClimbingRecordHook(
            provider.GetRequiredService<IDbContextFactory<LinkbaseDbContext>>(),
            provider.GetRequiredService<GenericTripService>(),
            () => RepoPaths.Resolve(RepoPaths.Corrections),
            provider.GetRequiredService<ILogger<ClimbingRecordHook>>()));
        services.AddScoped<LogbookRepository>();
        services.AddScoped<InsightsRepository>();
        services.AddScoped<WebImportRepository>();
        services.AddScoped<WebImportService>();
        services.AddHttpClient<PageTextFetcher>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IClimbingExtractor>(_ =>
            configuration["WebImport:FixtureExtraction"] is { Length: > 0 } fixture
                ? new FixtureClimbingExtractor(RepoPaths.Resolve(fixture))
                : new AnthropicClimbingExtractor(configuration));
    }

    public async Task<int?> TryRunCommandAsync(string[] args, IServiceProvider services, IConfiguration configuration)
    {
        if (GenericImportCommands.IsCommand(args))
        {
            await using var importScope = services.CreateAsyncScope();
            return await GenericImportCommands.RunAsync(importScope.ServiceProvider.GetRequiredService<ModelCatalog>(), args, Console.Out);
        }

        return null;
    }

    private static string AscentCaption(GraphNode node)
    {
        var date = node.Property("date") is { } text && DateOnly.TryParse(text, out var parsed)
            ? Labels.Date(parsed, ParsePrecision(node.Property("date_precision")))
            : "";
        return $"{date} {node.Property("style")?.Replace('_', ' ')}".Trim();
    }

    private static DatePrecision ParsePrecision(string? value) => value switch
    {
        "month" => DatePrecision.Month,
        "year" => DatePrecision.Year,
        _ => DatePrecision.Day,
    };
}
