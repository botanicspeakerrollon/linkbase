using Linkbase.Domains;
using Npgsql;

namespace Linkbase.Core.Models;

// A model the app can show, each in its own database. Domains are the domain modules it uses, such as Climbing.
public sealed record ModelInfo(string Key, string Name, string ConnectionStringName, IReadOnlyList<string> Domains, int Order)
{
    // The home page's panels this model leaves out, from Models:<key>:Home:Hide: records, imports, changes or growth.
    public IReadOnlyList<string> HomeHidden { get; init; } = [];

    // A model file to load, from Models:<key>:Seed, when `model setup` finds the model empty. Models without one are left alone.
    public string? Seed { get; init; }

    // From Models:<key>:RowSecurity: `model setup` turns row security on once the model's file has a security section. For demos,
    // which should show it working. Elsewhere it's `model security on`.
    public bool RowSecurity { get; init; }

    public bool ShowsOnHome(string panel) => !HomeHidden.Contains(panel, StringComparer.OrdinalIgnoreCase);
}

// The models configured under Models, in order. With none configured, there's one model, using ConnectionStrings:Linkbase
// and every domain. With only one, the app has no switcher. See docs/models.md.
public sealed class ModelCatalog
{
    private readonly IConfiguration _configuration;

    public ModelCatalog(IConfiguration configuration)
    {
        _configuration = configuration;
        Models = configuration.GetSection("Models").GetChildren()
            .Select(section => new ModelInfo(
                section.Key,
                section["Name"] ?? section.Key,
                section["ConnectionString"] ?? section.Key,
                section.GetSection("Domains").GetChildren().Select(d => d.Value!).ToList(),
                int.TryParse(section["Order"], out var order) ? order : int.MaxValue)
            {
                HomeHidden = section.GetSection("Home:Hide").GetChildren().Select(h => h.Value!).ToList(),
                Seed = section["Seed"] is { Length: > 0 } seed ? seed : null,
                RowSecurity = section.GetValue("RowSecurity", false),
            })
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .ToList();

        if (Models.Count == 0)
        {
            Models = [new ModelInfo("default", RegisteredDomains.All[0].Name, "Linkbase", RegisteredDomains.All.Select(d => d.Key).ToList(), 0)];
        }
    }

    public IReadOnlyList<ModelInfo> Models { get; }

    public ModelInfo Default => Models[0];

    public ModelInfo? Find(string? key) => Models.FirstOrDefault(m => m.Key == key);

    // Every connection the app makes to a model comes from here. JIT is off: it spent more time compiling the graph views
    // and the Insights queries than it saved, and it never made a query faster. See docs/performance.md.
    public string ConnectionString(ModelInfo model)
    {
        var configured = _configuration.GetConnectionString(model.ConnectionStringName)
            ?? throw new InvalidOperationException($"The {model.Name} model needs ConnectionStrings:{model.ConnectionStringName}. See docs/models.md.");
        var builder = new NpgsqlConnectionStringBuilder(configured);
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? "-c jit=off" : $"{builder.Options} -c jit=off";
        return builder.ConnectionString;
    }
}
