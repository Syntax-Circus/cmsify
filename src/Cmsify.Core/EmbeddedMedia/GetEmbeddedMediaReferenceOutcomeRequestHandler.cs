using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public interface IGetEmbeddedMediaReferenceOutcomeRequestHandler
{
    Task<Result<EmbeddedMediaReferenceOutcome>> HandleAsync(GetEmbeddedMediaReferenceOutcomeRequest request, CancellationToken cancellationToken);
}
public sealed class GetEmbeddedMediaReferenceOutcomeRequestHandler(IEmbeddedMediaReferenceRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspaceAuthorization, IEmbeddedPhotoAuthorizationService resourceAuthorization)
    : IGetEmbeddedMediaReferenceOutcomeRequestHandler
{
    public async Task<Result<EmbeddedMediaReferenceOutcome>> HandleAsync(GetEmbeddedMediaReferenceOutcomeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorkspaceId == Guid.Empty || string.IsNullOrWhiteSpace(request.ConsumerNamespace) || request.ConsumerNamespace.Length > 100 || request.ReferenceKey == Guid.Empty)
            return Result<EmbeddedMediaReferenceOutcome>.Failure(new(EmbeddedPhotoErrors.Validation, "Invalid request.", ResultErrorKind.Validation));
        var operation = new EmbeddedPhotoOperation(EmbeddedPhotoOperationKind.ReadReferenceOutcome, request.WorkspaceId, null, null, request.ConsumerNamespace, request.ReferenceKey);
        if (await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, operation, false, cancellationToken) is { } error)
            return Result<EmbeddedMediaReferenceOutcome>.Failure(error);
        var actorId = actor.UserId!.Value;
        async Task<bool> Authorize(EmbeddedPhotoOperation resolved, CancellationToken token) =>
            actor.UserId == actorId && resolved.Kind == operation.Kind && resolved.WorkspaceId == request.WorkspaceId &&
            (operation.AssetId is null || resolved.AssetId == operation.AssetId) &&
            (operation.Revision is null || resolved.Revision == operation.Revision) &&
            resolved.ConsumerNamespace == operation.ConsumerNamespace && resolved.ReferenceKey == operation.ReferenceKey &&
            await EmbeddedPhotoAuthorization.CheckAsync(actor, workspaceAuthorization, resourceAuthorization, resolved, false, token) is null;
        return await repository.GetOutcomeAsync(request, actorId, Authorize, cancellationToken);
    }
}
