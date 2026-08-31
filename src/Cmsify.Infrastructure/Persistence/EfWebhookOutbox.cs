using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Infrastructure.Persistence;

/// <summary>
/// Tracks durable webhook events in the caller's current <see cref="CmsifyDbContext"/> unit of work.
/// </summary>
public sealed class EfWebhookOutbox(CmsifyDbContext dbContext) : IWebhookOutbox
{
    public void Enqueue(string eventType, Guid? workspaceId, Guid entityId, JsonElement payload, DateTimeOffset occurredAt)
    {
        dbContext.WebhookOutboxEvents.Add(new WebhookOutboxEvent
        {
            Id = Guid.CreateVersion7(),
            EventType = eventType,
            WorkspaceId = workspaceId,
            EntityId = entityId,
            Payload = payload.Clone(),
            OccurredAt = occurredAt,
            CreatedAt = occurredAt
        });
    }
}
