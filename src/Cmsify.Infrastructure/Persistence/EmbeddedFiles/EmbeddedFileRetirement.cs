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
        foreach(var candidate in sessions)
        {
            var session=await EmbeddedFileFences.SessionAsync(db,candidate.Id,ct);
            if(session is null || session.CreatedAt>cutoff || session.LeaseUntil>now || session.State is EmbeddedFileUploadState.Completed or EmbeddedFileUploadState.Expired)continue;
            var asset=await EmbeddedPhotoFences.LockAssetAsync(db,session.AssetId,ct);
            session.State=EmbeddedFileUploadState.Expired;session.LeaseToken=null;session.LeaseUntil=null;
            if(asset?.BlobState==MediaBlobState.PendingUpload)asset.TransitionBlobState(MediaBlobState.UploadFailed,now);
            count++;
        }
        await db.SaveChangesAsync(ct);
        var attempts=await db.FileUploadAttempts.AsNoTracking().Where(x=>x.RetiredAt==null && x.CreatedAt<=cutoff).OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).Take(limit).ToListAsync(ct);
        foreach(var attempt in attempts)
        {
            var session=await EmbeddedFileFences.SessionAsync(db,attempt.SessionId,ct);
            if(session is null)continue;
            await EmbeddedPhotoFences.LockAssetAsync(db,session.AssetId,ct);
            if(session.State==EmbeddedFileUploadState.Active && session.LeaseToken==attempt.Id && session.LeaseUntil>now ||
                await db.FileUploadReceipts.AnyAsync(x=>x.SessionId==session.Id && x.StorageProvider==attempt.StorageProvider && x.StorageKey==attempt.StorageKey,ct))continue;
            await db.FileUploadAttempts.Where(x=>x.Id==attempt.Id && x.RetiredAt==null).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RetiredAt,now),ct);
        }
        return count;
    }
}
