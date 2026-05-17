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
    public Guid WebhookEndpointId { get; set; }

    public required string EventType { get; set; }

    public JsonElement Payload { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public int? StatusCode { get; set; }

    public bool IsDelivered { get; set; }

    public bool IsFailed { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record WebhookEvent(
    string EventType,
    Guid? WorkspaceId,
    Guid EntityId,
    JsonElement Payload,
    DateTimeOffset OccurredAt);
