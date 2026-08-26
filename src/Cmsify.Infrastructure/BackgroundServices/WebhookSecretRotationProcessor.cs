using System.Security.Cryptography;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed record SecretRotationBatchResult(
    Guid? NextCursor,
    int Selected,
    int Rotated,
    int Skipped,
    int Failed,
    bool ReachedEnd);

public sealed record SecretCiphertextCount(string Version, string KeyId, long Count);

public interface IWebhookSecretRotationProcessor
{
    Task<SecretRotationBatchResult> RotateBatchAsync(Guid? afterId, CancellationToken ct = default);

    Task<IReadOnlyList<SecretCiphertextCount>> CountRemainingAsync(CancellationToken ct = default);
}

public sealed class WebhookSecretRotationProcessor(
    CmsifyDbContext dbContext,
    ISecretProtector secretProtector,
    IOptions<SecretProtectionOptions> options) : IWebhookSecretRotationProcessor
{
    private const int MaximumBatchSize = 500;
    private readonly SecretProtectionOptions options = options.Value;

    public async Task<SecretRotationBatchResult> RotateBatchAsync(Guid? afterId, CancellationToken ct = default)
    {
        var batchSize = ValidateBatchSize(options.Rotation.BatchSize);
        var activePrefix = $"v2.{options.ActiveKeyId}.";
        var cursor = afterId ?? Guid.Empty;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var endpoints = await dbContext.WebhookEndpoints
            .FromSqlInterpolated($"""
                SELECT *, xmin FROM webhook_endpoints
                WHERE id > {cursor}
                  AND LEFT(secret, length({activePrefix})) <> {activePrefix}
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT {batchSize}
                """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        var rotated = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var endpoint in endpoints)
        {
            var originalCiphertext = endpoint.Secret;
            try
            {
                var rewrappedCiphertext = secretProtector.Protect(secretProtector.Unprotect(originalCiphertext));
                var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE webhook_endpoints
                    SET secret = {rewrappedCiphertext}, updated_at = CURRENT_TIMESTAMP
                    WHERE id = {endpoint.Id} AND secret = {originalCiphertext}
                    """, ct);
                if (updated == 1)
                {
                    rotated++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (SecretDecryptFailureException exception)
            {
                RecordDecryptFailure(originalCiphertext, ToMetricReason(exception.Reason));
                failed++;
            }
            catch (CryptographicException)
            {
                RecordDecryptFailure(originalCiphertext, "authentication");
                failed++;
            }
            catch (ArgumentException)
            {
                RecordDecryptFailure(originalCiphertext, "malformed_ciphertext");
                failed++;
            }
        }

        await transaction.CommitAsync(ct);
        var nextCursor = endpoints.Count == 0 ? afterId : endpoints[^1].Id;
        return new SecretRotationBatchResult(nextCursor, endpoints.Count, rotated, skipped, failed, endpoints.Count < batchSize);
    }

    public async Task<IReadOnlyList<SecretCiphertextCount>> CountRemainingAsync(CancellationToken ct = default)
    {
        var activePrefix = $"v2.{options.ActiveKeyId}.";
        var configuredKeyIds = options.EncryptionKeys.Keys.ToArray();
        return await dbContext.Database.SqlQuery<SecretCiphertextCount>($"""
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
                         AND split_part(secret, '.', 2) <> {options.ActiveKeyId}
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

    private static int ValidateBatchSize(int batchSize) => batchSize is >= 1 and <= MaximumBatchSize
        ? batchSize
        : throw new ArgumentOutOfRangeException(nameof(batchSize));

    private void RecordDecryptFailure(string ciphertext, string reason)
    {
        var segments = ciphertext.Split('.', StringSplitOptions.None);
        var version = segments.Length > 0 ? segments[0] : "unknown";
        var keyId = segments.Length > 1 && string.Equals(version, "v2", StringComparison.Ordinal) ? segments[1] : "unknown";
        CmsifyOperationalMetrics.RecordSecretDecryptFailure(version, keyId, reason, options.EncryptionKeys.Keys);
    }

    private static string ToMetricReason(SecretDecryptFailureReason reason) => reason switch
    {
        SecretDecryptFailureReason.UnknownVersion => "unknown_version",
        SecretDecryptFailureReason.UnknownKey => "unknown_key",
        SecretDecryptFailureReason.Configuration => "configuration",
        SecretDecryptFailureReason.MalformedCiphertext => "malformed_ciphertext",
        SecretDecryptFailureReason.Authentication => "authentication",
        _ => "unknown"
    };
}
