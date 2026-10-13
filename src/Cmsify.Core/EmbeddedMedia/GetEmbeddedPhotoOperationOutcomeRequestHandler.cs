using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IGetEmbeddedPhotoOperationOutcomeRequestHandler
{
    Task<Result<EmbeddedPhotoStatus>> HandleAsync(GetEmbeddedPhotoOperationOutcomeRequest request, CancellationToken cancellationToken);
}

public sealed class GetEmbeddedPhotoOperationOutcomeRequestHandler(IEmbeddedPhotoProcessingRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IGetEmbeddedPhotoOperationOutcomeRequestHandler
{
    public async Task<Result<EmbeddedPhotoStatus>> HandleAsync(GetEmbeddedPhotoOperationOutcomeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.OperationKey == Guid.Empty ||
            request.Kind is not (EmbeddedPhotoOperationKind.EnrollUpload or EmbeddedPhotoOperationKind.Retry))
            return Result<EmbeddedPhotoStatus>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadOperationOutcome, request.WorkspaceId, request.AssetId);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, false, cancellationToken) is { } error)
            return Result<EmbeddedPhotoStatus>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            resolved.AssetId == request.AssetId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, false, token) is null;
        return await repository.GetOperationOutcomeAsync(request, actorId, Authorize, cancellationToken);
    }
}
