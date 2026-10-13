using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IEnrollEmbeddedPhotoUploadRequestHandler
{
    Task<Result<EmbeddedPhotoStatus>> HandleAsync(EnrollEmbeddedPhotoUploadRequest request, CancellationToken cancellationToken);
}

public sealed class EnrollEmbeddedPhotoUploadRequestHandler(IEmbeddedPhotoProcessingRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IEnrollEmbeddedPhotoUploadRequestHandler
{
    public async Task<Result<EmbeddedPhotoStatus>> HandleAsync(EnrollEmbeddedPhotoUploadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.UploadReceiptId == Guid.Empty ||
            request.OperationKey == Guid.Empty || request.ExpectedLatestRevision < 1 || !EmbeddedPhotoPolicy.IsSha256(request.SourceSha256) ||
            string.IsNullOrWhiteSpace(request.PolicyId) || request.PolicyId.Length > 100)
            return Result<EmbeddedPhotoStatus>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.EnrollUpload, request.WorkspaceId, request.AssetId);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, true, cancellationToken) is { } error)
            return Result<EmbeddedPhotoStatus>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            resolved.AssetId == request.AssetId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, true, token) is null;
        return await repository.EnrollAsync(request, actorId, Authorize, cancellationToken);
    }
}
