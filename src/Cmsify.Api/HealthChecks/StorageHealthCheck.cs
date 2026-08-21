using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cmsify.Api.HealthChecks;

public sealed class StorageHealthCheck : IHealthCheck
{
    private readonly IStorageProvider storageProvider;

    public StorageHealthCheck(IStorageProvider storageProvider) => this.storageProvider = storageProvider;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await storageProvider.ExistsAsync(".cmsify-healthcheck", cancellationToken);
            return HealthCheckResult.Healthy("Storage provider is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Storage provider readiness check failed.", ex);
        }
    }
}
