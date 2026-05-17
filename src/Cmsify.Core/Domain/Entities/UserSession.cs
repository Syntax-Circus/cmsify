namespace Cmsify.Core.Domain.Entities;

public sealed class UserSession : Entity
{
    public Guid UserId { get; set; }

    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public string? IpAddress { get; set; }
}
