using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IGetEmbeddedFileReferenceOutcomeRequestHandler
{
    Task<Result<EmbeddedFileReferenceOutcome>> HandleAsync(GetEmbeddedFileReferenceOutcomeRequest request,CancellationToken ct);
}
public sealed class GetEmbeddedFileReferenceOutcomeRequestHandler(IEmbeddedFileReferenceRepository repository,ICurrentActor actor,IEmbeddedFileAuthorizationService resource) : IGetEmbeddedFileReferenceOutcomeRequestHandler
{
    public async Task<Result<EmbeddedFileReferenceOutcome>> HandleAsync(GetEmbeddedFileReferenceOutcomeRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!(request.WorkspaceId!=Guid.Empty && request.ReferenceKey!=Guid.Empty && EmbeddedFileRules.Named(request.ConsumerNamespace))) return EmbeddedFileErrors.Invalid<EmbeddedFileReferenceOutcome>();

        EmbeddedFileOperation operation=new(EmbeddedFileOperationKind.ReadReferenceOutcome,request.WorkspaceId,ConsumerNamespace:request.ConsumerNamespace,ReferenceKey:request.ReferenceKey);
        var authorize=EmbeddedFileRules.Guard(actor,resource,operation);
        if(!await authorize(operation,ct)) return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        return await repository.GetOutcomeAsync(request,actor.UserId!.Value,authorize,ct);
    }
}
