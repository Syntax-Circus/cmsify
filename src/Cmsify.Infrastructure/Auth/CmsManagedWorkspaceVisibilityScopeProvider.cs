using Cmsify.Core.Workspaces;

namespace Cmsify.Infrastructure.Auth;

internal sealed class CmsManagedWorkspaceVisibilityScopeProvider : IWorkspaceVisibilityScopeProvider
{
    public Task<WorkspaceVisibilityScope> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(WorkspaceVisibilityScope.CmsManaged);
    }
}
