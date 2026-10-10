using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IRetryEmbeddedPhotoProcessingRequestHandler
{
    Task<Result<EmbeddedPhotoStatus>> HandleAsync(RetryEmbeddedPhotoProcessingRequest request, CancellationToken cancellationToken);
}
public sealed class RetryEmbeddedPhotoProcessingRequestHandler(IEmbeddedPhotoProcessingRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IRetryEmbeddedPhotoProcessingRequestHandler
{
    public async Task<Result<EmbeddedPhotoStatus>> HandleAsync(RetryEmbeddedPhotoProcessingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.Revision < 1 || request.OperationKey == Guid.Empty || request.ExpectedRetryGeneration < 0)
            return Result<EmbeddedPhotoStatus>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.Retry, request.WorkspaceId, request.AssetId, request.Revision);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, true, cancellationToken) is { } error)
            return Result<EmbeddedPhotoStatus>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            resolved.AssetId == request.AssetId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, true, token) is null;
        return await repository.RetryAsync(request, actorId, Authorize, cancellationToken);
    }
}
