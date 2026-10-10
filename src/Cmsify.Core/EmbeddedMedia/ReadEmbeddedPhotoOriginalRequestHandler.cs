using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IReadEmbeddedPhotoOriginalRequestHandler
{
    Task<Result<EmbeddedPhotoReadOutput>> HandleAsync(ReadEmbeddedPhotoOriginalRequest request, CancellationToken cancellationToken);
}
public sealed class ReadEmbeddedPhotoOriginalRequestHandler(IEmbeddedPhotoReadRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IReadEmbeddedPhotoOriginalRequestHandler
{
    public async Task<Result<EmbeddedPhotoReadOutput>> HandleAsync(ReadEmbeddedPhotoOriginalRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.Sink is null)
            return Result<EmbeddedPhotoReadOutput>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadOriginal, request.WorkspaceId, request.AssetId, null, null, null);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, false, cancellationToken) is { } error)
            return Result<EmbeddedPhotoReadOutput>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            (operation.AssetId is null || resolved.AssetId == operation.AssetId) &&
            (operation.Revision is null || resolved.Revision == operation.Revision) &&
            resolved.ConsumerNamespace == operation.ConsumerNamespace && resolved.ReferenceKey == operation.ReferenceKey &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, false, token) is null;
        return await repository.ReadOriginalAsync(request, actorId, Authorize, cancellationToken);
    }
}
