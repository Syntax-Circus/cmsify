using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Auth;

public sealed class WorkspaceAuthorizationService : IWorkspaceAuthorizationService
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public WorkspaceAuthorizationService(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public Task<bool> CanReadWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (!currentActor.IsAuthenticated)
        {
            return Task.FromResult(false);
        }

        if (currentActor.IsSuperAdmin)
        {
            return Task.FromResult(true);
        }

        if (currentActor.WorkspaceId.HasValue)
        {
            return Task.FromResult(currentActor.WorkspaceId == workspaceId);
        }

        return currentActor.UserId.HasValue && currentActor.Role >= UserRole.Editor
            ? dbContext.UserWorkspaceAccesses.AsNoTracking().AnyAsync(access =>
                access.UserId == currentActor.UserId.Value
                && access.WorkspaceId == workspaceId
                && (access.AccessLevel == WorkspaceAccessLevel.Read || access.AccessLevel == WorkspaceAccessLevel.Write), ct)
            : Task.FromResult(false);
    }

    public Task<bool> CanWriteWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (!currentActor.IsAuthenticated)
        {
            return Task.FromResult(false);
        }

        if (currentActor.IsSuperAdmin)
        {
            return Task.FromResult(true);
        }

        if (currentActor.WorkspaceId.HasValue)
        {
            return Task.FromResult(currentActor.WorkspaceId == workspaceId && currentActor.Role >= UserRole.Editor);
        }

        return currentActor.UserId.HasValue
            ? dbContext.UserWorkspaceAccesses.AsNoTracking().AnyAsync(access =>
                access.UserId == currentActor.UserId.Value
                && access.WorkspaceId == workspaceId
                && access.AccessLevel == WorkspaceAccessLevel.Write, ct)
            : Task.FromResult(false);
    }
}
