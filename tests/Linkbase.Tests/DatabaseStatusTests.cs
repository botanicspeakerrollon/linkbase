using Linkbase.Core.Extraction;
using Linkbase.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Linkbase.Data;
using Linkbase.Domains.Climbing.Data;
using Linkbase.Core.Graph;
using Linkbase.Domains.Climbing.Features;
using Linkbase.Core.Explore;
using Linkbase.Domains.Climbing.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkbase.Tests;

[Trait("Category", "Database")]
public class DatabaseStatusTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Database_status_reports_connected_and_up_to_date()
    {
        var status = await new DatabaseStatusService(fixture.ContextFactory(), NullLogger<DatabaseStatusService>.Instance).GetStatusAsync();
        Assert.True(status.Connected);
        Assert.Equal(0, status.PendingMigrations);
        Assert.True(status.AppliedMigrations > 0);
        Assert.Null(status.Error);
    }

    private sealed class BrokenFactory : IDbContextFactory<LinkbaseDbContext>
    {
        public LinkbaseDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LinkbaseDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1").Options);
    }

    [Fact]
    public async Task Database_status_reports_a_failed_connection_without_throwing()
    {
        var status = await new DatabaseStatusService(new BrokenFactory(), NullLogger<DatabaseStatusService>.Instance).GetStatusAsync();
        Assert.False(status.Connected);
        Assert.NotNull(status.Error);
    }
}
