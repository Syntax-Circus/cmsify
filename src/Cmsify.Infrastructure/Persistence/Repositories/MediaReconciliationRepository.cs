using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed record MediaDeletionClaim(
    Guid Id,
    Guid? MediaAssetId,
    string Provider,
    string StorageKey,
    int AttemptCount,
    string LeaseOwner,
    Guid LeaseToken,
    bool WasReclaimed,
    string Reason = "unknown");

public sealed record MediaCheckpointClaim(
    Guid Id,
    string Provider,
    string Prefix,
    string? AfterKey,
    string LeaseOwner,
    Guid LeaseToken,
    bool WasReclaimed);

public sealed record MediaVerificationCandidate(Guid Id, string Provider, string StorageKey, MediaBlobState State);

public interface IMediaReconciliationRepository
{
    Task<IReadOnlyList<MediaDeletionClaim>> ClaimDeletionIntentsAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default);
    Task<bool> CompleteDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> RetryDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, DateTimeOffset nextAttemptAt, string error, CancellationToken ct = default);
    Task<int> FailStaleUploadsAsync(DateTimeOffset cutoff, DateTimeOffset now, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MediaVerificationCandidate>> GetVerificationBatchAsync(int limit, CancellationToken ct = default);
    Task RecordBlobMissingAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default);
    Task RecordBlobPresentAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> StorageKeyExistsAsync(string provider, string storageKey, CancellationToken ct = default);
    Task EnqueueOrphanDeletionAsync(string provider, string storageKey, DateTimeOffset now, CancellationToken ct = default);
    Task<MediaCheckpointClaim?> ClaimCheckpointAsync(string provider, string prefix, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<bool> CompleteCheckpointAsync(MediaCheckpointClaim claim, string? nextAfterKey, bool completedPrefix, DateTimeOffset now, CancellationToken ct = default);
}

