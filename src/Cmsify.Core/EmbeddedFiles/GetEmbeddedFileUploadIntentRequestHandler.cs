using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IGetEmbeddedFileUploadIntentRequestHandler
{
    Task<Result<EmbeddedFileUploadOutcome>> HandleAsync(GetEmbeddedFileUploadIntentRequest request,CancellationToken ct);
}
public sealed class GetEmbeddedFileUploadIntentRequestHandler(IEmbeddedFileUploadRepository repository,ICurrentActor actor,IEmbeddedFileAuthorizationService resource) : IGetEmbeddedFileUploadIntentRequestHandler
{
    public async Task<Result<EmbeddedFileUploadOutcome>> HandleAsync(GetEmbeddedFileUploadIntentRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!(request.WorkspaceId!=Guid.Empty && request.AssetId!=Guid.Empty && request.BeginOperationKey!=Guid.Empty && EmbeddedFileRules.Named(request.ConsumerNamespace))) return EmbeddedFileErrors.Invalid<EmbeddedFileUploadOutcome>();

        EmbeddedFileOperation operation=new(EmbeddedFileOperationKind.ReadUploadIntent,request.WorkspaceId,request.AssetId,ConsumerNamespace:request.ConsumerNamespace);
        var authorize=EmbeddedFileRules.Guard(actor,resource,operation);
        if(!await authorize(operation,ct)) return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
        return await repository.GetIntentAsync(request,actor.UserId!.Value,authorize,ct);
    }
}
