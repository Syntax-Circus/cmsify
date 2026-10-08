using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

public interface ICreateEmbeddedContentVersionRequestHandler
{
    Task<Result<EmbeddedContentWriteOutput>> HandleAsync(CreateEmbeddedContentVersionRequest request, CancellationToken cancellationToken);
}

public sealed class CreateEmbeddedContentVersionRequestHandler(IEmbeddedContentRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedContentAuthorizationService resourceAuthorization)
    : ICreateEmbeddedContentVersionRequestHandler
{
    public async Task<Result<EmbeddedContentWriteOutput>> HandleAsync(CreateEmbeddedContentVersionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.ContentItemId == Guid.Empty || request.OperationKey == Guid.Empty
            || !EmbeddedContractRules.IsFingerprint(request.ContractFingerprint) || request.TemplateVersionId == Guid.Empty || request.Fields is null || request.SourceVersionNumber <= 0 || request.SourceRevision?.Candidate is null)
            return Result<EmbeddedContentWriteOutput>.Failure(new(EmbeddedContentErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedContentOperation(EmbeddedContentOperationKind.VersionCreate, request.WorkspaceId,
            "", request.ContractFingerprint, request.TemplateVersionId, request.ContentItemId, request.SourceVersionNumber);
        if (await EmbeddedRequestAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization,
            operation, true, cancellationToken) is { } error) return Result<EmbeddedContentWriteOutput>.Failure(error);
        request = request with { Fields = Array.AsReadOnly(request.Fields.Select(f => f is null ? null! : f with { JsonValue = f.JsonValue?.Clone() }).ToArray()) };
        return await repository.CreateEmbeddedContentVersionAsync(request, actor.UserId!.Value, resourceAuthorization.CanExecuteAsync, cancellationToken);
    }
}
