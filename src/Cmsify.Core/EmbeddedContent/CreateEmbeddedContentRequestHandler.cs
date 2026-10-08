using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

public interface ICreateEmbeddedContentRequestHandler
{
    Task<Result<EmbeddedContentWriteOutput>> HandleAsync(CreateEmbeddedContentRequest request, CancellationToken cancellationToken);
}

public sealed class CreateEmbeddedContentRequestHandler(IEmbeddedContentRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedContentAuthorizationService resourceAuthorization)
    : ICreateEmbeddedContentRequestHandler
{
    public async Task<Result<EmbeddedContentWriteOutput>> HandleAsync(CreateEmbeddedContentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.ContentItemId == Guid.Empty || request.OperationKey == Guid.Empty
            || !EmbeddedContractRules.IsFingerprint(request.ContractFingerprint) || request.TemplateVersionId == Guid.Empty || request.Fields is null)
            return Result<EmbeddedContentWriteOutput>.Failure(new(EmbeddedContentErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedContentOperation(EmbeddedContentOperationKind.ItemCreate, request.WorkspaceId,
            "", request.ContractFingerprint, request.TemplateVersionId, request.ContentItemId, 1);
        if (await EmbeddedRequestAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization,
            operation, true, cancellationToken) is { } error) return Result<EmbeddedContentWriteOutput>.Failure(error);
        request = request with { Fields = Array.AsReadOnly(request.Fields.Select(f => f is null ? null! : f with { JsonValue = f.JsonValue?.Clone() }).ToArray()) };
        return await repository.CreateEmbeddedContentAsync(request, actor.UserId!.Value, resourceAuthorization.CanExecuteAsync, cancellationToken);
    }
}
