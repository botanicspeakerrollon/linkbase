using Linkbase.Core;
using Linkbase.Data;
using Linkbase.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace Linkbase.Core.Models;

// The model this circuit, or this command, is working with. Everything that reads the database follows it.
public sealed class CurrentModel(ModelCatalog catalog, DomainRegistry registry)
{
    // Remembers the choice between visits. Routes reads it as each circuit starts.
    public const string Cookie = "linkbase-model";

    public ModelInfo Info { get; private set; } = catalog.Default;

    public IEnumerable<IDomainModule> Domains => registry.Used(Info.Domains);

    // An unknown key, such as one from an old cookie, falls back to the first model.
    public void Select(string? key) => Info = catalog.Find(key) ?? catalog.Default;
}

public sealed class ModelDbContextFactory(ModelCatalog catalog, CurrentModel current, ViewerConnectionInterceptor viewer) : IDbContextFactory<LinkbaseDbContext>
{
    // The interceptor tells the database who is asking when the model has row security on, and does nothing otherwise.
    public LinkbaseDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(current.Info)).AddInterceptors(viewer).Options);
}
