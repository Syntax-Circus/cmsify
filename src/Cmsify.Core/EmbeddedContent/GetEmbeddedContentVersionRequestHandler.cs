using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

public interface IGetEmbeddedContentVersionRequestHandler
{
    Task<Result<EmbeddedContentVersionOutput>> HandleAsync(GetEmbeddedContentVersionRequest request, CancellationToken cancellationToken);
}

public sealed class GetEmbeddedContentVersionRequestHandler(IEmbeddedContentRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedContentAuthorizationService resourceAuthorization)
    : IGetEmbeddedContentVersionRequestHandler
{
    public async Task<Result<EmbeddedContentVersionOutput>> HandleAsync(GetEmbeddedContentVersionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.ContentItemId == Guid.Empty || request.TemplateVersionId == Guid.Empty
            || request.VersionNumber <= 0 || !EmbeddedContractRules.IsFingerprint(request.ContractFingerprint))
            return Result<EmbeddedContentVersionOutput>.Failure(new(EmbeddedContentErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedContentOperation(EmbeddedContentOperationKind.VersionRead, request.WorkspaceId,
            "", request.ContractFingerprint, request.TemplateVersionId, request.ContentItemId, request.VersionNumber);
        if (await EmbeddedRequestAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization,
            operation, false, cancellationToken) is { } error) return Result<EmbeddedContentVersionOutput>.Failure(error);
        return await repository.GetEmbeddedContentVersionAsync(request, resourceAuthorization.CanExecuteAsync, cancellationToken);
    }
}