public sealed class MediaReconciliationRepository(CmsifyDbContext dbContext) : IMediaReconciliationRepository
{
    public async Task<IReadOnlyList<MediaDeletionClaim>> ClaimDeletionIntentsAsync(
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int limit,
        CancellationToken ct = default)
    {
        ValidateClaimArguments(workerId, leaseDuration, limit);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var intents = await dbContext.MediaDeletionIntents.FromSqlInterpolated($"""
            SELECT * FROM media_deletion_intents
            WHERE completed_at IS NULL AND not_before <= {now} AND next_attempt_at <= {now}
              AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            ORDER BY next_attempt_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);

        var reclaimed = new Dictionary<Guid, bool>();
        foreach (var intent in intents)
        {
            reclaimed[intent.Id] = intent.LeaseExpiresAt.HasValue;
            intent.LeaseOwner = workerId;
            intent.LeaseToken = Guid.CreateVersion7();
            intent.LeaseExpiresAt = now.Add(leaseDuration);
        }

        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var claims = intents.Select(intent => new MediaDeletionClaim(
            intent.Id,
            intent.MediaAssetId,
            intent.Provider,
            intent.StorageKey,
            intent.AttemptCount,
            intent.LeaseOwner!,
            intent.LeaseToken!.Value,
            reclaimed[intent.Id],
            intent.Reason)).ToArray();
        CmsifyOperationalMetrics.ReportMediaPendingDeletion(
            await dbContext.MediaDeletionIntents.CountAsync(intent => intent.CompletedAt == null, ct));
        return claims;
    }

    public async Task<bool> CompleteDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var intent = await GetFencedIntentAsync(claim, now, ct);
        if (intent is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        intent.CompletedAt = now;
        ClearLease(intent);
        if (intent.MediaAssetId.HasValue)
        {
            var asset = await dbContext.MediaAssets.IgnoreQueryFilters()
                .SingleOrDefaultAsync(item => item.Id == intent.MediaAssetId.Value, ct);
            if (asset?.BlobState is MediaBlobState.DeletePending or MediaBlobState.UploadFailed)
            {
                asset.TransitionBlobState(MediaBlobState.Deleted, now);
            }
        }

        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RetryDeletionAsync(
        MediaDeletionClaim claim,
        DateTimeOffset now,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken ct = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var intent = await GetFencedIntentAsync(claim, now, ct);
        if (intent is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        intent.AttemptCount++;
        intent.NextAttemptAt = nextAttemptAt;
        intent.LastError = error.Length <= 2_000 ? error : error[..2_000];
        ClearLease(intent);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<int> FailStaleUploadsAsync(
        DateTimeOffset cutoff,
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1_000);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var ids = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM media_assets
            WHERE blob_state = 'PendingUpload' AND blob_state_changed_at <= {cutoff} AND NOT is_deleted
            ORDER BY blob_state_changed_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);
        var assets = await dbContext.MediaAssets.Where(asset => ids.Contains(asset.Id)).ToListAsync(ct);

        foreach (var asset in assets)
        {
            asset.TransitionBlobState(MediaBlobState.UploadFailed, now);
            dbContext.MediaDeletionIntents.Add(new MediaDeletionIntent
            {
                MediaAssetId = asset.Id,
                Provider = asset.StorageProvider,
                StorageKey = asset.StorageKey,
                Reason = "abandoned_upload",
                NotBefore = now,
                NextAttemptAt = now,
                CreatedAt = now
            });
        }

        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return assets.Count;
    }

    public async Task<MediaCheckpointClaim?> ClaimCheckpointAsync(
        string provider,
        string prefix,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ValidateClaimArguments(workerId, leaseDuration, 1);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO media_reconciliation_checkpoints
                (id, provider, prefix, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {provider}, {prefix}, {now}, {now})
            ON CONFLICT (provider, prefix) DO NOTHING
            """, ct);
        var checkpoint = await dbContext.MediaReconciliationCheckpoints.FromSqlInterpolated($"""
            SELECT * FROM media_reconciliation_checkpoints
            WHERE provider = {provider} AND prefix = {prefix}
              AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            FOR UPDATE SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (checkpoint is null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var wasReclaimed = checkpoint.LeaseExpiresAt.HasValue;
        checkpoint.LeaseOwner = workerId;
        checkpoint.LeaseToken = Guid.CreateVersion7();
        checkpoint.LeaseExpiresAt = now.Add(leaseDuration);
        checkpoint.LastScanStartedAt = now;
        checkpoint.UpdatedAt = now;
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MediaCheckpointClaim(
            checkpoint.Id, provider, prefix, checkpoint.AfterKey, workerId, checkpoint.LeaseToken.Value, wasReclaimed);
    }

    public async Task<IReadOnlyList<MediaVerificationCandidate>> GetVerificationBatchAsync(
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1_000);
        return await dbContext.MediaAssets.AsNoTracking()
            .Where(asset => asset.BlobState == MediaBlobState.Available || asset.BlobState == MediaBlobState.Missing)
            .OrderBy(asset => asset.BlobVerifiedAt)
            .ThenBy(asset => asset.Id)
            .Take(limit)
            .Select(asset => new MediaVerificationCandidate(asset.Id, asset.StorageProvider, asset.StorageKey, asset.BlobState))
            .ToListAsync(ct);
    }

    public async Task RecordBlobMissingAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default)
    {
        var asset = await dbContext.MediaAssets.SingleOrDefaultAsync(item => item.Id == assetId, ct);
        if (asset?.BlobState == MediaBlobState.Available)
        {
            asset.TransitionBlobState(MediaBlobState.Missing, now);
            await dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task RecordBlobPresentAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default)
    {
        var asset = await dbContext.MediaAssets.SingleOrDefaultAsync(item => item.Id == assetId, ct);
        if (asset?.BlobState == MediaBlobState.Missing)
        {
            asset.TransitionBlobState(MediaBlobState.Available, now);
        }
        else if (asset?.BlobState == MediaBlobState.Available)
        {
            asset.BlobVerifiedAt = now;
        }

        if (asset is not null) await dbContext.SaveChangesAsync(ct);
    }

    public Task<bool> StorageKeyExistsAsync(string provider, string storageKey, CancellationToken ct = default) =>
        dbContext.MediaAssets.IgnoreQueryFilters().AnyAsync(
            asset => asset.StorageProvider == provider && asset.StorageKey == storageKey && asset.BlobState != MediaBlobState.Deleted,
            ct);

    public async Task EnqueueOrphanDeletionAsync(
        string provider,
        string storageKey,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO media_deletion_intents
                (id, provider, storage_key, reason, not_before, next_attempt_at, attempt_count, created_at)
            VALUES ({Guid.CreateVersion7()}, {provider}, {storageKey}, 'orphan', {now}, {now}, 0, {now})
            ON CONFLICT (provider, storage_key) WHERE completed_at IS NULL DO NOTHING
            """, ct);
    }

    public async Task<bool> CompleteCheckpointAsync(
        MediaCheckpointClaim claim,
        string? nextAfterKey,
        bool completedPrefix,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var checkpoint = await dbContext.MediaReconciliationCheckpoints.FromSqlInterpolated($"""
            SELECT * FROM media_reconciliation_checkpoints
            WHERE id = {claim.Id} AND lease_owner = {claim.LeaseOwner} AND lease_token = {claim.LeaseToken}
              AND lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (checkpoint is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        checkpoint.AfterKey = completedPrefix ? null : nextAfterKey;
        checkpoint.LastScanCompletedAt = completedPrefix ? now : checkpoint.LastScanCompletedAt;
        checkpoint.UpdatedAt = now;
        checkpoint.LeaseOwner = null;
        checkpoint.LeaseToken = null;
        checkpoint.LeaseExpiresAt = null;
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private Task<MediaDeletionIntent?> GetFencedIntentAsync(MediaDeletionClaim claim, DateTimeOffset now, CancellationToken ct) =>
        dbContext.MediaDeletionIntents.FromSqlInterpolated($"""
            SELECT * FROM media_deletion_intents
            WHERE id = {claim.Id} AND completed_at IS NULL
              AND lease_owner = {claim.LeaseOwner} AND lease_token = {claim.LeaseToken}
              AND lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);

    private static void ClearLease(MediaDeletionIntent intent)
    {
        intent.LeaseOwner = null;
        intent.LeaseToken = null;
        intent.LeaseExpiresAt = null;
    }

    private static void ValidateClaimArguments(string workerId, TimeSpan leaseDuration, int limit)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200) throw new ArgumentException("Worker ID is invalid.", nameof(workerId));
        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (limit is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(limit));
    }
}
