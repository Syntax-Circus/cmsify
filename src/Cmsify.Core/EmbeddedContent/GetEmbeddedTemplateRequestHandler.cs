using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

public interface IGetEmbeddedTemplateRequestHandler
{
    Task<Result<EmbeddedTemplateOutput>> HandleAsync(GetEmbeddedTemplateRequest request, CancellationToken cancellationToken);
}

public sealed class GetEmbeddedTemplateRequestHandler(IEmbeddedTemplateRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedContentAuthorizationService resourceAuthorization)
    : IGetEmbeddedTemplateRequestHandler
{
    public async Task<Result<EmbeddedTemplateOutput>> HandleAsync(GetEmbeddedTemplateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || string.IsNullOrWhiteSpace(request.ContractKey)
            || request.ContractKey.Length > 200 || !EmbeddedContractRules.IsFingerprint(request.ExpectedFingerprint))
            return Result<EmbeddedTemplateOutput>.Failure(new(EmbeddedContentErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedContentOperation(EmbeddedContentOperationKind.TemplateRead, request.WorkspaceId,
            request.ContractKey, request.ExpectedFingerprint, Guid.Empty);
        if (await EmbeddedRequestAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization,
            operation, false, cancellationToken) is { } error) return Result<EmbeddedTemplateOutput>.Failure(error);
        return await repository.GetAsync(request, resourceAuthorization.CanExecuteAsync, cancellationToken);
    }
}
