using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;

namespace Cmsify.Infrastructure.Persistence.Providers;

// Infrastructure-only selection and locking; the repository owns the transaction and mutation.
internal interface IWebhookQueries
{
    Task<List<WebhookDeliveryLog>> LockPendingDeliveriesAsync(DateTimeOffset now, int limit, CancellationToken ct);
    Task<List<WebhookOutboxEvent>> LockPendingOutboxEventsAsync(DateTimeOffset now, int limit, CancellationToken ct);
    Task<WebhookOutboxEvent?> LockOwnedOutboxEventAsync(ClaimedWebhookOutboxEventDto claim, DateTimeOffset now, CancellationToken ct);
}

internal static class WebhookQueries
{
    public static IWebhookQueries Create(CmsifyDbContext context) => context.Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => new PostgresWebhookQueries(context),
        "Microsoft.EntityFrameworkCore.Sqlite" => new SqliteWebhookQueries(context),
        _ => throw new NotSupportedException($"Unsupported webhook provider: {context.Database.ProviderName}")
    };
}
