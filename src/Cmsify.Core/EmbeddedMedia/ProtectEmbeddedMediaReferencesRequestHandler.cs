using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IProtectEmbeddedMediaReferencesRequestHandler
{
    Task<Result<EmbeddedMediaReferenceOutcome>> HandleAsync(ProtectEmbeddedMediaReferencesRequest request, CancellationToken cancellationToken);
}
public sealed class ProtectEmbeddedMediaReferencesRequestHandler(IEmbeddedMediaReferenceRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IProtectEmbeddedMediaReferencesRequestHandler
{
    public async Task<Result<EmbeddedMediaReferenceOutcome>> HandleAsync(ProtectEmbeddedMediaReferencesRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || string.IsNullOrWhiteSpace(request.ConsumerNamespace) || request.ConsumerNamespace.Length > 100 || request.ReferenceKey == Guid.Empty || request.OperationKey == Guid.Empty || request.Manifests is null || request.Manifests.Count is < 1 or > EmbeddedPhotoPolicy.MaxReferenceManifests || request.Manifests.Any(m => m is null || m.AssetId == Guid.Empty || m.Revision < 1 || !EmbeddedPhotoPolicy.IsSha256(m.ManifestSha256)))
            return Result<EmbeddedMediaReferenceOutcome>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        request = request with { Manifests = Array.AsReadOnly(request.Manifests.ToArray()) };
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.Protect, request.WorkspaceId, null, null, request.ConsumerNamespace, request.ReferenceKey);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, true, cancellationToken) is { } error)
            return Result<EmbeddedMediaReferenceOutcome>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            (operation.AssetId is null || resolved.AssetId == operation.AssetId) &&
            (operation.Revision is null || resolved.Revision == operation.Revision) &&
            resolved.ConsumerNamespace == operation.ConsumerNamespace && resolved.ReferenceKey == operation.ReferenceKey &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, true, token) is null;
        return await repository.ProtectAsync(request, actorId, Authorize, cancellationToken);
    }
}
