using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedFiles;

public interface IProtectEmbeddedFileReferencesRequestHandler
{
    Task<Result<EmbeddedFileReferenceOutcome>> HandleAsync(ProtectEmbeddedFileReferencesRequest request,CancellationToken ct);
}
public sealed class ProtectEmbeddedFileReferencesRequestHandler(IEmbeddedFileReferenceRepository repository,ICurrentActor actor,IEmbeddedFileAuthorizationService resource) : IProtectEmbeddedFileReferencesRequestHandler
{
    public async Task<Result<EmbeddedFileReferenceOutcome>> HandleAsync(ProtectEmbeddedFileReferencesRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!(request.WorkspaceId!=Guid.Empty && request.ReferenceKey!=Guid.Empty && request.OperationKey!=Guid.Empty && EmbeddedFileRules.Named(request.ConsumerNamespace) && request.Files is { Count:>0 and <=EmbeddedFileRules.MaxFiles } && request.Files.All(EmbeddedFileRules.File) && request.Files.Select(x=>x.AssetId).Distinct().Count()==request.Files.Count)) return EmbeddedFileErrors.Invalid<EmbeddedFileReferenceOutcome>();
        request = request with { Files=Array.AsReadOnly(request.Files.Select(x=>x with { SourceSha256=x.SourceSha256.ToLowerInvariant() }).ToArray()) };
        EmbeddedFileOperation operation=new(EmbeddedFileOperationKind.Protect,request.WorkspaceId,ConsumerNamespace:request.ConsumerNamespace,ReferenceKey:request.ReferenceKey);
        var authorize=EmbeddedFileRules.Guard(actor,resource,operation);
        if(!await authorize(operation,ct)) return EmbeddedFileErrors.Forbidden<EmbeddedFileReferenceOutcome>();
        return await repository.ProtectAsync(request,actor.UserId!.Value,authorize,ct);
    }
}
