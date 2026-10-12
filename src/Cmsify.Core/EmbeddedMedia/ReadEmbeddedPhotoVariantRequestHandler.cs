using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IReadEmbeddedPhotoVariantRequestHandler
{
    Task<Result<EmbeddedPhotoReadOutput>> HandleAsync(ReadEmbeddedPhotoVariantRequest request, CancellationToken cancellationToken);
}
public sealed class ReadEmbeddedPhotoVariantRequestHandler(IEmbeddedPhotoReadRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IReadEmbeddedPhotoVariantRequestHandler
{
    public async Task<Result<EmbeddedPhotoReadOutput>> HandleAsync(ReadEmbeddedPhotoVariantRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.Manifest is null || request.Manifest.AssetId == Guid.Empty || request.Manifest.Revision < 1 || !EmbeddedPhotoPolicy.IsSha256(request.Manifest.ManifestSha256) || request.Authority is null || request.Sink is null || !EmbeddedPhotoPolicy.RequiredVariants.Any(v => v.VariantKey == request.VariantKey))
            return Result<EmbeddedPhotoReadOutput>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadVariant, request.WorkspaceId, request.Manifest.AssetId, request.Manifest.Revision, request.Authority.ConsumerNamespace, request.Authority.ReferenceKey);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, false, cancellationToken) is { } error)
            return Result<EmbeddedPhotoReadOutput>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            (operation.AssetId is null || resolved.AssetId == operation.AssetId) &&
            (operation.Revision is null || resolved.Revision == operation.Revision) &&
            resolved.ConsumerNamespace == operation.ConsumerNamespace && resolved.ReferenceKey == operation.ReferenceKey &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, false, token) is null;
        return await repository.ReadVariantAsync(request, actorId, Authorize, cancellationToken);
    }
}
