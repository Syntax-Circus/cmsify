using System.Text.Json;
using Cmsify.Core.Domain.Enums;

namespace Cmsify.Core.Domain.Entities;

public sealed class AuditLog : Entity
{
    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    public AuditAction Action { get; set; }

    public Guid? ActorUserId { get; set; }

    public Guid? ActorApiClientId { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public JsonElement? ChangeDelta { get; set; }

    public Guid? WorkspaceId { get; set; }
}
