using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedMedia;

public sealed class DenyEmbeddedPhotoAuthorizationService : IEmbeddedPhotoAuthorizationService
{
    public Task<bool> CanExecuteAsync(EmbeddedPhotoOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

internal static class EmbeddedPhotoAuthorization
{
    internal static async Task<ResultError?> CheckAsync(ICurrentActor actor,
        IWorkspaceAuthorizationService workspace, IEmbeddedPhotoAuthorizationService resource,
        EmbeddedPhotoOperation operation, bool write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.IsAuthenticated || actor.UserId is null || actor.UserId == Guid.Empty)
            return new("authentication-required", "Authentication is required.", ResultErrorKind.Unauthenticated);
        if (actor.Role < (write ? UserRole.Editor : UserRole.Reader))
            return new(EmbeddedPhotoErrors.Forbidden, "Permission denied.", ResultErrorKind.Forbidden);
        if (!(write ? await workspace.CanWriteWorkspaceAsync(operation.WorkspaceId, cancellationToken)
                    : await workspace.CanReadWorkspaceAsync(operation.WorkspaceId, cancellationToken)))
            return new(EmbeddedPhotoErrors.NotFound, "Resource unavailable.", ResultErrorKind.NotFound);
        if (!await resource.CanExecuteAsync(operation, cancellationToken))
            return new(EmbeddedPhotoErrors.Forbidden, "Permission denied.", ResultErrorKind.Forbidden);
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }
}
