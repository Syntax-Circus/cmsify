using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IReleaseEmbeddedFileReferencesRequestHandler
{
    Task<Result<EmbeddedFileReferenceOutcome>> HandleAsync(ReleaseEmbeddedFileReferencesRequest request,CancellationToken ct);
}
public sealed class ReleaseEmbeddedFileReferencesRequestHandler(IEmbeddedFileReferenceRepository repository,ICurrentActor actor,IEmbeddedFileAuthorizationService resource) : IReleaseEmbeddedFileReferencesRequestHandler
{
    public async Task<Result<EmbeddedFileReferenceOutcome>> HandleAsync(ReleaseEmbeddedFileReferencesRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!(request.WorkspaceId!=Guid.Empty && request.ReferenceKey!=Guid.Empty && request.OperationKey!=Guid.Empty && request.ExpectedProtectionRevision>0 && EmbeddedFileRules.Named(request.ConsumerNamespace))) return EmbeddedFileErrors.Invalid<EmbeddedFileReferenceOutcome>();

        EmbeddedFileOperation operation=new(EmbeddedFileOperationKind.Release,request.WorkspaceId,ConsumerNamespace:request.ConsumerNamespace,ReferenceKey:request.ReferenceKey);
        var authorize=EmbeddedFileRules.Guard(actor,resource,operation);
        if(!await authorize(operation,ct)) return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        return await repository.ReleaseAsync(request,actor.UserId!.Value,authorize,ct);
    }
}
