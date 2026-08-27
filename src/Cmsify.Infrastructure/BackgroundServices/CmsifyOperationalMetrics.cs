using System.Diagnostics.Metrics;
using System.Collections.Immutable;

namespace Cmsify.Infrastructure.BackgroundServices;

public static class CmsifyOperationalMetrics
{
    public const string MeterName = "Cmsify.Operational";
    private static readonly Meter Meter = new(MeterName, "1.0");
    private static long pendingOutbox;
    private static long dueDeliveries;
    private static long dueScheduled;
    private static long pendingMediaDeletion;
    private static SecretRotationRemainingSnapshot secretRotationRemaining = SecretRotationRemainingSnapshot.Empty;

    private static readonly Counter<long> OutboxClaimed = Meter.CreateCounter<long>("cmsify.webhook.outbox.claimed");
    private static readonly Counter<long> OutboxReclaimed = Meter.CreateCounter<long>("cmsify.webhook.outbox.reclaimed");
    private static readonly Counter<long> OutboxMaterialized = Meter.CreateCounter<long>("cmsify.webhook.outbox.materialized");
    private static readonly Counter<long> OutboxFailures = Meter.CreateCounter<long>("cmsify.webhook.outbox.failures");
    private static readonly Counter<long> DeliveryClaimed = Meter.CreateCounter<long>("cmsify.webhook.delivery.claimed");
    private static readonly Counter<long> DeliveryReclaimed = Meter.CreateCounter<long>("cmsify.webhook.delivery.reclaimed");
    private static readonly Counter<long> DeliverySucceeded = Meter.CreateCounter<long>("cmsify.webhook.delivery.succeeded");
    private static readonly Counter<long> DeliveryRetried = Meter.CreateCounter<long>("cmsify.webhook.delivery.retried");
    private static readonly Counter<long> DeliveryDeadLettered = Meter.CreateCounter<long>("cmsify.webhook.delivery.dead_lettered");
    private static readonly Counter<long> DestinationRejected = Meter.CreateCounter<long>("cmsify.webhook.destination.rejected");
    private static readonly Counter<long> PinnedConnectionFailed = Meter.CreateCounter<long>("cmsify.webhook.connection.failed");
    private static readonly Counter<long> ScheduledClaimed = Meter.CreateCounter<long>("cmsify.schedule.claimed");
    private static readonly Counter<long> ScheduledReclaimed = Meter.CreateCounter<long>("cmsify.schedule.reclaimed");
    private static readonly Counter<long> ScheduledPublished = Meter.CreateCounter<long>("cmsify.schedule.published");
    private static readonly Counter<long> ScheduledFailures = Meter.CreateCounter<long>("cmsify.schedule.failures");
    private static readonly Counter<long> CleanupOutbox = Meter.CreateCounter<long>("cmsify.cleanup.outbox_deleted");
    private static readonly Counter<long> CleanupDeliveries = Meter.CreateCounter<long>("cmsify.cleanup.deliveries_deleted");
    private static readonly Counter<long> SecretDecryptFailures = Meter.CreateCounter<long>("cmsify.webhook.secret.decrypt_failures");
    private static readonly Counter<long> SecretRotationRows = Meter.CreateCounter<long>("cmsify.webhook.secret.rotation.rows");
    private static readonly Counter<long> SecretRotationCycles = Meter.CreateCounter<long>("cmsify.webhook.secret.rotation.cycles");
    private static readonly Histogram<double> SecretRotationDuration = Meter.CreateHistogram<double>("cmsify.webhook.secret.rotation.duration", unit: "s");
    private static readonly Counter<long> MediaCycleFailures = Meter.CreateCounter<long>("cmsify.media.reconciliation.cycle_failures");
    private static readonly Counter<long> MediaDeletionClaims = Meter.CreateCounter<long>("cmsify.media.deletion.claimed");
    private static readonly Counter<long> MediaDeletionReclaims = Meter.CreateCounter<long>("cmsify.media.deletion.reclaimed");
    private static readonly Counter<long> MediaDeletionOutcomes = Meter.CreateCounter<long>("cmsify.media.deletion.outcome");
    private static readonly Counter<long> MediaDeletionRetries = Meter.CreateCounter<long>("cmsify.media.deletion.retried");
    private static readonly Counter<long> MediaStaleUploads = Meter.CreateCounter<long>("cmsify.media.upload.stale");
    private static readonly Counter<long> MediaMissingBlobs = Meter.CreateCounter<long>("cmsify.media.blob.missing");
    private static readonly Counter<long> MediaScans = Meter.CreateCounter<long>("cmsify.media.scan");
    private static readonly Counter<long> MediaOrphans = Meter.CreateCounter<long>("cmsify.media.orphan.discovered");

