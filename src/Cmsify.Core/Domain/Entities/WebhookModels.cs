using System.Text.Json;

namespace Cmsify.Core.Domain.Entities;

public sealed class WebhookEndpoint : SoftDeletableEntity
{
    public Guid WorkspaceId { get; set; }

    public required string Name { get; set; }

    public required string Url { get; set; }

    public required string Secret { get; set; }

    public bool IsActive { get; set; } = true;

    public Guid CreatedByUserId { get; set; }

    public IList<WebhookSubscription> Subscriptions { get; } = new List<WebhookSubscription>();
}

public sealed class WebhookSubscription
{
    public Guid WebhookEndpointId { get; set; }

    public required string EventType { get; set; }
}

public sealed class WebhookDeliveryLog : Entity
{
    // This is also the consumer deduplication key.  The migration backfills it
    // for historical rows before making the column required.
    public Guid WebhookEventId { get; set; } = Guid.CreateVersion7();
    public Guid WebhookEndpointId { get; set; }

    public required string EventType { get; set; }

    public JsonElement Payload { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public int? StatusCode { get; set; }

    public bool IsDelivered { get; set; }

    public bool IsFailed { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public string? LeaseOwner { get; set; }

    public Guid? LeaseToken { get; set; }

    public string? LastError { get; set; }

    public bool IsDeadLetter { get; set; }

    public DateTimeOffset? DeadLetteredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A durable, at-least-once webhook event. Delivery intents are materialized from this record.
/// </summary>
public sealed class WebhookOutboxEvent : Entity
{
    public required string EventType { get; set; }

    public Guid? WorkspaceId { get; set; }

    public Guid EntityId { get; set; }

    public JsonElement Payload { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? LeaseOwner { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
