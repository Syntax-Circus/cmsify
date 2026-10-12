using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IReadEmbeddedFileRequestHandler
{
    Task<Result<EmbeddedFileReceipt>> HandleAsync(ReadEmbeddedFileRequest request,CancellationToken ct);
}
public sealed class ReadEmbeddedFileRequestHandler(IEmbeddedFileReadRepository repository,ICurrentActor actor,IEmbeddedFileAuthorizationService resource) : IReadEmbeddedFileRequestHandler
{
    public async Task<Result<EmbeddedFileReceipt>> HandleAsync(ReadEmbeddedFileRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!(request.WorkspaceId!=Guid.Empty && EmbeddedFileRules.Named(request.ConsumerNamespace) && EmbeddedFileRules.File(request.File) && request.Sink is not null)) return EmbeddedFileErrors.Invalid<EmbeddedFileReceipt>();
        request = request with { File=request.File with { SourceSha256=request.File.SourceSha256.ToLowerInvariant() } };
        EmbeddedFileOperation operation=new(EmbeddedFileOperationKind.ReadFile,request.WorkspaceId,request.File.AssetId,ReceiptId:request.File.ReceiptId,ConsumerNamespace:request.ConsumerNamespace);
        var authorize=EmbeddedFileRules.Guard(actor,resource,operation);
        if(!await authorize(operation,ct)) return EmbeddedFileErrors.Forbidden<EmbeddedFileReceipt>();
        return await repository.ReadAsync(request,actor.UserId,authorize,ct);
    }
}
