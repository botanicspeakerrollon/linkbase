using Linkbase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Linkbase.Core.Models;

// Ready means every configured model's database can be reached and has every migration, so a platform only sends
// traffic to a pod that can serve each model. Migrations are never applied here: that's `model migrate`'s job.
public sealed class ModelDatabasesHealthCheck(ModelCatalog catalog) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var data = new Dictionary<string, object>();
        foreach (var model in catalog.Models)
        {
            try
            {
                await using var db = new LinkbaseDbContext(new DbContextOptionsBuilder<LinkbaseDbContext>().UseNpgsql(catalog.ConnectionString(model)).Options);
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
                data[model.Key] = pending == 0 ? "ready" : $"{pending} migration(s) pending";
                if (pending > 0)
                {
                    problems.Add($"{model.Key} has {pending} migration(s) pending");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The message, not the exception: a connection error can include the host, never a password, but the
                // stack trace says nothing a platform can act on.
                data[model.Key] = "unreachable";
                problems.Add($"{model.Key} is unreachable: {ex.Message}");
            }
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy("Every model's database is ready.", data)
            : HealthCheckResult.Unhealthy(string.Join("; ", problems), data: data);
    }
}
