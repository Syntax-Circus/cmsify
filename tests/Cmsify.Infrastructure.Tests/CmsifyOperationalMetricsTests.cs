using System.Diagnostics.Metrics;
using Cmsify.Infrastructure.BackgroundServices;

namespace Cmsify.Infrastructure.Tests;

public sealed class CmsifyOperationalMetricsTests
{
    [Fact]
    public void DeliveryAndClaimMetrics_AreLowCardinalityAndObservable()
    {
        using var listener = new MeterListener();
        var measurements = new List<(string Name, int TagCount)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CmsifyOperationalMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) => measurements.Add((instrument.Name, tags.Length)));
        listener.Start();

        CmsifyOperationalMetrics.RecordOutboxClaim(reclaimed: true);
        CmsifyOperationalMetrics.RecordDeliveryClaim(reclaimed: false);
        CmsifyOperationalMetrics.RecordDeliverySucceeded();
        CmsifyOperationalMetrics.RecordDeliveryRetried();
        CmsifyOperationalMetrics.RecordDeliveryDeadLettered();
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, measurement => measurement.Name == "cmsify.webhook.outbox.claimed");
        Assert.Contains(measurements, measurement => measurement.Name == "cmsify.webhook.delivery.claimed");
        Assert.Contains(measurements, measurement => measurement.Name == "cmsify.webhook.delivery.succeeded");
        Assert.Contains(measurements, measurement => measurement.Name == "cmsify.webhook.delivery.retried");
        Assert.Contains(measurements, measurement => measurement.Name == "cmsify.webhook.delivery.dead_lettered");
        Assert.All(measurements, measurement => Assert.Equal(0, measurement.TagCount));
    }
}
