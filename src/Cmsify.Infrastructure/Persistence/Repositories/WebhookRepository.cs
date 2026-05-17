using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class WebhookRepository : IWebhookRepository
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;

    public WebhookRepository(CmsifyDbContext dbContext, ICurrentActor currentActor)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
    }

    public async Task<WebhookEndpointDto?> GetEndpointAsync(Guid id, CancellationToken ct = default) =>
        (await dbContext.WebhookEndpoints.AsNoTracking()
            .ScopeToActorWorkspace(currentActor)
            .Include(endpoint => endpoint.Subscriptions)
            .FirstOrDefaultAsync(endpoint => endpoint.Id == id, ct))?.ToDto();

    public Task<PagedResult<WebhookEndpointDto>> ListEndpointsAsync(Guid workspaceId, PageRequest page, CancellationToken ct = default) =>
        dbContext.WebhookEndpoints.AsNoTracking()
            .Include(endpoint => endpoint.Subscriptions)
            .Where(endpoint => endpoint.WorkspaceId == workspaceId)
            .ScopeToActorWorkspace(currentActor)
            .OrderBy(endpoint => endpoint.Name)
            .ToPagedResultAsync(page, endpoint => endpoint.ToDto(), ct);

    public async Task<WebhookEndpointDto> CreateEndpointAsync(CreateWebhookEndpointCommand command, string encryptedSecret, CancellationToken ct = default)
    {
        var entity = new WebhookEndpoint
        {
            WorkspaceId = command.WorkspaceId,
            Name = command.Name,
            Url = command.Url,
            Secret = encryptedSecret,
            CreatedByUserId = command.CreatedByUserId,
            IsActive = true
        };

        foreach (var eventType in command.EventTypes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            entity.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = entity.Id, EventType = eventType });
        }

        dbContext.WebhookEndpoints.Add(entity);
        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task<WebhookEndpointDto> UpdateEndpointAsync(UpdateWebhookEndpointCommand command, CancellationToken ct = default)
    {
        var entity = await dbContext.WebhookEndpoints
            .ScopeToActorWorkspace(currentActor)
            .Include(endpoint => endpoint.Subscriptions)
            .FirstAsync(endpoint => endpoint.Id == command.Id, ct);

        entity.Name = command.Name;
        entity.Url = command.Url;
        entity.IsActive = command.IsActive;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        dbContext.WebhookSubscriptions.RemoveRange(entity.Subscriptions);
        foreach (var eventType in command.EventTypes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            entity.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = entity.Id, EventType = eventType });
        }

        await dbContext.SaveChangesAsync(ct);
        return entity.ToDto();
    }

    public async Task AddDeliveryLogAsync(WebhookDeliveryLogDto log, CancellationToken ct = default)
    {
        dbContext.WebhookDeliveryLogs.Add(new WebhookDeliveryLog
        {
            Id = log.Id == Guid.Empty ? Guid.CreateVersion7() : log.Id,
            WebhookEndpointId = log.WebhookEndpointId,
            EventType = log.EventType,
            Payload = log.Payload,
            AttemptCount = log.AttemptCount,
            LastAttemptAt = log.LastAttemptAt,
            NextRetryAt = log.NextRetryAt,
            StatusCode = log.StatusCode,
            IsDelivered = log.IsDelivered,
            IsFailed = log.IsFailed,
            CreatedAt = log.CreatedAt == default ? DateTimeOffset.UtcNow : log.CreatedAt
        });
        await dbContext.SaveChangesAsync(ct);
    }

    public Task<PagedResult<WebhookDeliveryLogDto>> ListDeliveryLogsAsync(Guid endpointId, PageRequest page, CancellationToken ct = default) =>
        dbContext.WebhookDeliveryLogs.AsNoTracking()
            .Where(log => log.WebhookEndpointId == endpointId)
            .OrderByDescending(log => log.CreatedAt)
            .ToPagedResultAsync(page, log => log.ToDto(), ct);

    public async Task<IReadOnlyList<WebhookDispatchTargetDto>> GetActiveEndpointsForEventAsync(string eventType, Guid? workspaceId, CancellationToken ct = default) =>
        await dbContext.WebhookEndpoints.AsNoTracking()
            .Where(endpoint => endpoint.IsActive && (!workspaceId.HasValue || endpoint.WorkspaceId == workspaceId.Value))
            .Where(endpoint => endpoint.Subscriptions.Any(subscription => subscription.EventType == eventType))
            .Select(endpoint => new WebhookDispatchTargetDto(endpoint.Id, endpoint.WorkspaceId, endpoint.Url, endpoint.Secret))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PendingWebhookDeliveryDto>> GetPendingDeliveryLogsAsync(DateTimeOffset now, int limit, CancellationToken ct = default) =>
        await dbContext.WebhookDeliveryLogs.AsNoTracking()
            .Where(log => !log.IsDelivered && !log.IsFailed && log.NextRetryAt <= now)
            .OrderBy(log => log.NextRetryAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Join(
                dbContext.WebhookEndpoints,
                log => log.WebhookEndpointId,
                endpoint => endpoint.Id,
                (log, endpoint) => new PendingWebhookDeliveryDto(log.Id, log.WebhookEndpointId, endpoint.WorkspaceId, log.EventType, endpoint.Url, endpoint.Secret, log.Payload, log.AttemptCount, log.NextRetryAt))
            .ToListAsync(ct);

    public async Task MarkDeliverySucceededAsync(Guid deliveryLogId, int statusCode, CancellationToken ct = default)
    {
        var entity = await dbContext.WebhookDeliveryLogs.FirstAsync(log => log.Id == deliveryLogId, ct);
        entity.AttemptCount++;
        entity.LastAttemptAt = DateTimeOffset.UtcNow;
        entity.StatusCode = statusCode;
        entity.IsDelivered = true;
        entity.IsFailed = false;
        entity.NextRetryAt = null;
        await dbContext.SaveChangesAsync(ct);
    }

    public async Task MarkDeliveryFailedAsync(Guid deliveryLogId, int? statusCode, DateTimeOffset nextRetryAt, bool isFailed, CancellationToken ct = default)
    {
        var entity = await dbContext.WebhookDeliveryLogs.FirstAsync(log => log.Id == deliveryLogId, ct);
        entity.AttemptCount++;
        entity.LastAttemptAt = DateTimeOffset.UtcNow;
        entity.StatusCode = statusCode;
        entity.IsDelivered = false;
        entity.IsFailed = isFailed;
        entity.NextRetryAt = isFailed ? null : nextRetryAt;
        await dbContext.SaveChangesAsync(ct);
    }
}
