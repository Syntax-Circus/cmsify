using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class PostgresWebhookSecretRotationQueries(CmsifyDbContext dbContext) : IWebhookSecretRotationQueries
{
    public Task<List<WebhookEndpoint>> LockCandidatesAsync(Guid cursor, string activePrefix, int batchSize, CancellationToken ct) =>
        dbContext.WebhookEndpoints.FromSqlInterpolated($"""
            SELECT *, xmin FROM webhook_endpoints
            WHERE id > {cursor}
              AND LEFT(secret, length({activePrefix})) <> {activePrefix}
            ORDER BY id
            FOR UPDATE SKIP LOCKED
            LIMIT {batchSize}
            """).IgnoreQueryFilters().ToListAsync(ct);

    public Task<int> UpdateSecretAsync(Guid id, string originalCiphertext, string rewrappedCiphertext, CancellationToken ct) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE webhook_endpoints
            SET secret = {rewrappedCiphertext}, updated_at = CURRENT_TIMESTAMP
            WHERE id = {id} AND secret = {originalCiphertext}
            """, ct);

    public async Task<IReadOnlyList<SecretCiphertextCount>> CountRemainingAsync(string activePrefix, string activeKeyId, string[] configuredKeyIds, CancellationToken ct) =>
        await dbContext.Database.SqlQuery<SecretCiphertextCount>($"""
            SELECT
                CASE
                    WHEN secret LIKE 'v1.%' THEN 'v1'
                    WHEN secret LIKE 'v2.%' THEN 'v2'
                    ELSE 'unknown'
                END AS version,
                CASE
                    WHEN secret LIKE 'v1.%' THEN 'legacy'
                    WHEN secret LIKE 'v2.%'
                         AND split_part(secret, '.', 2) = ANY({configuredKeyIds})
                         AND split_part(secret, '.', 2) <> {activeKeyId}
                        THEN split_part(secret, '.', 2)
                    ELSE 'unknown'
                END AS key_id,
                COUNT(*) AS count
            FROM webhook_endpoints
            WHERE LEFT(secret, length({activePrefix})) <> {activePrefix}
            GROUP BY 1, 2
            ORDER BY 1, 2
            """).ToListAsync(ct);
}
