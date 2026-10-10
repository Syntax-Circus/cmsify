using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IGetEmbeddedPhotoUploadOutcomeRequestHandler
{
    Task<Result<EmbeddedPhotoUploadOutcome>> HandleAsync(GetEmbeddedPhotoUploadOutcomeRequest request, CancellationToken cancellationToken);
}

public sealed class GetEmbeddedPhotoUploadOutcomeRequestHandler(IEmbeddedPhotoUploadRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IGetEmbeddedPhotoUploadOutcomeRequestHandler
{
    public async Task<Result<EmbeddedPhotoUploadOutcome>> HandleAsync(GetEmbeddedPhotoUploadOutcomeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || request.SessionId == Guid.Empty)
            return Result<EmbeddedPhotoUploadOutcome>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadUploadOutcome, request.WorkspaceId, null);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, false, cancellationToken) is { } error)
            return Result<EmbeddedPhotoUploadOutcome>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.WorkspaceId == request.WorkspaceId &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, false, token) is null;
        return await repository.GetOutcomeAsync(request, actorId, Authorize, cancellationToken);
    }
}
