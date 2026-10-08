using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

public interface IGetEmbeddedContentOperationReceiptRequestHandler
{
    Task<Result<EmbeddedContentReceiptOutput>> HandleAsync(GetEmbeddedContentOperationReceiptRequest request, CancellationToken cancellationToken);
}

public sealed class GetEmbeddedContentOperationReceiptRequestHandler(IEmbeddedContentRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedContentAuthorizationService resourceAuthorization)
    : IGetEmbeddedContentOperationReceiptRequestHandler
{
    public async Task<Result<EmbeddedContentReceiptOutput>> HandleAsync(GetEmbeddedContentOperationReceiptRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.ContentItemId == Guid.Empty || request.OperationKey == Guid.Empty
            || !EmbeddedContractRules.IsFingerprint(request.ContractFingerprint) || !Enum.IsDefined(request.Kind))
            return Result<EmbeddedContentReceiptOutput>.Failure(new(EmbeddedContentErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedContentOperation(EmbeddedContentOperationKind.ReceiptRead, request.WorkspaceId,
            "", request.ContractFingerprint, Guid.Empty, request.ContentItemId, null);
        if (await EmbeddedRequestAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization,
            operation, false, cancellationToken) is { } error) return Result<EmbeddedContentReceiptOutput>.Failure(error);
        
        return await repository.GetEmbeddedContentOperationReceiptAsync(request, resourceAuthorization.CanExecuteAsync, cancellationToken);
    }
}
