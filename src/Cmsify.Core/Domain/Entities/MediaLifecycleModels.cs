namespace Cmsify.Core.Domain.Entities;

public sealed class MediaDeletionIntent : Entity
{
    public Guid? MediaAssetId { get; set; }
    public required string Provider { get; set; }
    public required string StorageKey { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MediaReconciliationCheckpoint : Entity
{
    public required string Provider { get; set; }
    public required string Prefix { get; set; }
    public string? AfterKey { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? LastScanStartedAt { get; set; }
    public DateTimeOffset? LastScanCompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
