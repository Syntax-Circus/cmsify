using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.Media;

/// <summary>Expected revision accepts the current microsecond version or legacy UTC ticks.</summary>
public sealed record DeleteMediaAssetRequest(Guid WorkspaceId, Guid AssetId, long? ExpectedRevision);
public sealed record MediaAssetDeletionOutcome(bool Deleted, IReadOnlyList<Guid> ReferencedBy);
public interface IMediaAssetDeletionRepository
{
    Task<Result<MediaAssetDeletionOutcome>> DeleteAsync(DeleteMediaAssetRequest request, Guid? actorUserId,
        Func<CancellationToken, Task<bool>> authorize, CancellationToken cancellationToken);
}
public interface IDeleteMediaAssetRequestHandler
{
    Task<Result<MediaAssetDeletionOutcome>> HandleAsync(DeleteMediaAssetRequest request, CancellationToken cancellationToken);
}
public sealed class DeleteMediaAssetRequestHandler(IMediaAssetDeletionRepository repository, ICurrentActor actor,
    IWorkspaceAuthorizationService workspace) : IDeleteMediaAssetRequestHandler
{
    public async Task<Result<MediaAssetDeletionOutcome>> HandleAsync(DeleteMediaAssetRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.IsAuthenticated || actor.UserId is null && actor.ApiClientId is null)
            return Result<MediaAssetDeletionOutcome>.Failure(new("authentication-required", "Authentication is required.", ResultErrorKind.Unauthenticated));
        if (actor.Role < UserRole.Editor) return Result<MediaAssetDeletionOutcome>.Failure(new("forbidden", "Permission denied.", ResultErrorKind.Forbidden));
        if (!await workspace.CanWriteWorkspaceAsync(request.WorkspaceId, cancellationToken))
            return Result<MediaAssetDeletionOutcome>.Failure(new("not-found", "Resource unavailable.", ResultErrorKind.NotFound));
        var subject = actor.UserId; var client = actor.ApiClientId;
        async Task<bool> Authorize(CancellationToken ct) => actor.IsAuthenticated && actor.Role >= UserRole.Editor &&
            actor.UserId == subject && actor.ApiClientId == client && await workspace.CanWriteWorkspaceAsync(request.WorkspaceId, ct);
        return await repository.DeleteAsync(request, subject, Authorize, cancellationToken);
    }
}
