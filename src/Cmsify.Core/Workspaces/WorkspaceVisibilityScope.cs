namespace Cmsify.Core.Workspaces;

/// <summary>An immutable workspace query decision. Restricted IDs never augment CMS-managed privileges.</summary>
public sealed class WorkspaceVisibilityScope
{
    private WorkspaceVisibilityScope(WorkspaceVisibilityScopeKind kind, IReadOnlyList<Guid> workspaceIds)
    {
        Kind = kind;
        WorkspaceIds = workspaceIds;
    }

    public WorkspaceVisibilityScopeKind Kind { get; }
    public IReadOnlyList<Guid> WorkspaceIds { get; }
    public static WorkspaceVisibilityScope CmsManaged { get; } = new(WorkspaceVisibilityScopeKind.CmsManaged, Array.AsReadOnly(Array.Empty<Guid>()));
    public static WorkspaceVisibilityScope None { get; } = new(WorkspaceVisibilityScopeKind.Restricted, Array.AsReadOnly(Array.Empty<Guid>()));

    public static WorkspaceVisibilityScope ForWorkspaces(IEnumerable<Guid> workspaceIds)
    {
        ArgumentNullException.ThrowIfNull(workspaceIds);
        var ids = workspaceIds.Distinct().ToArray();
        if (ids.Contains(Guid.Empty))
            throw new ArgumentException("Workspace IDs must not be empty.", nameof(workspaceIds));
        return ids.Length == 0 ? None : new(WorkspaceVisibilityScopeKind.Restricted, Array.AsReadOnly(ids));
    }
}
