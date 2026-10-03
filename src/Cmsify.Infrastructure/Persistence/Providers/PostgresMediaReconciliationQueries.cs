using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class PostgresMediaReconciliationQueries(CmsifyDbContext dbContext) : IMediaReconciliationQueries
{
    public Task<List<MediaDeletionIntent>> LockDeletionIntentsAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        dbContext.MediaDeletionIntents.FromSqlInterpolated($"""
            SELECT * FROM media_deletion_intents
            WHERE completed_at IS NULL AND not_before <= {now} AND next_attempt_at <= {now}
              AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            ORDER BY next_attempt_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);

    public Task<MediaDeletionIntent?> LockFencedIntentAsync(MediaDeletionClaim claim, DateTimeOffset now, CancellationToken ct) =>
        dbContext.MediaDeletionIntents.FromSqlInterpolated($"""
            SELECT * FROM media_deletion_intents
            WHERE id = {claim.Id} AND completed_at IS NULL
              AND lease_owner = {claim.LeaseOwner} AND lease_token = {claim.LeaseToken}
              AND lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);

    public Task<List<Guid>> LockStaleUploadIdsAsync(DateTimeOffset cutoff, int limit, CancellationToken ct) =>
        dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM media_assets
            WHERE blob_state = 'PendingUpload' AND blob_state_changed_at <= {cutoff} AND NOT is_deleted
            ORDER BY blob_state_changed_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);

    public Task InsertCheckpointAsync(string provider, string prefix, DateTimeOffset now, CancellationToken ct) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO media_reconciliation_checkpoints
                (id, provider, prefix, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {provider}, {prefix}, {now}, {now})
            ON CONFLICT (provider, prefix) DO NOTHING
            """, ct);

    public Task<MediaReconciliationCheckpoint?> LockCheckpointAsync(string provider, string prefix, DateTimeOffset now, CancellationToken ct) =>
        dbContext.MediaReconciliationCheckpoints.FromSqlInterpolated($"""
            SELECT * FROM media_reconciliation_checkpoints
            WHERE provider = {provider} AND prefix = {prefix}
              AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(ct);

    public Task<MediaReconciliationCheckpoint?> LockFencedCheckpointAsync(MediaCheckpointClaim claim, DateTimeOffset now, CancellationToken ct) =>
        dbContext.MediaReconciliationCheckpoints.FromSqlInterpolated($"""
            SELECT * FROM media_reconciliation_checkpoints
            WHERE id = {claim.Id} AND lease_owner = {claim.LeaseOwner} AND lease_token = {claim.LeaseToken}
              AND lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);

    public Task InsertOrphanIntentAsync(string provider, string storageKey, DateTimeOffset now, CancellationToken ct) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO media_deletion_intents
                (id, provider, storage_key, reason, not_before, next_attempt_at, attempt_count, created_at)
            VALUES ({Guid.CreateVersion7()}, {provider}, {storageKey}, 'orphan', {now}, {now}, 0, {now})
            ON CONFLICT (provider, storage_key) WHERE completed_at IS NULL DO NOTHING
            """, ct);
}
