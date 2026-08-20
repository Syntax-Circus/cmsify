using System.Security.Claims;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cmsify.Infrastructure.Persistence.Interceptors;

public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor httpContextAccessor;

    public AuditInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            AddAuditLogs(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void AddAuditLogs(DbContext context)
    {
        var actor = ResolveActor();
        var entries = context.ChangeTracker.Entries()
            .Where(entry => entry.Entity is not AuditLog and not WebhookDeliveryLog and not UserSession and not ApiClient)
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(entry => entry.Properties.Any(property => property.Metadata.Name == "Id"))
            .ToArray();

        foreach (var entry in entries)
        {
            var entityId = GetGuidValue(entry, "Id");
            if (!entityId.HasValue || entityId.Value == Guid.Empty)
            {
                continue;
            }

            var action = GetAuditAction(entry);
            var delta = AuditDeltaBuilder.Build(entry);

            context.Set<AuditLog>().Add(new AuditLog
            {
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = entityId.Value,
                Action = action,
                ActorUserId = actor.UserId,
                ActorApiClientId = actor.ApiClientId,
                Timestamp = DateTimeOffset.UtcNow,
                ChangeDelta = delta,
                WorkspaceId = entry.Entity is Workspace ? entityId.Value : GetGuidValue(entry, "WorkspaceId")
            });
        }
    }

    private (Guid? UserId, Guid? ApiClientId) ResolveActor()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (httpContextAccessor.HttpContext?.Items.TryGetValue(CurrentActorHttpContextKeys.ItemName, out var actorItem) == true
            && actorItem is ICurrentActor currentActor
            && currentActor.IsAuthenticated)
        {
            return (currentActor.UserId, currentActor.ApiClientId);
        }

        if (user?.Identity?.IsAuthenticated != true)
        {
            return (null, null);
        }

        var apiClientClaim = user.FindFirst("cmsify_api_client_id")?.Value;
        if (Guid.TryParse(apiClientClaim, out var apiClientId))
        {
            return (null, apiClientId);
        }

        var userClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.FindFirst("cmsify_user_id")?.Value;

        return Guid.TryParse(userClaim, out var userId)
            ? (userId, null)
            : (null, null);
    }

    private static AuditAction GetAuditAction(EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
        {
            return AuditAction.Created;
        }

        if (entry.State == EntityState.Deleted || SoftDeleteWasSet(entry))
        {
            return AuditAction.Deleted;
        }

        if (entry.Entity is ContentItem && entry.Properties.Any(property => property.Metadata.Name == nameof(ContentItem.Status) && property.IsModified))
        {
            return AuditAction.StatusChanged;
        }

        return AuditAction.Updated;
    }

    private static bool SoftDeleteWasSet(EntityEntry entry)
    {
        var property = entry.Properties.FirstOrDefault(property => property.Metadata.Name == nameof(SoftDeletableEntity.IsDeleted));
        return property?.IsModified == true && property.CurrentValue is true;
    }

    private static Guid? GetGuidValue(EntityEntry entry, string propertyName)
    {
        var property = entry.Properties.FirstOrDefault(property => property.Metadata.Name == propertyName);
        return property?.CurrentValue is Guid value ? value : null;
    }
}
