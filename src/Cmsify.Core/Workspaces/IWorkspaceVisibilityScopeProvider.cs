namespace Cmsify.Core.Workspaces;

/// <summary>Resolves current workspace visibility independently of capability authorization.</summary>
public interface IWorkspaceVisibilityScopeProvider
{
    Task<WorkspaceVisibilityScope> ResolveAsync(CancellationToken cancellationToken = default);
}
