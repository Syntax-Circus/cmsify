using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.EmbeddedFiles;

public sealed class EmbeddedFileUploadRepository(CmsifyDbContext db,EmbeddedFileStorage storage,IOptions<EmbeddedFileOptions> options,TimeProvider clock):IEmbeddedFileUploadRepository
{
    private static Result<EmbeddedFileUploadOutcome> Error(string code,ResultErrorKind kind=ResultErrorKind.Conflict)=>Result<EmbeddedFileUploadOutcome>.Failure(new(code,"File operation could not be completed.",kind));
    private async Task<EmbeddedFileUploadOutcome> OutcomeAsync(FileUploadSessionRecord s,CancellationToken ct)
    {
        var receipt=await db.FileUploadReceipts.AsNoTracking().SingleOrDefaultAsync(x=>x.SessionId==s.Id,ct);
        var state=s.State==EmbeddedFileUploadState.Active && s.LeaseUntil<=clock.GetUtcNow()?EmbeddedFileUploadState.Interrupted:s.State;
        return new(s.WorkspaceId,s.AssetId,s.Id,s.OperationKey,state,receipt is null?null:EmbeddedFileFences.Receipt(receipt),s.FailureCode);
    }
    public async Task<Result<EmbeddedFileUploadOutcome>> BeginAsync(BeginEmbeddedFileUploadRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)
    {
        EmbeddedFileFences.Provider(db);ct.ThrowIfCancellationRequested();
        if(!options.Value.Enabled)return Error(EmbeddedFileErrors.Disabled,ResultErrorKind.Failure);
        if(!EmbeddedFileRules.Begin(r))return EmbeddedFileErrors.Invalid<EmbeddedFileUploadOutcome>();
        r=r with { ExpectedSourceSha256=r.ExpectedSourceSha256.ToLowerInvariant(),DisplayFileName=EmbeddedFileRules.DisplayName(r.DisplayFileName) };
        var op=new EmbeddedFileOperation(EmbeddedFileOperationKind.BeginUpload,r.WorkspaceId,r.AssetId,ConsumerNamespace:r.ConsumerNamespace,ActorId:actor);
        if(actor==Guid.Empty || !await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var workspace=await db.Workspaces.FromSqlInterpolated($"SELECT *, xmin FROM workspaces WHERE id={r.WorkspaceId} FOR UPDATE").AsNoTracking().ToListAsync(ct);
        if(workspace.Count!=1)return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
        var fingerprint=EmbeddedFileFences.Fingerprint(new { Request=r,Actor=actor });
        var known=await db.FileUploadSessions.AsNoTracking().Where(x=>x.WorkspaceId==r.WorkspaceId && (x.AssetId==r.AssetId || x.ConsumerNamespace==r.ConsumerNamespace && x.OperationKey==r.OperationKey)).ToListAsync(ct);
        if(!await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
        if(known.Count>0)return known.Count==1 && known[0].Fingerprint==fingerprint?Result<EmbeddedFileUploadOutcome>.Success(await OutcomeAsync(known[0],ct)):Error(EmbeddedFileErrors.Conflict);
        if(await db.MediaAssets.IgnoreQueryFilters().AnyAsync(x=>x.Id==r.AssetId,ct))return Error(EmbeddedFileErrors.Conflict);
        var now=clock.GetUtcNow();var s=new FileUploadSessionRecord { Id=Guid.NewGuid(),WorkspaceId=r.WorkspaceId,AssetId=r.AssetId,ActorId=actor,OperationKey=r.OperationKey,ConsumerNamespace=r.ConsumerNamespace,Fingerprint=fingerprint,DisplayFileName=r.DisplayFileName,DeclaredLength=r.DeclaredLength,ExpectedSha256=r.ExpectedSourceSha256,CreatedAt=now,State=EmbeddedFileUploadState.Pending };
        db.MediaAssets.Add(new MediaAsset { Id=r.AssetId,WorkspaceId=r.WorkspaceId,FileName=r.DisplayFileName,MimeType=EmbeddedFileRules.MediaType,SizeBytes=r.DeclaredLength,StorageProvider=storage.ProviderName,StorageKey=EmbeddedFileStorage.AttemptKey(r.WorkspaceId,r.AssetId,s.Id),CreatedByUserId=actor,CreatedAt=now,UpdatedAt=now,BlobState=MediaBlobState.PendingUpload,BlobStateChangedAt=now });
        db.FileUploadSessions.Add(s);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Result<EmbeddedFileUploadOutcome>.Success(await OutcomeAsync(s,ct));
    }
    public async Task<Result<EmbeddedFileUploadOutcome>> GetIntentAsync(GetEmbeddedFileUploadIntentRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)
    {
        EmbeddedFileFences.Provider(db);ct.ThrowIfCancellationRequested();if(!options.Value.Enabled)return Error(EmbeddedFileErrors.Disabled,ResultErrorKind.Failure);
        var op=new EmbeddedFileOperation(EmbeddedFileOperationKind.ReadUploadIntent,r.WorkspaceId,r.AssetId,ConsumerNamespace:r.ConsumerNamespace,ActorId:actor);
        if(actor==Guid.Empty || !await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
        var s=await db.FileUploadSessions.AsNoTracking().SingleOrDefaultAsync(x=>x.WorkspaceId==r.WorkspaceId && x.AssetId==r.AssetId && x.ConsumerNamespace==r.ConsumerNamespace && x.OperationKey==r.BeginOperationKey,ct);
        return await LookupAsync(s,actor,EmbeddedFileOperationKind.ReadUploadIntent,auth,ct);
    }
    public async Task<Result<EmbeddedFileUploadOutcome>> GetOutcomeAsync(GetEmbeddedFileUploadOutcomeRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)
    {
        EmbeddedFileFences.Provider(db);ct.ThrowIfCancellationRequested();if(!options.Value.Enabled)return Error(EmbeddedFileErrors.Disabled,ResultErrorKind.Failure);
        return await LookupAsync(await db.FileUploadSessions.AsNoTracking().SingleOrDefaultAsync(x=>x.WorkspaceId==r.WorkspaceId && x.Id==r.SessionId,ct),actor,EmbeddedFileOperationKind.ReadUploadOutcome,auth,ct);
    }
    private async Task<Result<EmbeddedFileUploadOutcome>> LookupAsync(FileUploadSessionRecord? s,Guid actor,EmbeddedFileOperationKind kind,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)
    {
        if(s is null)return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
        var op=EmbeddedFileFences.Operation(s,kind);if(actor==Guid.Empty || s.ActorId!=actor || !await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
        var outcome=await OutcomeAsync(s,ct);return await auth(op,ct)?Result<EmbeddedFileUploadOutcome>.Success(outcome):EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
    }
    public async Task<Result<EmbeddedFileUploadOutcome>> WriteAsync(WriteEmbeddedFileUploadRequest r,Guid actor,Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)
    {
        EmbeddedFileFences.Provider(db);ct.ThrowIfCancellationRequested();if(!options.Value.Enabled)return Error(EmbeddedFileErrors.Disabled,ResultErrorKind.Failure);
        FileUploadSessionRecord session;FileUploadAttemptRecord attempt;
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            var s=await EmbeddedFileFences.SessionAsync(db,r.SessionId,ct);
            if(s is null || s.WorkspaceId!=r.WorkspaceId)return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
            session=s;var op=EmbeddedFileFences.Operation(s,EmbeddedFileOperationKind.WriteUpload);
            if(actor==Guid.Empty || s.ActorId!=actor || !await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
            if(r.OperationKey==Guid.Empty || r.Source is null || s.WriteOperationKey is { } key && key!=r.OperationKey)return Error(EmbeddedFileErrors.Conflict);
            if(s.State==EmbeddedFileUploadState.Completed)return Result<EmbeddedFileUploadOutcome>.Success(await OutcomeAsync(s,ct));
            if(s.State==EmbeddedFileUploadState.Expired)return Error(EmbeddedFileErrors.Conflict);
            var now=clock.GetUtcNow();if(s.State==EmbeddedFileUploadState.Active && s.LeaseUntil>now)return Error(EmbeddedFileErrors.Conflict);
            var asset=await EmbeddedPhotoFences.LockAssetAsync(db,s.AssetId,ct);if(asset is null || asset.IsDeleted || asset.BlobState!=MediaBlobState.PendingUpload)return Error(EmbeddedFileErrors.Conflict);
            var token=Guid.NewGuid();s.WriteOperationKey=r.OperationKey;s.LeaseToken=token;s.LeaseUntil=now+options.Value.UploadLeaseDuration;s.State=EmbeddedFileUploadState.Active;s.FailureCode=null;
            attempt=new() { Id=token,SessionId=s.Id,StorageProvider=storage.ProviderName,StorageKey=EmbeddedFileStorage.AttemptKey(s.WorkspaceId,s.AssetId,token),CreatedAt=now };
            db.FileUploadAttempts.Add(attempt);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        var operation=EmbeddedFileFences.Operation(session,EmbeddedFileOperationKind.WriteUpload);
        async Task<bool> Guard(CancellationToken token)=>clock.GetUtcNow()<session.LeaseUntil && await auth(operation,token);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(options.Value.UploadDeadline);
        try
        {
            if(!await Guard(deadline.Token))throw new EmbeddedFileAuthorityLostException();
            var source=await r.Source.OpenReadAsync(deadline.Token);var bytes=await storage.StoreAsync(attempt.StorageKey,source,session.DeclaredLength,Guard,deadline.Token);
            if(bytes.Sha256!=session.ExpectedSha256)throw new EmbeddedFileInvalidBytesException();
            await using var tx=await db.Database.BeginTransactionAsync(ct);var fresh=await EmbeddedFileFences.SessionAsync(db,session.Id,ct);
            if(fresh is null || fresh.State!=EmbeddedFileUploadState.Active || fresh.LeaseToken!=attempt.Id || fresh.LeaseUntil<=clock.GetUtcNow())return Error(EmbeddedFileErrors.Conflict);
            if(!await auth(operation,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
            var asset=await EmbeddedPhotoFences.LockAssetAsync(db,fresh.AssetId,ct);if(asset is null || asset.IsDeleted || asset.BlobState!=MediaBlobState.PendingUpload)return Error(EmbeddedFileErrors.Conflict);
            db.FileUploadReceipts.Add(new() { Id=Guid.NewGuid(),SessionId=fresh.Id,WorkspaceId=fresh.WorkspaceId,AssetId=fresh.AssetId,ConsumerNamespace=fresh.ConsumerNamespace,Length=bytes.Length,SourceSha256=bytes.Sha256,StorageProvider=attempt.StorageProvider,StorageKey=attempt.StorageKey,CreatedAt=clock.GetUtcNow() });
            fresh.State=EmbeddedFileUploadState.Completed;fresh.LeaseToken=null;fresh.LeaseUntil=null;asset.StorageKey=attempt.StorageKey;asset.SizeBytes=bytes.Length;asset.TransitionBlobState(MediaBlobState.Available,clock.GetUtcNow());
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Result<EmbeddedFileUploadOutcome>.Success(await OutcomeAsync(fresh,ct));
        }
        catch(EmbeddedFileInvalidBytesException){await FailAsync(session.Id,attempt.Id,EmbeddedFileErrors.InvalidBytes,ct);return Error(EmbeddedFileErrors.InvalidBytes,ResultErrorKind.Validation);}
        catch(EmbeddedFileAuthorityLostException){await FailAsync(session.Id,attempt.Id,EmbeddedFileErrors.Denied,ct);return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();}
    }
    private async Task FailAsync(Guid session,Guid token,string code,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);var s=await EmbeddedFileFences.SessionAsync(db,session,ct);
        if(s is not null && s.LeaseToken==token && s.State==EmbeddedFileUploadState.Active){s.State=EmbeddedFileUploadState.Failed;s.FailureCode=code;s.LeaseToken=null;s.LeaseUntil=null;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);}
    }
}
