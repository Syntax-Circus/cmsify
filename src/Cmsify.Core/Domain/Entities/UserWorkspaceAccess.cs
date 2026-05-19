using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Domain.Entities;

public sealed class UserWorkspaceAccess : Entity
{
    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public WorkspaceAccessLevel AccessLevel { get; set; }

    public User? User { get; set; }

    public Workspace? Workspace { get; set; }
}
