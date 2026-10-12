using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Persistence.EmbeddedFiles;

public sealed class EmbeddedFileReadRepository(CmsifyDbContext db,EmbeddedFileStorage storage,IOptions<EmbeddedFileOptions> options) : IEmbeddedFileReadRepository
{
    public async Task<Result<EmbeddedFileReceipt>> ReadAsync(ReadEmbeddedFileRequest request,Guid? actorId,
        Func<EmbeddedFileOperation,CancellationToken,Task<bool>> authorize,CancellationToken ct)
    {
        if(!options.Value.Enabled)return Failure(EmbeddedFileErrors.Disabled,ResultErrorKind.Forbidden);
        EmbeddedFileFences.Provider(db);
        if(request.WorkspaceId==Guid.Empty || !EmbeddedFileRules.Named(request.ConsumerNamespace) || !EmbeddedFileRules.File(request.File) || request.Sink is null)
            return EmbeddedFileErrors.Invalid<EmbeddedFileReceipt>();
        var operation=new EmbeddedFileOperation(EmbeddedFileOperationKind.ReadFile,request.WorkspaceId,request.File.AssetId,
            ReceiptId:request.File.ReceiptId,ConsumerNamespace:request.ConsumerNamespace,ActorId:actorId);
        async Task<bool> Guard(CancellationToken token)=>await authorize(operation,token);
        if(!await Guard(ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReceipt>();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var asset=await EmbeddedPhotoFences.LockAssetAsync(db,request.File.AssetId,ct);
        if(asset is null || asset.IsDeleted || asset.WorkspaceId!=request.WorkspaceId || asset.BlobState!=MediaBlobState.Available)
            return Failure(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
        var row=await db.FileUploadReceipts.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.File.ReceiptId && x.AssetId==request.File.AssetId && x.WorkspaceId==request.WorkspaceId,ct);
        if(row is null || row.ConsumerNamespace!=request.ConsumerNamespace || row.Length!=request.File.Length ||
            !string.Equals(row.SourceSha256,request.File.SourceSha256,StringComparison.OrdinalIgnoreCase) || row.StorageProvider!=storage.ProviderName ||
            !await db.FileUploadSessions.AnyAsync(x=>x.Id==row.SessionId && x.State==EmbeddedFileUploadState.Completed,ct))
            return Failure(EmbeddedFileErrors.NotFound,ResultErrorKind.NotFound);
        var receipt=EmbeddedFileFences.Receipt(row);
        try
        {
            if(!await Guard(ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReceipt>();
            await using(var input=await storage.Provider.ReadAsync(row.StorageKey,ct) ?? throw new IOException("File unavailable."))
            using(var checkedInput=new EmbeddedFileBoundedReadStream(input.Content,row.Length,Guard,row.SourceSha256))
            {
                await checkedInput.CopyToAsync(Stream.Null,ct);checkedInput.Proof();
            }
            if(!await Guard(ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReceipt>();
            await using var output=await storage.Provider.ReadAsync(row.StorageKey,ct) ?? throw new IOException("File unavailable.");
            using var checkedOutput=new EmbeddedFileBoundedReadStream(output.Content,row.Length,Guard,row.SourceSha256);
            await request.Sink.WriteAsync(checkedOutput,receipt,ct);checkedOutput.Proof();
            if(!await Guard(ct))return EmbeddedFileErrors.Forbidden<EmbeddedFileReceipt>();
            return Result<EmbeddedFileReceipt>.Success(receipt);
        }
        catch(EmbeddedFileAuthorityLostException){return EmbeddedFileErrors.Forbidden<EmbeddedFileReceipt>();}
        catch(EmbeddedFileInvalidBytesException){return Failure(EmbeddedFileErrors.InvalidBytes,ResultErrorKind.Conflict);}
    }
    private static Result<EmbeddedFileReceipt> Failure(string code,ResultErrorKind kind)=>Result<EmbeddedFileReceipt>.Failure(new(code,"File unavailable.",kind));
}
