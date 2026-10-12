using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IGetEmbeddedPhotoRequestHandler
{
    Task<Result<EmbeddedPhotoStatus>> HandleAsync(GetEmbeddedPhotoRequest request, CancellationToken cancellationToken);
}
public sealed class GetEmbeddedPhotoRequestHandler(IEmbeddedPhotoProcessingRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IGetEmbeddedPhotoRequestHandler
{
    public async Task<Result<EmbeddedPhotoStatus>> HandleAsync(GetEmbeddedPhotoRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.Revision < 1)
            return Result<EmbeddedPhotoStatus>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadStatus, request.WorkspaceId, request.AssetId, request.Revision);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, false, cancellationToken) is { } error)
            return Result<EmbeddedPhotoStatus>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            resolved.AssetId == request.AssetId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, false, token) is null;
        return await repository.GetAsync(request, actorId, Authorize, cancellationToken);
    }
}
