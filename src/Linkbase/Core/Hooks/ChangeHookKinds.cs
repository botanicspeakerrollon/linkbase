namespace Linkbase.Core.Hooks;

// Every kind of hook the app knows. Adding one here is the last step of linking a new kind in.
//
//   "Hooks": { "audit-log": { "Kind": "console", "Models": ["default"], "BatchSize": 100 } }
//
// Models names the models the hook hears of (none means all) and BatchSize how many events it is given at once.
public static class ChangeHookKinds
{
    public const int DefaultBatchSize = 100;
    public const int MaxBatchSize = 1000;

    public static IReadOnlyList<ChangeHookKind> All { get; } = [new ConsoleHookKind()];

    // Checks what Hooks names, so a hook of an unknown kind stops the app at start-up with the reason.
    public static IReadOnlyList<(string Key, ChangeHookKind Kind, IConfigurationSection Section)> Parse(IConfiguration configuration) =>
        configuration.GetSection("Hooks").GetChildren().Select(section =>
        {
            var id = (section["Kind"] ?? "").Trim().ToLowerInvariant();
            var kind = All.FirstOrDefault(k => k.Id == id)
                ?? throw new InvalidOperationException($"Hooks:{section.Key}:Kind is '{id}', which isn't one of: {string.Join(", ", All.Select(k => k.Id))}.");
            return (section.Key, kind, section);
        }).ToList();

    public static IReadOnlyList<ConfiguredHook> Build(IConfiguration configuration, IServiceProvider services) =>
        Parse(configuration).Select(p => new ConfiguredHook(
            p.Key,
            p.Section.GetSection("Models").GetChildren().Select(m => m.Value!).ToList(),
            Math.Clamp(p.Section.GetValue("BatchSize", DefaultBatchSize), 1, MaxBatchSize),
            p.Kind.Create(p.Key, p.Section, services))).ToList();

    // Runs the hooks in the background, when any are configured. Without any, nothing here runs.
    public static IServiceCollection AddChangeHooks(this IServiceCollection services, IConfiguration configuration)
    {
        if (Parse(configuration).Count > 0)
        {
            services.AddHostedService(sp => new ChangeDispatcherService(
                Build(configuration, sp), sp.GetRequiredService<Models.ModelCatalog>(), sp.GetRequiredService<ILogger<ChangeDispatcherService>>()));
        }

        return services;
    }
}
