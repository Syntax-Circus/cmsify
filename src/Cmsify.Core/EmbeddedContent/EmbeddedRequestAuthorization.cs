using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;

namespace Cmsify.Core.EmbeddedContent;

internal static class EmbeddedRequestAuthorization
{
    internal static async Task<ResultError?> CheckAsync(ICurrentActor actor, IWorkspaceAuthorizationService workspace,
        IEmbeddedContentAuthorizationService resource, EmbeddedContentOperation operation, bool write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.IsAuthenticated || (write && (actor.UserId is null || actor.UserId == Guid.Empty)))
            return new("authentication-required", "Authentication is required.", ResultErrorKind.Unauthenticated);
        if (actor.Role < (write ? UserRole.Editor : UserRole.Reader))
            return new("forbidden", "Permission denied.", ResultErrorKind.Forbidden);
        if (!(write ? await workspace.CanWriteWorkspaceAsync(operation.WorkspaceId, cancellationToken)
                    : await workspace.CanReadWorkspaceAsync(operation.WorkspaceId, cancellationToken)))
            return new("not-found", "Resource unavailable.", ResultErrorKind.NotFound);
        if (!await resource.CanExecuteAsync(operation, cancellationToken))
            return new("forbidden", "Permission denied.", ResultErrorKind.Forbidden);
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }
}
