using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IGetEmbeddedFileUploadOutcomeRequestHandler
{
    Task<Result<EmbeddedFileUploadOutcome>> HandleAsync(GetEmbeddedFileUploadOutcomeRequest request,CancellationToken ct);
}
public sealed class GetEmbeddedFileUploadOutcomeRequestHandler(IEmbeddedFileUploadRepository repository,ICurrentActor actor,IEmbeddedFileAuthorizationService resource) : IGetEmbeddedFileUploadOutcomeRequestHandler
{
    public async Task<Result<EmbeddedFileUploadOutcome>> HandleAsync(GetEmbeddedFileUploadOutcomeRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!(request.WorkspaceId!=Guid.Empty && request.SessionId!=Guid.Empty)) return EmbeddedFileErrors.Invalid<EmbeddedFileUploadOutcome>();

        EmbeddedFileOperation operation=new(EmbeddedFileOperationKind.ReadUploadOutcome,request.WorkspaceId,SessionId:request.SessionId);
        var authorize=EmbeddedFileRules.Guard(actor,resource,operation);
        if(!await authorize(operation,ct)) return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
        return await repository.GetOutcomeAsync(request,actor.UserId!.Value,authorize,ct);
    }
}
