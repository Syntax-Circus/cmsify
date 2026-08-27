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
    ILogger<MediaReconciliationProcessor> logger,
    TimeProvider? timeProvider = null)
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
                await RetryAsync(claim, CurrentTime(now), "provider_mismatch", ct);
                continue;
            }

            try
            {
                var preparation = await repository.PrepareDeletionAsync(
                    claim,
                    CurrentTime(now),
                    TimeSpan.FromSeconds(settings.LeaseDurationSeconds),
                    ct);
                if (preparation != DeletionPreparationResult.Ready)
                {
                    CmsifyOperationalMetrics.RecordMediaDeletion(claim.Provider, claim.Reason, "skipped");
                    continue;
                }

                await storage.DeleteAsync(claim.StorageKey, ct);
                var completed = await repository.CompleteDeletionAsync(claim, CurrentTime(now), ct);
                CmsifyOperationalMetrics.RecordMediaDeletion(
                    claim.Provider,
                    claim.Reason,
                    completed ? "succeeded" : "skipped");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Media deletion attempt failed for provider {Provider} and reason {Reason}.",
                    NormalizeProvider(claim.Provider),
                    NormalizeFailure(exception));
                await RetryAsync(claim, CurrentTime(now), NormalizeFailure(exception), ct);
            }
        }

        var candidates = await repository.GetVerificationBatchAsync(providerName, settings.BatchSize, ct);
        foreach (var candidate in candidates)
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
        var claimTime = CurrentTime(now);
        var checkpoint = await repository.ClaimCheckpointAsync(
            providerName, prefix, workerId, claimTime, TimeSpan.FromSeconds(settings.LeaseDurationSeconds), ct);
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

        var completed = await repository.CompleteCheckpointAsync(
            checkpoint, page.NextAfterKey, page.NextAfterKey is null, CurrentTime(now), ct);
        CmsifyOperationalMetrics.RecordMediaScan(providerName, completed ? "succeeded" : "skipped");
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

    private DateTimeOffset CurrentTime(DateTimeOffset cycleTime) => timeProvider?.GetUtcNow() ?? cycleTime;

    private static string NormalizeFailure(Exception exception) => exception switch
    {
        TimeoutException => "timeout",
        IOException => "io",
        UnauthorizedAccessException => "authorization",
        _ => "unknown"
    };
}
