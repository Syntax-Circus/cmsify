using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

internal static class EmbeddedPhotoRetirement
{
    // Caller owns the transaction. Revoke expired fences before inventory can become unowned.
    internal static async Task<int> RetireAsync(CmsifyDbContext db, DateTimeOffset cutoff, DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return 0;
        var abandoned = 0;
        var sessions = await db.PhotoUploadSessions.AsNoTracking()
            .Where(x => x.CreatedAt <= cutoff && (x.State == EmbeddedPhotoUploadState.Pending || x.State == EmbeddedPhotoUploadState.Active) &&
                (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now)).OrderBy(x => x.CreatedAt).Take(limit).ToListAsync(ct);
        foreach (var candidate in sessions)
        {
            var asset = await EmbeddedPhotoFences.LockAssetAsync(db, candidate.AssetId, ct);
            var session = await LockSessionAsync(db, candidate.Id, ct);
            if (session is null || session.CreatedAt > cutoff || session.LeaseExpiresAt > now ||
                session.State is not (EmbeddedPhotoUploadState.Pending or EmbeddedPhotoUploadState.Active)) continue;
            session.State = EmbeddedPhotoUploadState.Abandoned;
            session.LeaseToken = null; session.LeaseOwner = null; session.LeaseExpiresAt = null;
            if (asset?.BlobState == MediaBlobState.PendingUpload) asset.TransitionBlobState(MediaBlobState.UploadFailed, now);
            abandoned++;
        }
        await db.SaveChangesAsync(ct);

        var uploads = await db.PhotoUploadAttempts.AsNoTracking().Where(x => x.RetiredAt == null && x.CreatedAt <= cutoff)
            .OrderBy(x => x.CreatedAt).Take(limit).ToListAsync(ct);
        foreach (var candidate in uploads)
        {
            var assetId = await db.PhotoUploadSessions.Where(x => x.Id == candidate.SessionId).Select(x => x.AssetId).SingleAsync(ct);
            await EmbeddedPhotoFences.LockAssetAsync(db, assetId, ct);
            var session = await LockSessionAsync(db, candidate.SessionId, ct);
            if (session?.State == EmbeddedPhotoUploadState.Active && session.LeaseToken == candidate.Token && session.LeaseExpiresAt > now) continue;
            await db.PhotoUploadAttempts.Where(x => x.Id == candidate.Id && x.RetiredAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RetiredAt, now), ct);
        }
        var processing = await db.PhotoProcessingAttempts.AsNoTracking().Where(x => x.RetiredAt == null && x.CreatedAt <= cutoff)
            .OrderBy(x => x.CreatedAt).Take(limit).ToListAsync(ct);
        foreach (var candidate in processing)
        {
            var assetId = await db.PhotoJobs.Where(x => x.Id == candidate.JobId).Select(x => x.AssetId).SingleAsync(ct);
            await EmbeddedPhotoFences.LockAssetAsync(db, assetId, ct);
            var job = await db.PhotoJobs.FromSqlInterpolated($"SELECT * FROM embedded_photo_jobs WHERE id = {candidate.JobId} FOR UPDATE")
                .AsNoTracking().SingleAsync(ct);
            if (job.State == EmbeddedPhotoState.Processing && job.LeaseToken == candidate.Token && job.LeaseExpiresAt > now) continue;
            // Completion always checks expiry under this job lock. An expired token cannot commit.
            await db.PhotoProcessingAttempts.Where(x => x.Id == candidate.Id && x.RetiredAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RetiredAt, now), ct);
        }
        return abandoned;
    }

    private static async Task<PhotoUploadSessionRecord?> LockSessionAsync(CmsifyDbContext db, Guid id, CancellationToken ct)
    {
        var fresh = await db.PhotoUploadSessions.FromSqlInterpolated($"SELECT * FROM embedded_photo_upload_sessions WHERE id = {id} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        if (fresh is null) return null;
        var local = db.PhotoUploadSessions.Local.FirstOrDefault(x => x.Id == id);
        if (local is null) { db.PhotoUploadSessions.Attach(fresh); return fresh; }
        db.Entry(local).CurrentValues.SetValues(fresh); db.Entry(local).OriginalValues.SetValues(fresh); return local;
    }
}
