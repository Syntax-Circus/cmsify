using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IWriteEmbeddedFileUploadRequestHandler
{
    Task<Result<EmbeddedFileUploadOutcome>> HandleAsync(WriteEmbeddedFileUploadRequest request,CancellationToken ct);
}
public sealed class WriteEmbeddedFileUploadRequestHandler(IEmbeddedFileUploadRepository repository,ICurrentActor actor,IEmbeddedFileAuthorizationService resource) : IWriteEmbeddedFileUploadRequestHandler
{
    public async Task<Result<EmbeddedFileUploadOutcome>> HandleAsync(WriteEmbeddedFileUploadRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!(request.WorkspaceId!=Guid.Empty && request.SessionId!=Guid.Empty && request.OperationKey!=Guid.Empty && request.Source is not null)) return EmbeddedFileErrors.Invalid<EmbeddedFileUploadOutcome>();

        EmbeddedFileOperation operation=new(EmbeddedFileOperationKind.WriteUpload,request.WorkspaceId,SessionId:request.SessionId);
        var authorize=EmbeddedFileRules.Guard(actor,resource,operation);
        if(!await authorize(operation,ct)) return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
        return await repository.WriteAsync(request,actor.UserId!.Value,authorize,ct);
    }
}
