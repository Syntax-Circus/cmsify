using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Domain.Entities;

public sealed class User : SoftDeletableEntity
{
    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public required string PasswordHash { get; set; }

    public UserRole Role { get; set; }

    public bool IsSuperAdmin { get; set; }

    public bool MustChangePassword { get; set; }

    public string? TimeZoneId { get; set; }

    public string? Theme { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<UserWorkspaceAccess> WorkspaceAccesses { get; } = new List<UserWorkspaceAccess>();
}
