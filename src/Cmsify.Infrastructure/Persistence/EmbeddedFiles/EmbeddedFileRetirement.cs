using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.EmbeddedFiles;

internal static class EmbeddedFileRetirement
{
    // Caller owns the transaction. Upload writers lock session before asset too.
    internal static async Task<int> RetireAsync(CmsifyDbContext db,DateTimeOffset cutoff,DateTimeOffset now,int limit,CancellationToken ct)
    {
        if(!db.Database.IsNpgsql())return 0;
        var count=0;
        var sessions=await db.FileUploadSessions.AsNoTracking().Where(x=>x.CreatedAt<=cutoff && x.State!=EmbeddedFileUploadState.Completed && x.State!=EmbeddedFileUploadState.Expired && (x.LeaseUntil==null || x.LeaseUntil<=now))
            .OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).Take(limit).ToListAsync(ct);
        // Permanent winners must not occupy the bounded candidate window.
        var attempts=await db.FileUploadAttempts.AsNoTracking().Where(x=>x.RetiredAt==null && x.CreatedAt<=cutoff &&
            !db.FileUploadReceipts.Any(r=>r.SessionId==x.SessionId && r.StorageProvider==x.StorageProvider && r.StorageKey==x.StorageKey) &&
            !db.FileUploadSessions.Any(s=>s.Id==x.SessionId && s.State==EmbeddedFileUploadState.Active && s.LeaseToken==x.Id && s.LeaseUntil>now))
            .OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).Take(limit).ToListAsync(ct);
        var attemptSessionIds=attempts.Select(x=>x.SessionId).Distinct().ToArray();
        var attemptSessions=await db.FileUploadSessions.AsNoTracking().Where(x=>attemptSessionIds.Contains(x.Id)).ToListAsync(ct);
        // Select bounded work first, then lock the complete asset set in the same
        // order as references. Session precedes its asset, matching writers.
        var candidates=sessions.Concat(attemptSessions).DistinctBy(x=>x.Id).OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).Take(limit).OrderBy(x=>x.AssetId);
        foreach(var candidate in candidates)
        {
            var session=await EmbeddedFileFences.SessionAsync(db,candidate.Id,ct);
            if(session is null)continue;
            var asset=await EmbeddedPhotoFences.LockAssetAsync(db,session.AssetId,ct);
            if(session.CreatedAt<=cutoff && (session.LeaseUntil is null || session.LeaseUntil<=now) && session.State is not (EmbeddedFileUploadState.Completed or EmbeddedFileUploadState.Expired))
            {
                session.State=EmbeddedFileUploadState.Expired;session.LeaseToken=null;session.LeaseUntil=null;
                if(asset?.BlobState==MediaBlobState.PendingUpload)asset.TransitionBlobState(MediaBlobState.UploadFailed,now);
                count++;
            }
            foreach(var attempt in attempts.Where(x=>x.SessionId==session.Id))
            {
                if(session.State==EmbeddedFileUploadState.Active && session.LeaseToken==attempt.Id && session.LeaseUntil>now ||
                    await db.FileUploadReceipts.AnyAsync(x=>x.SessionId==session.Id && x.StorageProvider==attempt.StorageProvider && x.StorageKey==attempt.StorageKey,ct))continue;
                await db.FileUploadAttempts.Where(x=>x.Id==attempt.Id && x.RetiredAt==null).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RetiredAt,now),ct);
            }
        }
        return count;
    }
}
