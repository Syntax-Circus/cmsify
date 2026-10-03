using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class SqliteWebhookSecretRotationQueries(CmsifyDbContext dbContext) : IWebhookSecretRotationQueries
{
    public async Task<List<WebhookEndpoint>> LockCandidatesAsync(Guid cursor, string activePrefix, int batchSize, CancellationToken ct)
    {
        RequireTransaction();
        // BeginTransactionAsync reserves SQLite's writer before selection. The
        // reservation holds through this overflow guard, conditional updates and commit.
        var candidates = dbContext.WebhookEndpoints.FromSqlInterpolated($"""
            SELECT * FROM webhook_endpoints
            WHERE id > {cursor}
              AND substr(secret, 1, length({activePrefix})) <> {activePrefix}
            ORDER BY id
            LIMIT {batchSize}
            """).IgnoreQueryFilters().AsNoTracking();
        if (await candidates.AnyAsync(endpoint => EF.Property<uint>(endpoint, "xmin") == uint.MaxValue, ct))
            throw new OverflowException("A webhook endpoint concurrency token is exhausted.");
        return await candidates.ToListAsync(ct);
    }

    public Task<int> UpdateSecretAsync(Guid id, string originalCiphertext, string rewrappedCiphertext, CancellationToken ct)
    {
        RequireTransaction();
        // Raw SQL bypasses PrepareChanges. Advance the mapped token atomically
        // with the secret and use the provider's UTC-tick timestamp representation.
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE webhook_endpoints
            SET secret = {rewrappedCiphertext}, updated_at = {DateTimeOffset.UtcNow.UtcTicks}, row_version = row_version + 1
            WHERE id = {id} AND secret = {originalCiphertext}
            """, ct);
    }

    public async Task<IReadOnlyList<SecretCiphertextCount>> CountRemainingAsync(string activePrefix, string activeKeyId, string[] configuredKeyIds, CancellationToken ct)
    {
        // json_each supplies parameterized configured labels; only grouped counts
        // cross the boundary. Exact substr comparisons preserve PostgreSQL's case
        // sensitivity (SQLite LIKE is case insensitive by default).
        var configuredKeys = JsonSerializer.Serialize(configuredKeyIds);
        return await dbContext.Database.SqlQuery<SecretCiphertextCount>($"""
            SELECT
                CASE
                    WHEN substr(secret, 1, 3) = 'v1.' THEN 'v1'
                    WHEN substr(secret, 1, 3) = 'v2.' THEN 'v2'
                    ELSE 'unknown'
                END AS version,
                CASE
                    WHEN substr(secret, 1, 3) = 'v1.' THEN 'legacy'
                    WHEN substr(secret, 1, 3) = 'v2.'
                         AND key_label IN (SELECT value FROM json_each({configuredKeys}))
                         AND key_label <> {activeKeyId}
                        THEN key_label
                    ELSE 'unknown'
                END AS key_id,
                COUNT(*) AS count
            FROM (
                SELECT secret,
                    CASE WHEN instr(substr(secret, 4), '.') = 0 THEN substr(secret, 4)
                         ELSE substr(secret, 4, instr(substr(secret, 4), '.') - 1)
                    END AS key_label
                FROM webhook_endpoints
                WHERE substr(secret, 1, length({activePrefix})) <> {activePrefix}
            )
            GROUP BY 1, 2
            ORDER BY 1, 2
            """).ToListAsync(ct);
    }

    private void RequireTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Webhook secret rotation requires a write transaction.");
    }
}
