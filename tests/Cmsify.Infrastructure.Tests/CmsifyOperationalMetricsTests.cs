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

    [Fact]
    public void WebhookSecurityFailureMetrics_NormalizeUnknownReasonsToOneFixedTag()
    {
        using var listener = new MeterListener();
        var measurements = new List<(string Name, List<KeyValuePair<string, object?>> Tags)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CmsifyOperationalMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var capturedTags = new List<KeyValuePair<string, object?>>();
            foreach (var tag in tags)
            {
                capturedTags.Add(tag);
            }

            measurements.Add((instrument.Name, capturedTags));
        });
        listener.Start();

        CmsifyOperationalMetrics.RecordDestinationRejection("untrusted user message");
        CmsifyOperationalMetrics.RecordPinnedConnectionFailure("untrusted user message");

        var securityFailures = measurements.Where(measurement => measurement.Name is "cmsify.webhook.destination.rejected" or "cmsify.webhook.connection.failed").ToArray();
        Assert.Equal(2, securityFailures.Length);
        Assert.All(securityFailures, measurement =>
        {
            var tag = Assert.Single(measurement.Tags);
            Assert.Equal("reason", tag.Key);
            Assert.Equal("unknown", tag.Value);
        });
    }
}
