using System.Security.Cryptography;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Providers;
using Cmsify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

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
    IOptions<SecretProtectionOptions> options,
    ILogger<WebhookSecretRotationProcessor> logger) : IWebhookSecretRotationProcessor
{
    private const int MaximumBatchSize = 500;
    private readonly SecretProtectionOptions options = options.Value;
    private readonly IWebhookSecretRotationQueries _queries = WebhookSecretRotationQueries.Create(dbContext);

    public async Task<SecretRotationBatchResult> RotateBatchAsync(Guid? afterId, CancellationToken ct = default)
    {
        var batchSize = ValidateBatchSize(options.Rotation.BatchSize);
        var activePrefix = $"v2.{options.ActiveKeyId}.";
        var cursor = afterId ?? Guid.Empty;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var endpoints = await _queries.LockCandidatesAsync(cursor, activePrefix, batchSize, ct);

        var rotated = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var endpoint in endpoints)
        {
            var originalCiphertext = endpoint.Secret;
            try
            {
                var rewrappedCiphertext = secretProtector.Protect(secretProtector.Unprotect(originalCiphertext));
                var updated = await _queries.UpdateSecretAsync(endpoint.Id, originalCiphertext, rewrappedCiphertext, ct);
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
                RecordDecryptFailure(endpoint.Id, WebhookSecretDecryptDiagnostic.FromTypedFailure(originalCiphertext, exception, options.EncryptionKeys.Keys));
                failed++;
            }
            catch (CryptographicException)
            {
                RecordDecryptFailure(endpoint.Id, WebhookSecretDecryptDiagnostic.Create(originalCiphertext, "authentication", options.EncryptionKeys.Keys));
                failed++;
            }
            catch (ArgumentException)
            {
                RecordDecryptFailure(endpoint.Id, WebhookSecretDecryptDiagnostic.Create(originalCiphertext, "malformed_ciphertext", options.EncryptionKeys.Keys));
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
        return await _queries.CountRemainingAsync(activePrefix, options.ActiveKeyId, configuredKeyIds, ct);
    }

    private static int ValidateBatchSize(int batchSize) => batchSize is >= 1 and <= MaximumBatchSize
        ? batchSize
        : throw new ArgumentOutOfRangeException(nameof(batchSize));

    private void RecordDecryptFailure(Guid endpointId, WebhookSecretDecryptDiagnostic diagnostic)
    {
        CmsifyOperationalMetrics.RecordSecretDecryptFailure(diagnostic.Version, diagnostic.KeyId, diagnostic.Reason, options.EncryptionKeys.Keys);
        logger.LogWarning("Webhook secret rotation could not decrypt endpoint {EndpointId}; version {Version}, key ID {KeyId}, reason {Reason}.", endpointId, diagnostic.Version, diagnostic.KeyId, diagnostic.Reason);
    }
}
