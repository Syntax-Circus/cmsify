using System.Text.Json;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookDispatchServiceLifecycleTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsExplicitBlankWorkerId(string workerId)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookDispatchService(
            scopeFactory,
            Configuration(),
            new CapturingLogger(),
            workerId: workerId));
    }

    [Fact]
    public void Constructor_RejectsAnExplicitWorkerIdLongerThanThePersistenceLimit()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        Assert.Throws<ArgumentOutOfRangeException>(() => new WebhookDispatchService(
            scopeFactory,
            Configuration(),
            new CapturingLogger(),
            workerId: new string('w', 201)));
    }

    [Fact]
    public async Task RunOnce_UsesAFreshScopeAfterOneMaterializationFails()
    {
        var now = DateTimeOffset.Parse("2026-08-26T09:00:00Z");
        var first = Claim("worker", "first");
        var second = Claim("worker", "second");
        var claimRepository = Substitute.For<IWebhookRepository>();
        claimRepository.ClaimOutboxEventsAsync("worker", now, TimeSpan.FromMinutes(5), 10, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ClaimedWebhookOutboxEventDto>>([first, second]));
        claimRepository.MaterializeOutboxEventAsync(Arg.Any<ClaimedWebhookOutboxEventDto>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(new InvalidOperationException("poisoned tracker")));
        var poisonedRepository = Substitute.For<IWebhookRepository>();
        poisonedRepository.MaterializeOutboxEventAsync(first, now, Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(new InvalidOperationException("poisoned tracker")));
        var successfulRepository = Substitute.For<IWebhookRepository>();
        successfulRepository.MaterializeOutboxEventAsync(second, now, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var claimScope = ScopeFor(claimRepository);
        var poisonedScope = ScopeFor(poisonedRepository);
        var successfulScope = ScopeFor(successfulRepository);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(claimScope, poisonedScope, successfulScope);
        var logger = new CapturingLogger();
        var service = new WebhookDispatchService(scopeFactory, Configuration(), logger, new FixedTimeProvider(now), "worker");

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        await successfulRepository.Received(1).MaterializeOutboxEventAsync(second, now, Arg.Any<CancellationToken>());
        Assert.Single(logger.Messages);
        Assert.Equal("Webhook outbox materialization failed.", logger.Messages[0]);
        Assert.DoesNotContain(first.Id.ToString(), logger.Messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnce_UsesFreshTimeForEachMaterialization()
    {
        var claimedAt = DateTimeOffset.Parse("2026-08-26T09:10:00Z");
        var firstCompletedAt = claimedAt.AddSeconds(10);
        var secondCompletedAt = claimedAt.AddSeconds(20);
        var first = Claim("worker", "first");
        var second = Claim("worker", "second");
        var claimRepository = Substitute.For<IWebhookRepository>();
        claimRepository.ClaimOutboxEventsAsync("worker", claimedAt, TimeSpan.FromMinutes(5), 10, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ClaimedWebhookOutboxEventDto>>([first, second]));
        var firstRepository = Substitute.For<IWebhookRepository>();
        firstRepository.MaterializeOutboxEventAsync(first, firstCompletedAt, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var secondRepository = Substitute.For<IWebhookRepository>();
        secondRepository.MaterializeOutboxEventAsync(second, secondCompletedAt, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var claimScope = ScopeFor(claimRepository);
        var firstScope = ScopeFor(firstRepository);
        var secondScope = ScopeFor(secondRepository);
        scopeFactory.CreateScope().Returns(claimScope, firstScope, secondScope);

        await new WebhookDispatchService(scopeFactory, Configuration(), new CapturingLogger(), new SequenceTimeProvider(claimedAt, firstCompletedAt, secondCompletedAt), "worker").RunOnceAsync(TestContext.Current.CancellationToken);

        await firstRepository.Received(1).MaterializeOutboxEventAsync(first, firstCompletedAt, Arg.Any<CancellationToken>());
        await secondRepository.Received(1).MaterializeOutboxEventAsync(second, secondCompletedAt, Arg.Any<CancellationToken>());
    }

    private static ClaimedWebhookOutboxEventDto Claim(string owner, string entitySeed) => new(
        Guid.CreateVersion7(),
        "workspace.updated",
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        JsonDocument.Parse($"{{\"seed\":\"{entitySeed}\"}}").RootElement.Clone(),
        DateTimeOffset.Parse("2026-08-26T09:00:00Z"),
        owner,
        Guid.CreateVersion7());

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Webhook:OutboxPollIntervalSeconds"] = "30",
        ["Webhook:OutboxLeaseDurationSeconds"] = "300",
        ["Webhook:OutboxBatchSize"] = "10"
    }).Build();

    private static IServiceScope ScopeFor(IWebhookRepository repository)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IWebhookRepository)).Returns(repository);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        return scope;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> values = new(values);
        public override DateTimeOffset GetUtcNow() => values.Dequeue();
    }

    private sealed class CapturingLogger : ILogger<WebhookDispatchService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
