using System.Diagnostics.Metrics;

namespace Cmsify.Infrastructure.BackgroundServices;

public static class CmsifyOperationalMetrics
{
    public const string MeterName = "Cmsify.Operational";
    private static readonly Meter Meter = new(MeterName, "1.0");
    private static long pendingOutbox;
    private static long dueDeliveries;
    private static long dueScheduled;

    private static readonly Counter<long> OutboxClaimed = Meter.CreateCounter<long>("cmsify.webhook.outbox.claimed");
    private static readonly Counter<long> OutboxReclaimed = Meter.CreateCounter<long>("cmsify.webhook.outbox.reclaimed");
    private static readonly Counter<long> OutboxMaterialized = Meter.CreateCounter<long>("cmsify.webhook.outbox.materialized");
    private static readonly Counter<long> OutboxFailures = Meter.CreateCounter<long>("cmsify.webhook.outbox.failures");
    private static readonly Counter<long> DeliveryClaimed = Meter.CreateCounter<long>("cmsify.webhook.delivery.claimed");
    private static readonly Counter<long> DeliveryReclaimed = Meter.CreateCounter<long>("cmsify.webhook.delivery.reclaimed");
    private static readonly Counter<long> DeliverySucceeded = Meter.CreateCounter<long>("cmsify.webhook.delivery.succeeded");
    private static readonly Counter<long> DeliveryRetried = Meter.CreateCounter<long>("cmsify.webhook.delivery.retried");
    private static readonly Counter<long> DeliveryDeadLettered = Meter.CreateCounter<long>("cmsify.webhook.delivery.dead_lettered");
    private static readonly Counter<long> ScheduledClaimed = Meter.CreateCounter<long>("cmsify.schedule.claimed");
    private static readonly Counter<long> ScheduledReclaimed = Meter.CreateCounter<long>("cmsify.schedule.reclaimed");
    private static readonly Counter<long> ScheduledPublished = Meter.CreateCounter<long>("cmsify.schedule.published");
    private static readonly Counter<long> ScheduledFailures = Meter.CreateCounter<long>("cmsify.schedule.failures");
    private static readonly Counter<long> CleanupOutbox = Meter.CreateCounter<long>("cmsify.cleanup.outbox_deleted");
    private static readonly Counter<long> CleanupDeliveries = Meter.CreateCounter<long>("cmsify.cleanup.deliveries_deleted");

    static CmsifyOperationalMetrics()
    {
        Meter.CreateObservableGauge("cmsify.webhook.outbox.pending", () => Volatile.Read(ref pendingOutbox));
        Meter.CreateObservableGauge("cmsify.webhook.delivery.due", () => Volatile.Read(ref dueDeliveries));
        Meter.CreateObservableGauge("cmsify.schedule.due", () => Volatile.Read(ref dueScheduled));
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
    public static void RecordScheduledClaim(bool reclaimed) { ScheduledClaimed.Add(1); if (reclaimed) ScheduledReclaimed.Add(1); }
    public static void RecordScheduledPublished() => ScheduledPublished.Add(1);
    public static void RecordScheduledFailure() => ScheduledFailures.Add(1);
    public static void RecordCleanup(int outbox, int deliveries) { if (outbox > 0) CleanupOutbox.Add(outbox); if (deliveries > 0) CleanupDeliveries.Add(deliveries); }
}
