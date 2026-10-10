using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IWriteEmbeddedPhotoUploadRequestHandler
{
    Task<Result<EmbeddedPhotoUploadOutcome>> HandleAsync(WriteEmbeddedPhotoUploadRequest request, CancellationToken cancellationToken);
}

public sealed class WriteEmbeddedPhotoUploadRequestHandler(IEmbeddedPhotoUploadRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IWriteEmbeddedPhotoUploadRequestHandler
{
    public async Task<Result<EmbeddedPhotoUploadOutcome>> HandleAsync(WriteEmbeddedPhotoUploadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.SessionId == Guid.Empty || request.OperationKey == Guid.Empty || request.Source is null)
            return Result<EmbeddedPhotoUploadOutcome>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.WriteUpload, request.WorkspaceId, null);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, true, cancellationToken) is { } error)
            return Result<EmbeddedPhotoUploadOutcome>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.WorkspaceId == request.WorkspaceId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, true, token) is null;
        return await repository.WriteAsync(request, actorId, Authorize, cancellationToken);
    }
}
