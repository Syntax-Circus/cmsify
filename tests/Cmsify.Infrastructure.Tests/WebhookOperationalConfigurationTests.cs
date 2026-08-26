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
}
