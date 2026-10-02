using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class SqliteWebhookQueries(CmsifyDbContext dbContext) : IWebhookQueries
{
    // Database.BeginTransactionAsync uses Microsoft.Data.Sqlite's non-deferred
    // transaction: reserve the writer before selection and hold it through commit.
    // Competing SQLite writers wait; PostgreSQL instead uses row locks/SKIP LOCKED.
    public Task<List<WebhookDeliveryLog>> LockPendingDeliveriesAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        RequireTransaction();
        return dbContext.WebhookDeliveryLogs
            .Where(log => !log.IsDelivered && !log.IsFailed && log.NextRetryAt <= now &&
                (log.LeaseExpiresAt == null || log.LeaseExpiresAt <= now) &&
                dbContext.WebhookEndpoints.Any(endpoint => endpoint.Id == log.WebhookEndpointId && endpoint.IsActive && !endpoint.IsDeleted))
            .OrderBy(log => log.NextRetryAt).Take(limit).ToListAsync(ct);
    }

    public Task<List<WebhookOutboxEvent>> LockPendingOutboxEventsAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        RequireTransaction();
        return dbContext.WebhookOutboxEvents
            .Where(evt => evt.ProcessedAt == null && (evt.LeaseExpiresAt == null || evt.LeaseExpiresAt <= now))
            .OrderBy(evt => evt.OccurredAt).ThenBy(evt => evt.Id).Take(limit).ToListAsync(ct);
    }

    public Task<WebhookOutboxEvent?> LockOwnedOutboxEventAsync(ClaimedWebhookOutboxEventDto claim, DateTimeOffset now, CancellationToken ct)
    {
        RequireTransaction();
        return dbContext.WebhookOutboxEvents.SingleOrDefaultAsync(evt => evt.Id == claim.Id && evt.ProcessedAt == null &&
            evt.LeaseOwner == claim.LeaseOwner && evt.LeaseToken == claim.LeaseToken && evt.LeaseExpiresAt > now, ct);
    }

    private void RequireTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Webhook selection requires a write transaction.");
    }
}
