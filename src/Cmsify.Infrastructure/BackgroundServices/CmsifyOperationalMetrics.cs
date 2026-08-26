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

    static CmsifyOperationalMetrics()
    {
        Meter.CreateObservableGauge("cmsify.webhook.outbox.pending", () => Volatile.Read(ref pendingOutbox));
        Meter.CreateObservableGauge("cmsify.webhook.delivery.due", () => Volatile.Read(ref dueDeliveries));
        Meter.CreateObservableGauge("cmsify.schedule.due", () => Volatile.Read(ref dueScheduled));
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
    public static void ReportSecretRotationRemaining(IEnumerable<SecretCiphertextCount> counts, IEnumerable<string> configuredKeyIds)
    {
        var configured = configuredKeyIds.ToHashSet(StringComparer.Ordinal);
        var normalized = counts.Select(count => new SecretCiphertextCount(
                NormalizeSecretVersion(count.Version),
                NormalizeSecretKeyId(count.KeyId, configured),
                Math.Max(0, count.Count)))
            .ToImmutableArray();
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

    private sealed record SecretRotationRemainingSnapshot(ImmutableArray<SecretCiphertextCount> Counts)
    {
        public static SecretRotationRemainingSnapshot Empty { get; } = new([]);
    }
}
