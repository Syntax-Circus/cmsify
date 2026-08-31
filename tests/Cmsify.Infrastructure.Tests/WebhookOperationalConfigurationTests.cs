using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookOperationalConfigurationTests
{
    [Fact]
    public void WebhookBatchDefaults_AreOneHundred()
    {
        var options = new WebhookOperationalOptions();

        Assert.Equal(100, options.OutboxBatchSize);
        Assert.Equal(100, options.DeliveryBatchSize);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public void WebhookBatchBounds_AcceptSupportedEndpoints(int batchSize)
    {
        var options = new WebhookOperationalOptions
        {
            OutboxBatchSize = batchSize,
            DeliveryBatchSize = batchSize
        };

        Assert.True(new WebhookOperationalOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void WebhookBatchBounds_RejectValuesOutsideSupportedRange(int batchSize)
    {
        var outbox = new WebhookOperationalOptions { OutboxBatchSize = batchSize };
        var delivery = new WebhookOperationalOptions { DeliveryBatchSize = batchSize };
        var validator = new WebhookOperationalOptionsValidator();

        Assert.True(validator.Validate(null, outbox).Failed);
        Assert.True(validator.Validate(null, delivery).Failed);
    }

    [Fact]
    public void RetryWorker_RejectsZeroMaxAttemptsAtConstruction()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Webhook:MaxAttempts"] = "0"
        }).Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookRetryService(
            Substitute.For<IServiceScopeFactory>(),
            configuration,
            NullLogger<WebhookRetryService>.Instance));
    }

    [Fact]
    public void TypedOptions_RejectInvalidWebhookAndSchedulerBounds()
    {
        var webhook = new WebhookOperationalOptions { OutboxBatchSize = 501, DeliveryLeaseDurationSeconds = 0, RetentionDays = 0 };
        var scheduler = new SchedulerOperationalOptions { PublishingIntervalSeconds = 0, PublishingLeaseDurationSeconds = 1_801, PublishingBatchSize = 501 };

        Assert.True(new WebhookOperationalOptionsValidator().Validate(null, webhook).Failed);
        Assert.True(new SchedulerOperationalOptionsValidator().Validate(null, scheduler).Failed);
    }

    [Theory]
    [InlineData("release-smoke")]
    [InlineData("cmsify-smoke-too_short")]
    [InlineData("cmsify-smoke-1234abcd\nProduction")]
    public void ReleaseSmokeRunId_RejectsMalformedOrAmbiguousValues(string runId)
    {
        var result = new WebhookOperationalOptionsValidator().Validate(null, new WebhookOperationalOptions { ReleaseSmokeRunId = runId });

        Assert.True(result.Failed);
    }
}
