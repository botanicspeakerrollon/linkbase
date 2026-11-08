using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Linkbase.Data;

// How `dotnet ef` builds the context, so it doesn't start the app to get one. It reads ConnectionStrings:Linkbase from
// user secrets or the environment, as the app does, and `--connection` overrides it.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LinkbaseDbContext>
{
    public LinkbaseDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(LinkbaseDbContext).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connection = configuration.GetConnectionString("Linkbase") ?? "Host=localhost;Database=linkbase";
        return new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(connection).Options);
    }
}
