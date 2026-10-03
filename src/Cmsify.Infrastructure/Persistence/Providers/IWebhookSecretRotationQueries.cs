using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.BackgroundServices;

namespace Cmsify.Infrastructure.Persistence.Providers;

// Infrastructure-only SQL; the processor owns the transaction, rewrap and counters.
internal interface IWebhookSecretRotationQueries
{
    Task<List<WebhookEndpoint>> LockCandidatesAsync(Guid cursor, string activePrefix, int batchSize, CancellationToken ct);
    Task<int> UpdateSecretAsync(Guid id, string originalCiphertext, string rewrappedCiphertext, CancellationToken ct);
    Task<IReadOnlyList<SecretCiphertextCount>> CountRemainingAsync(string activePrefix, string activeKeyId, string[] configuredKeyIds, CancellationToken ct);
}

internal static class WebhookSecretRotationQueries
{
    public static IWebhookSecretRotationQueries Create(CmsifyDbContext context) => context.Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => new PostgresWebhookSecretRotationQueries(context),
        "Microsoft.EntityFrameworkCore.Sqlite" => new SqliteWebhookSecretRotationQueries(context),
        _ => throw new NotSupportedException($"Unsupported webhook provider: {context.Database.ProviderName}")
    };
}
