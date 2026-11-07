using Linkbase.Core;
using Linkbase.Data;
using Linkbase.Domains;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Models;

// The model this circuit, or this command, is working with. Everything that reads the database follows it.
public sealed class CurrentModel(ModelCatalog catalog)
{
    // Remembers the choice between visits. Routes reads it as each circuit starts.
    public const string Cookie = "linkbase-model";

    public ModelInfo Info { get; private set; } = catalog.Default;

    public IEnumerable<IDomainModule> Domains => RegisteredDomains.All.Where(d => Info.Domains.Contains(d.Key));

    // An unknown key, such as one from an old cookie, falls back to the first model.
    public void Select(string? key) => Info = catalog.Find(key) ?? catalog.Default;
}

public sealed class ModelDbContextFactory(ModelCatalog catalog, CurrentModel current) : IDbContextFactory<LinkbaseDbContext>
{
    public LinkbaseDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(current.Info)).Options);
}
