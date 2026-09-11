namespace Cmsify.Core.Domain.Entities;

public sealed class IpBanEvent : Entity
{
    public required string IpAddress { get; set; }

    public int RejectionCount { get; set; }

    public DateTimeOffset BannedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset BannedUntil { get; set; }

    public string? RequestPath { get; set; }
}
