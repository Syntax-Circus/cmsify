using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface ICreateEmbeddedPhotoRevisionRequestHandler
{
    Task<Result<EmbeddedPhotoStatus>> HandleAsync(CreateEmbeddedPhotoRevisionRequest request, CancellationToken cancellationToken);
}
public sealed class CreateEmbeddedPhotoRevisionRequestHandler(IEmbeddedPhotoProcessingRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : ICreateEmbeddedPhotoRevisionRequestHandler
{
    public async Task<Result<EmbeddedPhotoStatus>> HandleAsync(CreateEmbeddedPhotoRevisionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.OperationKey == Guid.Empty || request.ExpectedLatestRevision < 1 || request.Instructions is null || !EmbeddedPhotoPolicy.ValidateInstructions(request.Instructions) || string.IsNullOrWhiteSpace(request.PolicyId) || request.PolicyId.Length > 100)
            return Result<EmbeddedPhotoStatus>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.CreateRevision, request.WorkspaceId, request.AssetId, null);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, true, cancellationToken) is { } error)
            return Result<EmbeddedPhotoStatus>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            resolved.AssetId == request.AssetId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, true, token) is null;
        return await repository.CreateRevisionAsync(request, actorId, Authorize, cancellationToken);
    }
}
