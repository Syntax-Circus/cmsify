using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IBeginEmbeddedPhotoUploadRequestHandler
{
    Task<Result<EmbeddedPhotoUploadOutcome>> HandleAsync(BeginEmbeddedPhotoUploadRequest request, CancellationToken cancellationToken);
}

public sealed class BeginEmbeddedPhotoUploadRequestHandler(IEmbeddedPhotoUploadRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IBeginEmbeddedPhotoUploadRequestHandler
{
    public async Task<Result<EmbeddedPhotoUploadOutcome>> HandleAsync(BeginEmbeddedPhotoUploadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.AssetId == Guid.Empty || request.OperationKey == Guid.Empty || string.IsNullOrWhiteSpace(request.DisplayFileName) || request.DisplayFileName.Length > 255 || request.DeclaredLength <= 0 || request.DeclaredLength > EmbeddedPhotoPolicy.MaxBytes || string.IsNullOrWhiteSpace(request.PolicyId) || request.PolicyId.Length > 100)
            return Result<EmbeddedPhotoUploadOutcome>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.BeginUpload, request.WorkspaceId, request.AssetId);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, true, cancellationToken) is { } error)
            return Result<EmbeddedPhotoUploadOutcome>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.WorkspaceId == request.WorkspaceId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, true, token) is null;
        return await repository.BeginAsync(request, actorId, Authorize, cancellationToken);
    }
}
