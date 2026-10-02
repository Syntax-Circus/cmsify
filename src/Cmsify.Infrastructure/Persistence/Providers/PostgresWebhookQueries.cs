using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class PostgresWebhookQueries(CmsifyDbContext dbContext) : IWebhookQueries
{
    public Task<List<WebhookDeliveryLog>> LockPendingDeliveriesAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        dbContext.WebhookDeliveryLogs.FromSqlInterpolated($"""
            SELECT * FROM webhook_delivery_logs
            WHERE NOT is_delivered AND NOT is_failed AND next_retry_at <= {now} AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
              AND EXISTS (SELECT 1 FROM webhook_endpoints endpoint WHERE endpoint.id = webhook_delivery_logs.webhook_endpoint_id AND endpoint.is_active AND NOT endpoint.is_deleted)
            ORDER BY next_retry_at
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);

    public Task<List<WebhookOutboxEvent>> LockPendingOutboxEventsAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        dbContext.WebhookOutboxEvents.FromSqlInterpolated($"""
            SELECT * FROM webhook_outbox_events
            WHERE processed_at IS NULL AND (lease_expires_at IS NULL OR lease_expires_at <= {now})
            ORDER BY occurred_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT {limit}
            """).ToListAsync(ct);

    public Task<WebhookOutboxEvent?> LockOwnedOutboxEventAsync(ClaimedWebhookOutboxEventDto claim, DateTimeOffset now, CancellationToken ct) =>
        dbContext.WebhookOutboxEvents.FromSqlInterpolated($"""
            SELECT * FROM webhook_outbox_events
            WHERE id = {claim.Id} AND processed_at IS NULL
              AND lease_owner = {claim.LeaseOwner} AND lease_token = {claim.LeaseToken}
              AND lease_expires_at > {now}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
}