    static CmsifyOperationalMetrics()
    {
        Meter.CreateObservableGauge("cmsify.webhook.outbox.pending", () => Volatile.Read(ref pendingOutbox));
        Meter.CreateObservableGauge("cmsify.webhook.delivery.due", () => Volatile.Read(ref dueDeliveries));
        Meter.CreateObservableGauge("cmsify.schedule.due", () => Volatile.Read(ref dueScheduled));
        Meter.CreateObservableGauge("cmsify.media.deletion.pending", () => Volatile.Read(ref pendingMediaDeletion));
        Meter.CreateObservableGauge("cmsify.webhook.secret.rotation.remaining", ObserveSecretRotationRemaining);
    }

    public static void ReportOutboxDepth(long value) => Interlocked.Exchange(ref pendingOutbox, value);
    public static void ReportDueDeliveryDepth(long value) => Interlocked.Exchange(ref dueDeliveries, value);
    public static void ReportDueScheduledDepth(long value) => Interlocked.Exchange(ref dueScheduled, value);
    public static void RecordOutboxClaim(bool reclaimed) { OutboxClaimed.Add(1); if (reclaimed) OutboxReclaimed.Add(1); }
    public static void RecordOutboxMaterialized() => OutboxMaterialized.Add(1);
    public static void RecordOutboxFailure() => OutboxFailures.Add(1);
    public static void RecordDeliveryClaim(bool reclaimed) { DeliveryClaimed.Add(1); if (reclaimed) DeliveryReclaimed.Add(1); }
    public static void RecordDeliverySucceeded() => DeliverySucceeded.Add(1);
    public static void RecordDeliveryRetried() => DeliveryRetried.Add(1);
    public static void RecordDeliveryDeadLettered() => DeliveryDeadLettered.Add(1);
    public static void RecordDestinationRejection(string reason) => DestinationRejected.Add(1, new KeyValuePair<string, object?>("reason", NormalizeDestinationRejectionReason(reason)));
    public static void RecordPinnedConnectionFailure(string reason) => PinnedConnectionFailed.Add(1, new KeyValuePair<string, object?>("reason", NormalizePinnedConnectionFailureReason(reason)));
    public static void RecordScheduledClaim(bool reclaimed) { ScheduledClaimed.Add(1); if (reclaimed) ScheduledReclaimed.Add(1); }
    public static void RecordScheduledPublished() => ScheduledPublished.Add(1);
    public static void RecordScheduledFailure() => ScheduledFailures.Add(1);
    public static void RecordCleanup(int outbox, int deliveries) { if (outbox > 0) CleanupOutbox.Add(outbox); if (deliveries > 0) CleanupDeliveries.Add(deliveries); }
    public static void RecordSecretDecryptFailure(string version, string keyId, string reason, IEnumerable<string> configuredKeyIds) =>
        SecretDecryptFailures.Add(1,
            new KeyValuePair<string, object?>("version", NormalizeSecretVersion(version)),
            new KeyValuePair<string, object?>("key_id", NormalizeSecretKeyId(keyId, configuredKeyIds)),
            new KeyValuePair<string, object?>("reason", NormalizeSecretDecryptReason(reason)));
    public static void RecordSecretRotationRows(SecretRotationBatchResult result)
    {
        if (result.Rotated > 0) SecretRotationRows.Add(result.Rotated, new KeyValuePair<string, object?>("outcome", "rotated"));
        if (result.Skipped > 0) SecretRotationRows.Add(result.Skipped, new KeyValuePair<string, object?>("outcome", "skipped"));
        if (result.Failed > 0) SecretRotationRows.Add(result.Failed, new KeyValuePair<string, object?>("outcome", "failed"));
    }
    public static void RecordSecretRotationCycle(string outcome) => SecretRotationCycles.Add(1, new KeyValuePair<string, object?>("outcome", NormalizeSecretRotationCycleOutcome(outcome)));
    public static void RecordSecretRotationDuration(TimeSpan duration) => SecretRotationDuration.Record(duration.TotalSeconds);
    public static void RecordMediaCycleFailure() => MediaCycleFailures.Add(1);
    public static void ReportMediaPendingDeletion(long value) => Interlocked.Exchange(ref pendingMediaDeletion, Math.Max(0, value));
    public static void RecordMediaDeletionClaim(string provider, bool reclaimed)
    {
        var tag = new KeyValuePair<string, object?>("provider", NormalizeMediaProvider(provider));
        MediaDeletionClaims.Add(1, tag);
        if (reclaimed) MediaDeletionReclaims.Add(1, tag);
    }
    public static void RecordMediaDeletion(string provider, string reason, string outcome) => MediaDeletionOutcomes.Add(1,
        new KeyValuePair<string, object?>("provider", NormalizeMediaProvider(provider)),
        new KeyValuePair<string, object?>("reason", NormalizeMediaReason(reason)),
        new KeyValuePair<string, object?>("outcome", NormalizeMediaOutcome(outcome)));
    public static void RecordMediaRetry(string provider, string reason) => MediaDeletionRetries.Add(1,
        new KeyValuePair<string, object?>("provider", NormalizeMediaProvider(provider)),
        new KeyValuePair<string, object?>("reason", NormalizeMediaReason(reason)));
    public static void RecordMediaStaleUpload(string provider, long count) => MediaStaleUploads.Add(count,
        new KeyValuePair<string, object?>("provider", NormalizeMediaProvider(provider)));
    public static void RecordMediaMissing(string provider) => MediaMissingBlobs.Add(1,
        new KeyValuePair<string, object?>("provider", NormalizeMediaProvider(provider)));
    public static void RecordMediaScan(string provider, string outcome) => MediaScans.Add(1,
        new KeyValuePair<string, object?>("provider", NormalizeMediaProvider(provider)),
        new KeyValuePair<string, object?>("outcome", NormalizeMediaOutcome(outcome)));
    public static void RecordMediaOrphan(string provider) => MediaOrphans.Add(1,
        new KeyValuePair<string, object?>("provider", NormalizeMediaProvider(provider)));
    public static void ReportSecretRotationRemaining(IEnumerable<SecretCiphertextCount> counts, IEnumerable<string> configuredKeyIds)
    {
        var configured = configuredKeyIds.Distinct(StringComparer.Ordinal).ToArray();
        var normalized = new Dictionary<(string Version, string KeyId), long>
        {
            [("v1", "legacy")] = 0,
            [("v2", "unknown")] = 0,
            [("unknown", "unknown")] = 0
        };
        foreach (var keyId in configured)
        {
            normalized[("v2", keyId)] = 0;
        }

        foreach (var count in counts)
        {
            var version = NormalizeSecretVersion(count.Version);
            var keyId = version switch
            {
                "v1" => "legacy",
                "v2" => NormalizeSecretKeyId(count.KeyId, configured),
                _ => "unknown"
            };
            var category = (version, keyId);
            normalized[category] = normalized.GetValueOrDefault(category) + Math.Max(0, count.Count);
        }

        Volatile.Write(ref secretRotationRemaining, new SecretRotationRemainingSnapshot(normalized));
    }

