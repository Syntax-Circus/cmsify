using Cmsify.Infrastructure.Persistence.Providers;

namespace Cmsify.Infrastructure.Persistence;

/// <summary>Conditional API-client last-use persistence, including provider concurrency tokens.</summary>
public static class ApiClientLastUseExtensions
{
    public static Task<int> TouchApiClientLastUsedIfDueAsync(this CmsifyDbContext context,
        Guid clientId, DateTimeOffset now, TimeSpan touchInterval, CancellationToken ct) =>
        ApiClientLastUseQueries.Create(context).TouchIfDueAsync(clientId, now, touchInterval, ct);
}
