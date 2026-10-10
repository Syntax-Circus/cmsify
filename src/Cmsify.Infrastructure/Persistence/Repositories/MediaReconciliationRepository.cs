using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence.Providers;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
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

public enum DeletionPreparationResult
{
    ClaimLost,
    Ready,
    Owned,
    Retained
}

public interface IMediaReconciliationRepository
{
    Task<IReadOnlyList<MediaDeletionClaim>> ClaimDeletionIntentsAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default);
    Task<DeletionPreparationResult> PrepareDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<bool> CompleteDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> RetryDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, DateTimeOffset nextAttemptAt, string error, CancellationToken ct = default);
    Task<int> FailStaleUploadsAsync(DateTimeOffset cutoff, DateTimeOffset now, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MediaVerificationCandidate>> GetVerificationBatchAsync(string provider, int limit, CancellationToken ct = default);
    Task RecordBlobMissingAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default);
    Task RecordBlobPresentAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> StorageKeyExistsAsync(string provider, string storageKey, CancellationToken ct = default);
    Task EnqueueOrphanDeletionAsync(string provider, string storageKey, DateTimeOffset now, CancellationToken ct = default);
    Task<MediaCheckpointClaim?> ClaimCheckpointAsync(string provider, string prefix, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<bool> CompleteCheckpointAsync(MediaCheckpointClaim claim, string? nextAfterKey, bool completedPrefix, DateTimeOffset now, CancellationToken ct = default);
}

public sealed class MediaReconciliationRepository(CmsifyDbContext dbContext) : IMediaReconciliationRepository
{
    private readonly IMediaReconciliationQueries queries = MediaReconciliationQueries.Create(dbContext);
    public async Task<IReadOnlyList<MediaDeletionClaim>> ClaimDeletionIntentsAsync(
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int limit,
        CancellationToken ct = default)
    {
        ValidateClaimArguments(workerId, leaseDuration, limit);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var intents = await queries.LockDeletionIntentsAsync(now, limit, ct);

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
        if (claim.MediaAssetId.HasValue)
            await EmbeddedPhotoFences.LockAssetAsync(dbContext, claim.MediaAssetId.Value, ct);
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

    public async Task<DeletionPreparationResult> PrepareDeletionAsync(
        MediaDeletionClaim claim,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var assetId = claim.MediaAssetId ?? await EmbeddedPhotoFences.FindAssetForKeyAsync(dbContext, claim.Provider, claim.StorageKey, ct);
        var asset = assetId.HasValue ? await EmbeddedPhotoFences.LockAssetAsync(dbContext, assetId.Value, ct) : null;
        var intent = await GetFencedIntentAsync(claim, now, ct);
        if (intent is null)
        {
            await transaction.RollbackAsync(ct);
            return DeletionPreparationResult.ClaimLost;
        }

        if (asset is not null && (await EmbeddedPhotoFences.ProtectedAsync(dbContext, asset.Id, ct) ||
            intent.Reason != "orphan" && asset.BlobState is MediaBlobState.Available or MediaBlobState.Missing or MediaBlobState.PendingUpload ||
            (await EmbeddedPhotoFences.ContentReferencesAsync(dbContext, asset.Id, true, ct)).Count > 0 ||
            asset.PurgeAfter > now))
        {
            intent.NextAttemptAt = now.AddMinutes(5);
            ClearLease(intent);
            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return DeletionPreparationResult.Retained;
        }

        if (string.Equals(intent.Reason, "orphan", StringComparison.Ordinal) &&
            await StorageKeyExistsAsync(intent.Provider, intent.StorageKey, ct))
        {
            intent.CompletedAt = now;
            ClearLease(intent);
            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return DeletionPreparationResult.Owned;
        }

        intent.LeaseExpiresAt = now.Add(leaseDuration);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return DeletionPreparationResult.Ready;
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
        var ids = await queries.LockStaleUploadIdsAsync(cutoff, limit, ct);
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
        var abandonedPhotos = await EmbeddedPhotoRetirement.RetireAsync(dbContext, cutoff, now, limit, ct);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return assets.Count + abandonedPhotos;
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
        await queries.InsertCheckpointAsync(provider, prefix, now, ct);
        var checkpoint = await queries.LockCheckpointAsync(provider, prefix, now, ct);
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
        string provider,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1_000);
        return await dbContext.MediaAssets.AsNoTracking()
            .Where(asset => asset.StorageProvider == provider &&
                            (asset.BlobState == MediaBlobState.Available || asset.BlobState == MediaBlobState.Missing))
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

    public async Task<bool> StorageKeyExistsAsync(string provider, string storageKey, CancellationToken ct = default)
    {
        if (await dbContext.MediaAssets.IgnoreQueryFilters().AnyAsync(
            asset => asset.StorageProvider == provider && asset.StorageKey == storageKey && asset.BlobState != MediaBlobState.Deleted,
            ct)) return true;

        // The optional photo model is PostgreSQL-only; legacy SQLite media stays unchanged.
        if (!dbContext.Database.IsNpgsql()) return false;
        return await dbContext.PhotoOriginals.AnyAsync(
                   item => item.StorageProvider == provider && item.StorageKey == storageKey &&
                       dbContext.MediaAssets.IgnoreQueryFilters().Any(asset => asset.Id == item.AssetId && asset.BlobState != MediaBlobState.Deleted), ct) ||
               await dbContext.PhotoVariants.AnyAsync(
                   item => item.StorageProvider == provider && item.StorageKey == storageKey &&
                       dbContext.MediaAssets.IgnoreQueryFilters().Any(asset => asset.Id == item.AssetId && asset.BlobState != MediaBlobState.Deleted), ct) ||
               await dbContext.PhotoUploadAttempts.AnyAsync(
                   item => item.StorageProvider == provider && item.StorageKey == storageKey && item.RetiredAt == null, ct) ||
               await dbContext.PhotoProcessingAttempts.AnyAsync(
                   item => item.StorageProvider == provider && item.StorageKey == storageKey && item.RetiredAt == null, ct);
    }

    public async Task EnqueueOrphanDeletionAsync(
        string provider,
        string storageKey,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await queries.InsertOrphanIntentAsync(provider, storageKey, now, ct);
    }

    public async Task<bool> CompleteCheckpointAsync(
        MediaCheckpointClaim claim,
        string? nextAfterKey,
        bool completedPrefix,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var checkpoint = await queries.LockFencedCheckpointAsync(claim, now, ct);
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
        queries.LockFencedIntentAsync(claim, now, ct);

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