    private static string NormalizeDestinationRejectionReason(string reason) => reason switch
    {
        "url_policy" => "url_policy",
        "resolution" => "resolution",
        "address_policy" => "address_policy",
        _ => "unknown"
    };

    private static string NormalizePinnedConnectionFailureReason(string reason) => reason switch
    {
        "connection" => "connection",
        _ => "unknown"
    };

    private static IEnumerable<Measurement<long>> ObserveSecretRotationRemaining()
    {
        var snapshot = Volatile.Read(ref secretRotationRemaining);
        return snapshot.Counts.Select(count => new Measurement<long>(count.Count,
            new KeyValuePair<string, object?>("version", count.Version),
            new KeyValuePair<string, object?>("key_id", count.KeyId)));
    }

    private static string NormalizeSecretVersion(string version) => version switch
    {
        "v1" => "v1",
        "v2" => "v2",
        _ => "unknown"
    };

    private static string NormalizeSecretKeyId(string keyId, IEnumerable<string> configuredKeyIds) =>
        configuredKeyIds.Contains(keyId, StringComparer.Ordinal) ? keyId : "unknown";

    private static string NormalizeSecretDecryptReason(string reason) => reason switch
    {
        "configuration" => "configuration",
        "unknown_version" => "unknown_version",
        "unknown_key" => "unknown_key",
        "malformed_ciphertext" => "malformed_ciphertext",
        "authentication" => "authentication",
        _ => "unknown"
    };

    private static string NormalizeSecretRotationCycleOutcome(string outcome) => outcome switch
    {
        "succeeded" => "succeeded",
        "failed" => "failed",
        _ => "failed"
    };

    private static string NormalizeMediaProvider(string provider) => provider.ToLowerInvariant() switch
    {
        "local" => "local",
        "s3" => "s3",
        _ => "unknown"
    };

    private static string NormalizeMediaReason(string reason) => reason switch
    {
        "user_delete" => "user_delete",
        "abandoned_upload" => "abandoned_upload",
        "orphan" => "orphan",
        "migration_deleted" => "migration_deleted",
        "provider_mismatch" => "provider_mismatch",
        "upload_failed" => "upload_failed",
        _ => "unknown"
    };

    private static string NormalizeMediaOutcome(string outcome) => outcome switch
    {
        "succeeded" => "succeeded",
        "skipped" => "skipped",
        _ => "failed"
    };

    private sealed record SecretRotationRemainingSnapshot(ImmutableArray<SecretCiphertextCount> Counts)
    {
        public static SecretRotationRemainingSnapshot Empty { get; } = new(ImmutableArray<SecretCiphertextCount>.Empty);

        public SecretRotationRemainingSnapshot(Dictionary<(string Version, string KeyId), long> counts)
            : this(counts.Select(count => new SecretCiphertextCount(count.Key.Version, count.Key.KeyId, count.Value)).ToImmutableArray())
        {
        }
    }
}
