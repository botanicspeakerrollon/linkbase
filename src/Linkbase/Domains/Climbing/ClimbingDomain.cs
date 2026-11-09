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
        new("ascents/new", "Add ascent", Icons.Material.Filled.Add, ExactMatch: true),
        new("browse", "Browse", Icons.Material.Outlined.AccountTree),
        new("import", "Import", Icons.Material.Outlined.CloudDownload),
        new("insights", "Insights", Icons.Material.Outlined.Insights),
    ];

    public IReadOnlyList<LabelStyle> LabelStyles { get; } =
    [
        new("Country", "#2563eb", Icons.Material.Filled.Public) { Link = node => $"/browse/area/{node.EntityId}" },
        new("Area", "#0e7490", Icons.Material.Filled.Map) { Link = node => $"/browse/area/{node.EntityId}" },
        new("Crag", "#a16207", Icons.Material.Filled.Terrain) { Link = node => $"/browse/crag/{node.EntityId}" },
        new("Route", "#7c3aed", Icons.Material.Filled.Timeline)
        {
            // Routes show their grade.
            Caption = node => node.Property("grade") is { } grade ? $"{node.Name} {grade}" : node.Name,
            Link = node => $"/browse/route/{node.EntityId}",
        },
        new("Ascent", "#15803d", Icons.Material.Filled.Flag)
        {
            // Captioned by date and style, because an ascent's name is its route's.
            Caption = AscentCaption,
            Link = node => $"/ascents/{node.EntityId}",
        },
        new("Partner", "#c2410c", Icons.Material.Filled.Person),
        new("Trip", "#be185d", Icons.Material.Filled.Flight),
    ];

    public IReadOnlyList<string> LabelsNamedAfterParent { get; } = ["Ascent"];

    public IReadOnlyList<string> LongTextProperties { get; } = ["notes", "description", "style_raw"];


    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AscentImportService>();
        services.AddScoped<TripService>();
        services.AddScoped<GenericTripService>();
        services.AddScoped<Linkbase.Core.Records.IRecordHook>(provider => new ClimbingRecordHook(
            provider.GetRequiredService<IDbContextFactory<LinkbaseDbContext>>(),
            provider.GetRequiredService<GenericTripService>(),
            () => RepoPaths.Resolve(RepoPaths.Corrections),
            provider.GetRequiredService<ILogger<ClimbingRecordHook>>()));
        services.AddScoped<ReconciliationService>();
        services.AddScoped<AreaRepository>();
        services.AddScoped<AscentRepository>();
        services.AddScoped<CragRepository>();
        services.AddScoped<RouteRepository>();
        services.AddScoped<PartnerRepository>();
        services.AddScoped<InsightsRepository>();
        services.AddScoped<WebImportRepository>();
        services.AddScoped<WebImportService>();
        services.AddHttpClient<PageTextFetcher>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IClimbingExtractor>(_ =>
            configuration["WebImport:FixtureExtraction"] is { Length: > 0 } fixture
                ? new FixtureClimbingExtractor(RepoPaths.Resolve(fixture))
                : new AnthropicClimbingExtractor(configuration));
        services.AddScoped(provider => new TripRefresher(
            provider.GetRequiredService<TripService>(),
            () => RepoPaths.Resolve(RepoPaths.Corrections),
            provider.GetRequiredService<ILogger<TripRefresher>>()));
    }

    public async Task<int?> TryRunCommandAsync(string[] args, IServiceProvider services, IConfiguration configuration)
    {
        if (GenericCommands.IsCommand(args))
        {
            await using var genericScope = services.CreateAsyncScope();
            return await new GenericCommands(genericScope.ServiceProvider.GetRequiredService<ModelCatalog>(), Console.Out).RunAsync(args);
        }

        if (!AdminCommands.IsCommand(args))
        {
            return null;
        }

        await using var scope = services.CreateAsyncScope();
        var commands = new AdminCommands(
            scope.ServiceProvider.GetRequiredService<AscentImportService>(),
            scope.ServiceProvider.GetRequiredService<TripService>(),
            scope.ServiceProvider.GetRequiredService<ReconciliationService>(),
            Console.Out);
        return await commands.RunAsync(args);
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
