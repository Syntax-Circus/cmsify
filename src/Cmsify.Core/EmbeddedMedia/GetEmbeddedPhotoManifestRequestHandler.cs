using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IGetEmbeddedPhotoManifestRequestHandler
{
    Task<Result<EmbeddedPhotoManifest>> HandleAsync(GetEmbeddedPhotoManifestRequest request, CancellationToken cancellationToken);
}
public sealed class GetEmbeddedPhotoManifestRequestHandler(IEmbeddedPhotoProcessingRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IGetEmbeddedPhotoManifestRequestHandler
{
    public async Task<Result<EmbeddedPhotoManifest>> HandleAsync(GetEmbeddedPhotoManifestRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.Revision < 1 || !EmbeddedPhotoPolicy.IsSha256(request.ManifestSha256))
            return Result<EmbeddedPhotoManifest>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadManifest, request.WorkspaceId, request.AssetId, request.Revision);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, false, cancellationToken) is { } error)
            return Result<EmbeddedPhotoManifest>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            resolved.AssetId == request.AssetId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, false, token) is null;
        return await repository.GetManifestAsync(request, actorId, Authorize, cancellationToken);
    }
}
