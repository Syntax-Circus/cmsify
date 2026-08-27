using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class MediaReconciliationProcessor(
    IMediaReconciliationRepository repository,
    IStorageProvider storage,
    IOptions<MediaOperationalOptions> options,
    string providerName,
    ILogger<MediaReconciliationProcessor> logger)
{
    private readonly MediaOperationalOptions settings = options.Value;

    public async Task RunCycleAsync(string workerId, DateTimeOffset now, CancellationToken ct = default)
    {
        var staleUploads = await repository.FailStaleUploadsAsync(
            now.AddMinutes(-settings.AbandonedUploadMinutes), now, settings.BatchSize, ct);
        if (staleUploads > 0) CmsifyOperationalMetrics.RecordMediaStaleUpload(providerName, staleUploads);

        var claims = await repository.ClaimDeletionIntentsAsync(
            workerId, now, TimeSpan.FromSeconds(settings.LeaseDurationSeconds), settings.BatchSize, ct);
        foreach (var claim in claims)
        {
            CmsifyOperationalMetrics.RecordMediaDeletionClaim(claim.Provider, claim.WasReclaimed);
            if (!string.Equals(claim.Provider, providerName, StringComparison.OrdinalIgnoreCase))
            {
                await RetryAsync(claim, now, "provider_mismatch", ct);
                continue;
            }

            try
            {
                await storage.DeleteAsync(claim.StorageKey, ct);
                await repository.CompleteDeletionAsync(claim, now, ct);
                CmsifyOperationalMetrics.RecordMediaDeletion(claim.Provider, claim.Reason, "succeeded");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Media deletion attempt failed for provider {Provider}.", NormalizeProvider(claim.Provider));
                await RetryAsync(claim, now, exception.GetType().Name, ct);
            }
        }

        var candidates = await repository.GetVerificationBatchAsync(settings.BatchSize, ct);
        foreach (var candidate in candidates.Where(candidate =>
                     string.Equals(candidate.Provider, providerName, StringComparison.OrdinalIgnoreCase)))
        {
            var metadata = await storage.GetMetadataAsync(candidate.StorageKey, ct);
            if (metadata is null)
            {
                await repository.RecordBlobMissingAsync(candidate.Id, now, ct);
                CmsifyOperationalMetrics.RecordMediaMissing(candidate.Provider);
            }
            else
            {
                await repository.RecordBlobPresentAsync(candidate.Id, now, ct);
            }
        }

        foreach (var prefix in settings.ManagedPrefixes)
        {
            await ScanPrefixAsync(prefix, workerId, now, ct);
        }
    }

    private async Task ScanPrefixAsync(string prefix, string workerId, DateTimeOffset now, CancellationToken ct)
    {
        var checkpoint = await repository.ClaimCheckpointAsync(
            providerName, prefix, workerId, now, TimeSpan.FromSeconds(settings.LeaseDurationSeconds), ct);
        if (checkpoint is null) return;

        var page = await storage.ListAsync(new ListStorageObjectsRequest(prefix, checkpoint.AfterKey, settings.BatchSize), ct);
        var graceBoundary = now.AddHours(-settings.OrphanGraceHours);
        foreach (var item in page.Items)
        {
            if (item.LastModified <= graceBoundary &&
                !await repository.StorageKeyExistsAsync(providerName, item.Key, ct))
            {
                await repository.EnqueueOrphanDeletionAsync(providerName, item.Key, now, ct);
                CmsifyOperationalMetrics.RecordMediaOrphan(providerName);
            }
        }

        await repository.CompleteCheckpointAsync(
            checkpoint, page.NextAfterKey, page.NextAfterKey is null, now, ct);
        CmsifyOperationalMetrics.RecordMediaScan(providerName, "succeeded");
    }

    private Task RetryAsync(MediaDeletionClaim claim, DateTimeOffset now, string reason, CancellationToken ct)
    {
        var delay = MediaRetryBackoff.Calculate(
            claim.AttemptCount + 1,
            TimeSpan.FromSeconds(settings.RetryBaseSeconds),
            TimeSpan.FromSeconds(settings.RetryCapSeconds));
        CmsifyOperationalMetrics.RecordMediaRetry(claim.Provider, reason);
        return repository.RetryDeletionAsync(claim, now, now.Add(delay), reason, ct);
    }

    private static string NormalizeProvider(string provider) => provider.ToLowerInvariant() switch
    {
        "local" => "local",
        "s3" => "s3",
        _ => "unknown"
    };
}
