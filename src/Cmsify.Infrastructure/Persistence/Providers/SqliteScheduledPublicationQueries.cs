using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class SqliteScheduledPublicationQueries(CmsifyDbContext dbContext) : IScheduledPublicationQueries
{
    // Microsoft.Data.Sqlite's ordinary transaction takes the write reservation
    // before selection. Unlike PostgreSQL, concurrent writers wait, not SKIP LOCKED.
    public Task<List<Guid>> LockDueIdsAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        RequireTransaction();
        return dbContext.ContentVersions
            .Where(version => version.Status == ContentStatus.Approved && version.PublishAt <= now &&
                dbContext.ContentItems.Any(item => item.Id == version.ContentItemId) &&
                (version.PublishLeaseExpiresAt == null || version.PublishLeaseExpiresAt <= now))
            .OrderBy(version => version.PublishAt).ThenBy(version => version.Id)
            .Select(version => version.Id).Take(limit).ToListAsync(ct);
    }

    public Task<Guid> LockOwnedIdAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct)
    {
        RequireTransaction();
        return dbContext.ContentVersions.Where(version => version.Id == claim.ContentVersionId &&
                version.Status == ContentStatus.Approved && version.PublishAt <= now &&
                version.PublishLeaseOwner == claim.LeaseOwner && version.PublishLeaseToken == claim.LeaseToken &&
                version.PublishLeaseExpiresAt > now)
            .Select(version => version.Id).SingleOrDefaultAsync(ct);
    }

    private void RequireTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Scheduled claim selection requires a write transaction.");
    }
}
