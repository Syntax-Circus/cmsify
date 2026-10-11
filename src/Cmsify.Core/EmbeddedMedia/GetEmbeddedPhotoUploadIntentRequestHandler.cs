using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IGetEmbeddedPhotoUploadIntentRequestHandler
{
    Task<Result<EmbeddedPhotoUploadOutcome>> HandleAsync(GetEmbeddedPhotoUploadIntentRequest request, CancellationToken cancellationToken);
}

public sealed class GetEmbeddedPhotoUploadIntentRequestHandler(IEmbeddedPhotoUploadRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IGetEmbeddedPhotoUploadIntentRequestHandler
{
    public async Task<Result<EmbeddedPhotoUploadOutcome>> HandleAsync(GetEmbeddedPhotoUploadIntentRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.OperationKey == Guid.Empty)
            return Result<EmbeddedPhotoUploadOutcome>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadUploadOutcome, request.WorkspaceId, request.AssetId);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, false, cancellationToken) is { } error)
            return Result<EmbeddedPhotoUploadOutcome>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId && resolved.AssetId == request.AssetId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, false, token) is null;
        return await repository.GetIntentAsync(request, actorId, Authorize, cancellationToken);
    }
}
