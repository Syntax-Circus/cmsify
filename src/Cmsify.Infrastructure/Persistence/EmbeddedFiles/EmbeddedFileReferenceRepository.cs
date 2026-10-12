using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.EmbeddedFiles;

public sealed class EmbeddedFileReferenceRepository(CmsifyDbContext db,EmbeddedFileStorage storage,IOptions<EmbeddedFileOptions> options,
    IEnumerable<RegisteredEmbeddedFileRetentionGuard> registrations,TimeProvider clock) : IEmbeddedFileReferenceRepository
{
    private readonly IReadOnlyDictionary<string,IEmbeddedFileRetentionGuard> _guards=registrations.ToDictionary(x=>x.ConsumerNamespace,x=>x.Guard,StringComparer.Ordinal);
    private static Result<EmbeddedFileReferenceOutcome> Error(string code,ResultErrorKind kind=ResultErrorKind.Conflict)=>Result<EmbeddedFileReferenceOutcome>.Failure(new(code,"File reference unavailable.",kind));
    private static EmbeddedFileOperation Operation(EmbeddedFileOperationKind kind,Guid workspace,string consumer,Guid reference,Guid actor)=>new(kind,workspace,ConsumerNamespace:consumer,ReferenceKey:reference,ActorId:actor);
    private async Task<EmbeddedFileReferenceOutcome> Outcome(FileReferenceSetRecord set,CancellationToken ct)
    {
        var entries=await db.FileReferenceEntries.AsNoTracking().Where(x=>x.ReferenceSetId==set.Id).OrderBy(x=>x.Ordinal).ToListAsync(ct);
        return new(set.WorkspaceId,set.ConsumerNamespace,set.ReferenceKey,set.OperationKey,set.ProtectionRevision,set.State,
            Array.AsReadOnly(entries.Select(x=>new EmbeddedFileIdentity(x.AssetId,x.ReceiptId,x.Length,x.SourceSha256)).ToArray()));
    }
    private async Task<bool> LockWorkspace(Guid workspace,CancellationToken ct)=>await db.Workspaces.FromSqlInterpolated($"SELECT *, xmin FROM workspaces WHERE id={workspace} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct) is not null;
    public async Task<Result<EmbeddedFileReferenceOutcome>> ProtectAsync(ProtectEmbeddedFileReferencesRequest request,Guid actor,
        Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)
    {
        EmbeddedFileFences.Provider(db);ct.ThrowIfCancellationRequested();
        if(!options.Value.Enabled)return Error(EmbeddedFileErrors.Disabled);
        if(request.WorkspaceId==Guid.Empty || !EmbeddedFileRules.Named(request.ConsumerNamespace) || request.ReferenceKey==Guid.Empty || request.OperationKey==Guid.Empty ||
            request.Files is null || request.Files.Count is <1 or >EmbeddedFileRules.MaxFiles || request.Files.Any(x=>!EmbeddedFileRules.File(x)) || request.Files.Select(x=>x.AssetId).Distinct().Count()!=request.Files.Count)
            return EmbeddedFileErrors.Invalid<EmbeddedFileReferenceOutcome>();
        if(!_guards.ContainsKey(request.ConsumerNamespace))return Error(EmbeddedFileErrors.Retained);
        request=request with { Files=Array.AsReadOnly(request.Files.Select(x=>x with { SourceSha256=x.SourceSha256.ToLowerInvariant() }).ToArray()) };
        var op=Operation(EmbeddedFileOperationKind.Protect,request.WorkspaceId,request.ConsumerNamespace,request.ReferenceKey,actor);
        if(actor==Guid.Empty || !await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        var fingerprint=EmbeddedFileFences.Fingerprint(new { Request=request,Actor=actor });
        await using var tx=await db.Database.BeginTransactionAsync(ct);if(!await LockWorkspace(request.WorkspaceId,ct))return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
        var known=await db.FileReferenceSets.AsNoTracking().Where(x=>x.WorkspaceId==request.WorkspaceId && x.ConsumerNamespace==request.ConsumerNamespace && (x.ReferenceKey==request.ReferenceKey || x.OperationKey==request.OperationKey)).ToListAsync(ct);
        if(known.Count>0)
        {
            if(known.Count!=1 || known[0].Fingerprint!=fingerprint || known[0].State!=EmbeddedFileReferenceState.Protected)return Error(EmbeddedFileErrors.Conflict);
            var replay=await Outcome(known[0],ct);return await auth(op,ct)?Result<EmbeddedFileReferenceOutcome>.Success(replay):EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        }
        foreach(var file in request.Files.OrderBy(x=>x.AssetId))
        {
            var asset=await EmbeddedPhotoFences.LockAssetAsync(db,file.AssetId,ct);
            if(asset is null || asset.IsDeleted || asset.WorkspaceId!=request.WorkspaceId || asset.BlobState!=MediaBlobState.Available)return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
            if(!await auth(op with { AssetId=file.AssetId,ReceiptId=file.ReceiptId },ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
            var receipt=await db.FileUploadReceipts.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==file.ReceiptId && x.AssetId==file.AssetId && x.WorkspaceId==request.WorkspaceId && x.ConsumerNamespace==request.ConsumerNamespace && x.Length==file.Length && x.SourceSha256==file.SourceSha256,ct);
            if(receipt is null || receipt.StorageProvider!=storage.ProviderName || !await db.FileUploadSessions.AnyAsync(x=>x.Id==receipt.SessionId && x.State==EmbeddedFileUploadState.Completed,ct))return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
            await using var input=await storage.Provider.ReadAsync(receipt.StorageKey,ct) ?? throw new IOException("File unavailable.");
            using var bounded=new EmbeddedFileBoundedReadStream(input.Content,receipt.Length,token=>auth(op with { AssetId=file.AssetId,ReceiptId=file.ReceiptId },token),receipt.SourceSha256);
            try { await bounded.CopyToAsync(Stream.Null,ct);bounded.Proof(); }
            catch(EmbeddedFileAuthorityLostException){return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();}
            catch(EmbeddedFileInvalidBytesException){return Error(EmbeddedFileErrors.InvalidBytes);}
        }
        if(!await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        var set=new FileReferenceSetRecord { WorkspaceId=request.WorkspaceId,ConsumerNamespace=request.ConsumerNamespace,ReferenceKey=request.ReferenceKey,OperationKey=request.OperationKey,ActorId=actor,Fingerprint=fingerprint,State=EmbeddedFileReferenceState.Protected,CreatedAt=clock.GetUtcNow() };
        db.FileReferenceSets.Add(set);
        for(var i=0;i<request.Files.Count;i++){var f=request.Files[i];db.FileReferenceEntries.Add(new() { ReferenceSetId=set.Id,Ordinal=i,AssetId=f.AssetId,ReceiptId=f.ReceiptId,Length=f.Length,SourceSha256=f.SourceSha256 });}
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Result<EmbeddedFileReferenceOutcome>.Success(await Outcome(set,ct));
    }
    public async Task<Result<EmbeddedFileReferenceOutcome>> GetOutcomeAsync(GetEmbeddedFileReferenceOutcomeRequest request,Guid actor,
        Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)
    {
        EmbeddedFileFences.Provider(db);if(!options.Value.Enabled)return Error(EmbeddedFileErrors.Disabled);
        var op=Operation(EmbeddedFileOperationKind.ReadReferenceOutcome,request.WorkspaceId,request.ConsumerNamespace,request.ReferenceKey,actor);
        if(actor==Guid.Empty || !await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        var set=await db.FileReferenceSets.AsNoTracking().SingleOrDefaultAsync(x=>x.WorkspaceId==request.WorkspaceId && x.ConsumerNamespace==request.ConsumerNamespace && x.ReferenceKey==request.ReferenceKey,ct);
        if(set is null)return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
        var outcome=await Outcome(set,ct);return await auth(op,ct)?Result<EmbeddedFileReferenceOutcome>.Success(outcome):EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
    }
    public async Task<Result<EmbeddedFileReferenceOutcome>> ReleaseAsync(ReleaseEmbeddedFileReferencesRequest request,Guid actor,
        Func<EmbeddedFileOperation,CancellationToken,Task<bool>> auth,CancellationToken ct)
    {
        EmbeddedFileFences.Provider(db);if(!options.Value.Enabled)return Error(EmbeddedFileErrors.Disabled);
        if(request.OperationKey==Guid.Empty || request.ExpectedProtectionRevision<1)return EmbeddedFileErrors.Invalid<EmbeddedFileReferenceOutcome>();
        var op=Operation(EmbeddedFileOperationKind.Release,request.WorkspaceId,request.ConsumerNamespace,request.ReferenceKey,actor);
        if(actor==Guid.Empty || !await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        await using var tx=await db.Database.BeginTransactionAsync(ct);if(!await LockWorkspace(request.WorkspaceId,ct))return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
        var fresh=await db.FileReferenceSets.AsNoTracking().SingleOrDefaultAsync(x=>x.WorkspaceId==request.WorkspaceId && x.ConsumerNamespace==request.ConsumerNamespace && x.ReferenceKey==request.ReferenceKey,ct);
        if(fresh is null)return Error(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
        if(fresh.ProtectionRevision!=request.ExpectedProtectionRevision)return Error(EmbeddedFileErrors.Conflict);
        var outcome=await Outcome(fresh,ct);
        foreach(var file in outcome.Files.OrderBy(x=>x.AssetId))await EmbeddedPhotoFences.LockAssetAsync(db,file.AssetId,ct);
        if(fresh.State==EmbeddedFileReferenceState.Released)
            return fresh.ReleaseOperationKey==request.OperationKey && await auth(op,ct)?Result<EmbeddedFileReferenceOutcome>.Success(outcome):Error(EmbeddedFileErrors.Conflict);
        if(await db.FileReferenceSets.AnyAsync(x=>x.WorkspaceId==request.WorkspaceId && x.ConsumerNamespace==request.ConsumerNamespace && (x.OperationKey==request.OperationKey || x.ReleaseOperationKey==request.OperationKey),ct))return Error(EmbeddedFileErrors.Conflict);
        if(!_guards.TryGetValue(request.ConsumerNamespace,out var guard))return Error(EmbeddedFileErrors.Retained);
        EmbeddedFileRetentionDecision decision;
        try { decision=await guard.CheckAsync(new(request.WorkspaceId,request.ConsumerNamespace,request.ReferenceKey,request.ExpectedProtectionRevision,outcome.Files),ct); }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception){return Error(EmbeddedFileErrors.Retained);}
        if(decision is null || !decision.AllowRelease)return Error(EmbeddedFileErrors.Retained);
        if(!await auth(op,ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        var local=db.FileReferenceSets.Local.FirstOrDefault(x=>x.Id==fresh.Id);
        if(local is null){db.FileReferenceSets.Attach(fresh);local=fresh;}else { db.Entry(local).CurrentValues.SetValues(fresh);db.Entry(local).OriginalValues.SetValues(fresh); }
        local.State=EmbeddedFileReferenceState.Released;local.ReleaseOperationKey=request.OperationKey;
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Result<EmbeddedFileReferenceOutcome>.Success(await Outcome(local,ct));
    }
}
