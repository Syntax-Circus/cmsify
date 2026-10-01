using Cmsify.Core.Interfaces.Repositories;

namespace Cmsify.Infrastructure.Persistence.Providers;

// Infrastructure contract: callers own the write transaction and lease mutation.
internal interface IScheduledPublicationQueries
{
    Task<List<Guid>> LockDueIdsAsync(DateTimeOffset now, int limit, CancellationToken ct);
    Task<Guid> LockOwnedIdAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct);
}

internal static class ScheduledPublicationQueries
{
    public static IScheduledPublicationQueries Create(CmsifyDbContext context) => context.Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => new PostgresScheduledPublicationQueries(context),
        "Microsoft.EntityFrameworkCore.Sqlite" => new SqliteScheduledPublicationQueries(context),
        _ => throw new NotSupportedException($"Unsupported scheduled-publication provider: {context.Database.ProviderName}")
    };
}
