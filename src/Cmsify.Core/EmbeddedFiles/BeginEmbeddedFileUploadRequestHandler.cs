using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IBeginEmbeddedFileUploadRequestHandler
{
    Task<Result<EmbeddedFileUploadOutcome>> HandleAsync(BeginEmbeddedFileUploadRequest request,CancellationToken ct);
}
public sealed class BeginEmbeddedFileUploadRequestHandler(IEmbeddedFileUploadRepository repository,ICurrentActor actor,IEmbeddedFileAuthorizationService resource) : IBeginEmbeddedFileUploadRequestHandler
{
    public async Task<Result<EmbeddedFileUploadOutcome>> HandleAsync(BeginEmbeddedFileUploadRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!(EmbeddedFileRules.Begin(request))) return EmbeddedFileErrors.Invalid<EmbeddedFileUploadOutcome>();
        request = request with { DisplayFileName=EmbeddedFileRules.DisplayName(request.DisplayFileName), ExpectedSourceSha256=request.ExpectedSourceSha256.ToLowerInvariant() };
        EmbeddedFileOperation operation=new(EmbeddedFileOperationKind.BeginUpload,request.WorkspaceId,request.AssetId,ConsumerNamespace:request.ConsumerNamespace);
        var authorize=EmbeddedFileRules.Guard(actor,resource,operation);
        if(!await authorize(operation,ct)) return EmbeddedFileErrors.Forbidden<EmbeddedFileUploadOutcome>();
        return await repository.BeginAsync(request,actor.UserId!.Value,authorize,ct);
    }
}
