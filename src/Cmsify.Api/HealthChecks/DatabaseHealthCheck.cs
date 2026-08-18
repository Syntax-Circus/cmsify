using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cmsify.Api.HealthChecks;

public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly CmsifyDbContext dbContext;

    public DatabaseHealthCheck(CmsifyDbContext dbContext) => this.dbContext = dbContext;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
        if (!canConnect)
        {
            return HealthCheckResult.Unhealthy("Database is unreachable.");
        }

        var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        return pendingMigrations.Length == 0
            ? HealthCheckResult.Healthy("Database is reachable and up to date.")
            : HealthCheckResult.Unhealthy($"Database has {pendingMigrations.Length} pending migration(s).");
    }
}
