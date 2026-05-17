using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Domain.Entities;

public sealed class ApiClient : SoftDeletableEntity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string TokenHash { get; set; }

    public UserRole Role { get; set; }

    public Guid? WorkspaceId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset? ExpiresAt { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
