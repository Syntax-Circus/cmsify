using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Cmsify.Infrastructure.BackgroundServices;

namespace Cmsify.Infrastructure.Tests;

public sealed class CmsifyOperationalMetricsTests
{
    [Fact]
    public void SecretRotationRemaining_ReportsExplicitBoundedZerosAfterASuccessfulEmptyRefresh()
    {
        var (listener, measurements) = CreateListener();
        using (listener)
        {
            CmsifyOperationalMetrics.ReportSecretRotationRemaining([], ["key_current"]);
            listener.RecordObservableInstruments();

            AssertMetric(measurements, "cmsify.webhook.secret.rotation.remaining", ["version", "key_id"], ["v1", "legacy"]);
            AssertMetric(measurements, "cmsify.webhook.secret.rotation.remaining", ["version", "key_id"], ["v2", "key_current"]);
            AssertMetric(measurements, "cmsify.webhook.secret.rotation.remaining", ["version", "key_id"], ["v2", "unknown"]);
            AssertMetric(measurements, "cmsify.webhook.secret.rotation.remaining", ["version", "key_id"], ["unknown", "unknown"]);
        }
    }

    [Fact]
    public void DeliveryAndClaimMetrics_AreLowCardinalityAndObservable()
    {
        using var listener = new MeterListener();
        var measurements = new ConcurrentQueue<(string Name, int TagCount)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CmsifyOperationalMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) => measurements.Enqueue((instrument.Name, tags.Length)));
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
        var deliveryAndClaimNames = new HashSet<string>
        {
            "cmsify.webhook.outbox.claimed",
            "cmsify.webhook.delivery.claimed",
            "cmsify.webhook.delivery.succeeded",
            "cmsify.webhook.delivery.retried",
            "cmsify.webhook.delivery.dead_lettered"
        };
        Assert.All(
            measurements.Where(measurement => deliveryAndClaimNames.Contains(measurement.Name)),
            measurement => Assert.Equal(0, measurement.TagCount));
    }

    [Fact]
    public void WebhookSecurityFailureMetrics_NormalizeUnknownReasonsToOneFixedTag()
    {
        using var listener = new MeterListener();
        var measurements = new ConcurrentQueue<(string Name, List<KeyValuePair<string, object?>> Tags)>();
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

            measurements.Enqueue((instrument.Name, capturedTags));
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

    [Fact]
    public void SecretRotationMetrics_UseOnlyBoundedConfiguredKeyAndOutcomeLabels()
    {
        using var listener = new MeterListener();
        var measurements = new ConcurrentQueue<(string Name, List<KeyValuePair<string, object?>> Tags)>();
        var doubleMeasurements = new ConcurrentQueue<(string Name, int TagCount)>();
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

            measurements.Enqueue((instrument.Name, capturedTags));
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) => doubleMeasurements.Enqueue((instrument.Name, tags.Length)));
        listener.Start();

        CmsifyOperationalMetrics.RecordSecretDecryptFailure("v2", "attacker-controlled-key", "arbitrary failure", ["key_current"]);
        CmsifyOperationalMetrics.RecordSecretRotationRows(new SecretRotationBatchResult(null, 3, 1, 1, 1, true));
        CmsifyOperationalMetrics.RecordSecretRotationCycle("unexpected exception");
        CmsifyOperationalMetrics.RecordSecretRotationDuration(TimeSpan.FromSeconds(2));
        CmsifyOperationalMetrics.ReportSecretRotationRemaining([new SecretCiphertextCount("v2", "attacker-controlled-key", 3)], ["key_current"]);
        listener.RecordObservableInstruments();

        AssertMetric(measurements, "cmsify.webhook.secret.decrypt_failures", ["version", "key_id", "reason"], ["v2", "unknown", "unknown"]);
        AssertMetric(measurements, "cmsify.webhook.secret.rotation.rows", ["outcome"], ["rotated"]);
        AssertMetric(measurements, "cmsify.webhook.secret.rotation.rows", ["outcome"], ["skipped"]);
        AssertMetric(measurements, "cmsify.webhook.secret.rotation.rows", ["outcome"], ["failed"]);
        AssertMetric(measurements, "cmsify.webhook.secret.rotation.cycles", ["outcome"], ["failed"]);
        Assert.Contains(doubleMeasurements, measurement => measurement.Name == "cmsify.webhook.secret.rotation.duration" && measurement.TagCount == 0);
        AssertMetric(measurements, "cmsify.webhook.secret.rotation.remaining", ["version", "key_id"], ["v2", "unknown"]);
        Assert.DoesNotContain(measurements.SelectMany(measurement => measurement.Tags), tag => tag.Key is "endpoint" or "workspace" or "ciphertext");
    }

    [Fact]
    public void MediaMetrics_NormalizeProviderReasonAndOutcomeLabels()
    {
        var (listener, measurements) = CreateListener();
        using (listener)
        {
            CmsifyOperationalMetrics.RecordMediaDeletionClaim("attacker-provider", reclaimed: true);
            CmsifyOperationalMetrics.RecordMediaDeletion("attacker-provider", "asset-secret", "attacker-outcome");
            CmsifyOperationalMetrics.RecordMediaRetry("attacker-provider", "exception text");
            CmsifyOperationalMetrics.RecordMediaOrphan("attacker-provider");
            CmsifyOperationalMetrics.RecordMediaMissing("attacker-provider");
            CmsifyOperationalMetrics.RecordMediaScan("attacker-provider", "attacker-outcome");

            AssertMetric(measurements, "cmsify.media.deletion.claimed", ["provider"], ["unknown"]);
            AssertMetric(measurements, "cmsify.media.deletion.outcome", ["provider", "reason", "outcome"], ["unknown", "unknown", "failed"]);
            AssertMetric(measurements, "cmsify.media.deletion.retried", ["provider", "reason"], ["unknown", "unknown"]);
            Assert.DoesNotContain(measurements.SelectMany(measurement => measurement.Tags), tag => tag.Value?.ToString() is "asset-secret" or "exception text");
        }
    }

    private static void AssertMetric(
        IEnumerable<(string Name, List<KeyValuePair<string, object?>> Tags)> measurements,
        string name,
        IReadOnlyList<string> expectedKeys,
        IReadOnlyList<string> expectedValues) =>
        Assert.Contains(measurements, measurement => measurement.Name == name
            && measurement.Tags.Select(tag => tag.Key).SequenceEqual(expectedKeys)
            && measurement.Tags.Select(tag => tag.Value).SequenceEqual(expectedValues.Cast<object?>()));

    private static (MeterListener Listener, ConcurrentQueue<(string Name, List<KeyValuePair<string, object?>> Tags)> Measurements) CreateListener()
    {
        var measurements = new ConcurrentQueue<(string Name, List<KeyValuePair<string, object?>> Tags)>();
        var listener = new MeterListener();
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
            measurements.Enqueue((instrument.Name, capturedTags));
        });
        listener.Start();
        return (listener, measurements);
    }
}
