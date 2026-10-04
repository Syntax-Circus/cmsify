using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class PostgresApiClientLastUseQueries(CmsifyDbContext context) : IApiClientLastUseQueries
{
    public Task<int> TouchIfDueAsync(Guid clientId, DateTimeOffset now, TimeSpan touchInterval, CancellationToken ct) =>
        context.ApiClients
            .Where(client => client.Id == clientId && (!client.LastUsedAt.HasValue || client.LastUsedAt.Value <= now - touchInterval))
            .ExecuteUpdateAsync(setters => setters.SetProperty(client => client.LastUsedAt, now), ct);
}
