using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Repositories;

public sealed class WebhookRepository : IWebhookRepository
{
    private const int MaxClaimBatchSize = 500;
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromMinutes(30);
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly ISecretProtector secretProtector;

    public WebhookRepository(CmsifyDbContext dbContext, ICurrentActor currentActor, ISecretProtector secretProtector)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        this.secretProtector = secretProtector;
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

    public async Task<IReadOnlyList<WebhookDispatchTargetDto>> GetActiveEndpointsForEventAsync(string eventType, Guid? workspaceId, CancellationToken ct = default)
    {
        var targets = await dbContext.WebhookEndpoints.AsNoTracking()
            .Where(endpoint => endpoint.IsActive && !endpoint.IsDeleted && (!workspaceId.HasValue || endpoint.WorkspaceId == workspaceId.Value))
            .Where(endpoint => endpoint.Subscriptions.Any(subscription => subscription.EventType == eventType))
            .Select(endpoint => new WebhookDispatchTargetDto(endpoint.Id, endpoint.WorkspaceId, endpoint.Url, endpoint.Secret))
            .ToListAsync(ct);
        return targets.Select(target => target with { Secret = secretProtector.Unprotect(target.Secret) }).ToArray();
    }

    public async Task<IReadOnlyList<PendingWebhookDeliveryDto>> ClaimPendingDeliveryLogsAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default)
    {
        ValidateClaimArguments(workerId, leaseDuration, limit);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var logs = await dbContext.WebhookDeliveryLogs
            .FromSqlInterpolated($"""
                SELECT * FROM webhook_delivery_logs
                WHERE NOT is_delivered AND NOT is_failed AND next_retry_at <= {now} AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
                  AND EXISTS (SELECT 1 FROM webhook_endpoints endpoint WHERE endpoint.id = webhook_delivery_logs.webhook_endpoint_id AND endpoint.is_active AND NOT endpoint.is_deleted)
                ORDER BY next_retry_at
                FOR UPDATE SKIP LOCKED
                LIMIT {limit}
                """)
            .ToListAsync(ct);

        if (logs.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return [];
        }

        var endpointIds = logs.Select(log => log.WebhookEndpointId).Distinct().ToArray();
        var endpoints = await dbContext.WebhookEndpoints.AsNoTracking()
            .Where(endpoint => endpointIds.Contains(endpoint.Id))
            .ToDictionaryAsync(endpoint => endpoint.Id, ct);
        var leaseUntil = now.Add(leaseDuration);
        var reclaimed = new Dictionary<Guid, bool>();
        foreach (var log in logs)
        {
            reclaimed[log.Id] = log.LeaseExpiresAt.HasValue;
            log.LeaseExpiresAt = leaseUntil;
            log.LeaseOwner = workerId;
            log.LeaseToken = Guid.CreateVersion7();
        }

        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var claims = logs
            .Select(log =>
            {
                var endpoint = endpoints[log.WebhookEndpointId];
                return new PendingWebhookDeliveryDto(log.Id, log.WebhookEndpointId, log.WebhookEventId, endpoint.WorkspaceId, log.EventType, endpoint.Url, secretProtector.Unprotect(endpoint.Secret), log.Payload, log.AttemptCount, log.NextRetryAt, log.LeaseOwner!, log.LeaseToken!.Value, reclaimed[log.Id]);
            })
            .ToArray();
        foreach (var claim in claims)
        {
            CmsifyOperationalMetrics.RecordDeliveryClaim(claim.WasReclaimed);
        }
        CmsifyOperationalMetrics.ReportDueDeliveryDepth(await dbContext.WebhookDeliveryLogs.CountAsync(log => !log.IsDelivered && !log.IsFailed && log.NextRetryAt <= now, ct));
        return claims;
    }

    public async Task<IReadOnlyList<ClaimedWebhookOutboxEventDto>> ClaimOutboxEventsAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default)
    {
        ValidateClaimArguments(workerId, leaseDuration, limit);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var events = await dbContext.WebhookOutboxEvents.FromSqlInterpolated($"""
            SELECT * FROM webhook_outbox_events
            WHERE processed_at IS NULL AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            ORDER BY occurred_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);

        var reclaimed = new Dictionary<Guid, bool>();
        foreach (var evt in events)
        {
            reclaimed[evt.Id] = evt.LeaseExpiresAt.HasValue;
            evt.LeaseOwner = workerId;
            evt.LeaseToken = Guid.CreateVersion7();
            evt.LeaseExpiresAt = now.Add(leaseDuration);
        }

        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var claims = events.Select(evt => new ClaimedWebhookOutboxEventDto(evt.Id, evt.EventType, evt.WorkspaceId, evt.EntityId, evt.Payload, evt.OccurredAt, evt.LeaseOwner!, evt.LeaseToken!.Value, reclaimed[evt.Id])).ToArray();
        foreach (var claim in claims)
        {
            CmsifyOperationalMetrics.RecordOutboxClaim(claim.WasReclaimed);
        }
        CmsifyOperationalMetrics.ReportOutboxDepth(await dbContext.WebhookOutboxEvents.CountAsync(evt => evt.ProcessedAt == null, ct));
        return claims;
    }

    public async Task<bool> MaterializeOutboxEventAsync(ClaimedWebhookOutboxEventDto claim, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        // The lock is held through intent creation and completion.  A reclaimer
        // uses SKIP LOCKED, so it cannot replace this claim after it is fenced.
        var evt = await dbContext.WebhookOutboxEvents.FromSqlInterpolated($"""
            SELECT * FROM webhook_outbox_events
            WHERE id = {claim.Id} AND processed_at IS NULL
              AND lease_owner = {claim.LeaseOwner} AND lease_token = {claim.LeaseToken}
              AND lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (evt is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var endpointIds = await dbContext.WebhookEndpoints
            .Where(endpoint => endpoint.IsActive && !endpoint.IsDeleted && (!evt.WorkspaceId.HasValue || endpoint.WorkspaceId == evt.WorkspaceId.Value))
            .Where(endpoint => endpoint.Subscriptions.Any(subscription => subscription.EventType == evt.EventType))
            .Select(endpoint => endpoint.Id)
            .ToListAsync(ct);
        var existingEndpointIds = await dbContext.WebhookDeliveryLogs
            .Where(log => log.WebhookEventId == evt.Id && endpointIds.Contains(log.WebhookEndpointId))
            .Select(log => log.WebhookEndpointId)
            .ToListAsync(ct);
        foreach (var endpointId in endpointIds.Except(existingEndpointIds))
        {
            dbContext.WebhookDeliveryLogs.Add(new WebhookDeliveryLog
            {
                Id = Guid.CreateVersion7(),
                WebhookEventId = evt.Id,
                WebhookEndpointId = endpointId,
                EventType = evt.EventType,
                Payload = evt.Payload,
                NextRetryAt = evt.OccurredAt,
                CreatedAt = evt.CreatedAt
            });
        }

        evt.ProcessedAt = now;
        evt.LeaseOwner = null;
        evt.LeaseToken = null;
        evt.LeaseExpiresAt = null;
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        CmsifyOperationalMetrics.RecordOutboxMaterialized();
        return true;
    }

    public async Task<WebhookRetentionCleanupResult> CleanupRetentionAsync(DateTimeOffset olderThan, int batchSize, CancellationToken ct = default)
    {
        if (batchSize is < 1 or > MaxClaimBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var outboxDeleted = await dbContext.WebhookOutboxEvents
            .Where(evt => evt.ProcessedAt.HasValue && evt.ProcessedAt.Value <= olderThan)
            .OrderBy(evt => evt.ProcessedAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(ct);
        var deliveriesDeleted = await dbContext.WebhookDeliveryLogs
            .Where(log => log.IsDelivered && log.LastAttemptAt.HasValue && log.LastAttemptAt.Value <= olderThan)
            .OrderBy(log => log.LastAttemptAt)
            .Take(batchSize)
            .ExecuteDeleteAsync(ct);
        CmsifyOperationalMetrics.RecordCleanup(outboxDeleted, deliveriesDeleted);
        return new WebhookRetentionCleanupResult(outboxDeleted, deliveriesDeleted);
    }

    private static void ValidateClaimArguments(string workerId, TimeSpan leaseDuration, int limit)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            throw new ArgumentException("Worker ID must be nonblank and at most 200 characters.", nameof(workerId));
        }

        if (leaseDuration < MinimumLeaseDuration || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (limit is < 1 or > MaxClaimBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }

    public async Task<bool> CompleteDeliverySucceededAsync(WebhookDeliveryCompletionDto completion, int statusCode, CancellationToken ct = default)
    {
        var affected = await dbContext.WebhookDeliveryLogs
            .Where(log => log.Id == completion.Id && !log.IsDelivered && !log.IsFailed && log.LeaseOwner == completion.LeaseOwner && log.LeaseToken == completion.LeaseToken && log.LeaseExpiresAt > completion.AttemptedAt)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(log => log.AttemptCount, log => log.AttemptCount + 1)
                .SetProperty(log => log.LastAttemptAt, completion.AttemptedAt)
                .SetProperty(log => log.StatusCode, statusCode)
                .SetProperty(log => log.IsDelivered, true)
                .SetProperty(log => log.IsFailed, false)
                .SetProperty(log => log.IsDeadLetter, false)
                .SetProperty(log => log.DeadLetteredAt, (DateTimeOffset?)null)
                .SetProperty(log => log.NextRetryAt, (DateTimeOffset?)null)
                .SetProperty(log => log.LeaseOwner, (string?)null)
                .SetProperty(log => log.LeaseToken, (Guid?)null)
                .SetProperty(log => log.LeaseExpiresAt, (DateTimeOffset?)null), ct);
        if (affected == 1)
        {
            CmsifyOperationalMetrics.RecordDeliverySucceeded();
        }
        return affected == 1;
    }

    public async Task<bool> CompleteDeliveryFailedAsync(WebhookDeliveryCompletionDto completion, int? statusCode, string? error, DateTimeOffset? nextRetryAt, bool isDeadLetter, CancellationToken ct = default)
    {
        if (isDeadLetter ? nextRetryAt.HasValue : !nextRetryAt.HasValue || nextRetryAt.Value < completion.AttemptedAt)
        {
            throw new ArgumentException("Dead-letter completion requires no retry instant; retry completion requires an instant at or after the attempt.", nameof(nextRetryAt));
        }

        var boundedError = string.IsNullOrWhiteSpace(error) ? null : error[..Math.Min(error.Length, 4_000)];
        var affected = await dbContext.WebhookDeliveryLogs
            .Where(log => log.Id == completion.Id && !log.IsDelivered && !log.IsFailed && log.LeaseOwner == completion.LeaseOwner && log.LeaseToken == completion.LeaseToken && log.LeaseExpiresAt > completion.AttemptedAt)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(log => log.AttemptCount, log => log.AttemptCount + 1)
                .SetProperty(log => log.LastAttemptAt, completion.AttemptedAt)
                .SetProperty(log => log.StatusCode, statusCode)
                .SetProperty(log => log.LastError, boundedError)
                .SetProperty(log => log.IsDelivered, false)
                .SetProperty(log => log.IsFailed, isDeadLetter)
                .SetProperty(log => log.IsDeadLetter, isDeadLetter)
                .SetProperty(log => log.DeadLetteredAt, isDeadLetter ? completion.AttemptedAt : null)
                .SetProperty(log => log.NextRetryAt, isDeadLetter ? null : nextRetryAt)
                .SetProperty(log => log.LeaseOwner, (string?)null)
                .SetProperty(log => log.LeaseToken, (Guid?)null)
                .SetProperty(log => log.LeaseExpiresAt, (DateTimeOffset?)null), ct);
        if (affected == 1)
        {
            if (isDeadLetter)
            {
                CmsifyOperationalMetrics.RecordDeliveryDeadLettered();
            }
            else
            {
                CmsifyOperationalMetrics.RecordDeliveryRetried();
            }
        }
        return affected == 1;
    }
}
