using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class PostgresScheduledPublicationQueries(CmsifyDbContext dbContext) : IScheduledPublicationQueries
{
    public Task<List<Guid>> LockDueIdsAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM content_versions
            WHERE status = 'Approved' AND publish_at <= {now}
              AND EXISTS (SELECT 1 FROM content_items ci WHERE ci.id = content_versions.content_item_id AND NOT ci.is_deleted)
              AND (publish_lease_expires_at IS NULL OR publish_lease_expires_at <= {now})
            ORDER BY publish_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);

    public Task<Guid> LockOwnedIdAsync(ScheduledContentClaimDto claim, DateTimeOffset now, CancellationToken ct) =>
        dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM content_versions
            WHERE id = {claim.ContentVersionId} AND status = 'Approved' AND publish_at <= {now}
              AND publish_lease_owner = {claim.LeaseOwner} AND publish_lease_token = {claim.LeaseToken}
              AND publish_lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
}
