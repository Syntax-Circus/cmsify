using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Reflection;

namespace Cmsify.Infrastructure.Tests;

public sealed class ScheduledPublishingServiceLifecycleTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsExplicitBlankWorkerId(string workerId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduledPublishingService(Substitute.For<IServiceScopeFactory>(), Configuration(), new CapturingLogger(), workerId: workerId));
    }

    [Fact]
    public void Constructor_RejectsOversizedWorkerId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScheduledPublishingService(Substitute.For<IServiceScopeFactory>(), Configuration(), new CapturingLogger(), workerId: new string('w', 201)));
    }

    [Fact]
    public async Task RunOnce_UsesAFreshCompletionScopeAfterOneClaimFails()
    {
        var now = DateTimeOffset.Parse("2026-08-26T13:00:00Z");
        var first = new ScheduledContentClaimDto(Guid.CreateVersion7(), "worker", Guid.CreateVersion7());
        var second = new ScheduledContentClaimDto(Guid.CreateVersion7(), "worker", Guid.CreateVersion7());
        var claimDispatcher = Substitute.For<IScheduledPublishingDispatcher>();
        claimDispatcher.ClaimDueAsync("worker", now, TimeSpan.FromMinutes(5), 10, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ScheduledContentClaimDto>>([first, second]));
        var failingDispatcher = Substitute.For<IScheduledPublishingDispatcher>();
        failingDispatcher.CompleteClaimAsync(first, now, Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(new InvalidOperationException("poisoned scope")));
        var successfulDispatcher = Substitute.For<IScheduledPublishingDispatcher>();
        successfulDispatcher.CompleteClaimAsync(second, now, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var claimScope = ScopeFor(claimDispatcher);
        var failingScope = ScopeFor(failingDispatcher);
        var successfulScope = ScopeFor(successfulDispatcher);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(claimScope, failingScope, successfulScope);
        var logger = new CapturingLogger();
        var service = new ScheduledPublishingService(scopeFactory, Configuration(), logger, new FixedTimeProvider(now), "worker");

        await service.RunOnceAsync();

        await successfulDispatcher.Received(1).CompleteClaimAsync(second, now, Arg.Any<CancellationToken>());
        Assert.Single(logger.Messages);
        Assert.Equal("Scheduled publishing completion failed.", logger.Messages[0]);
        Assert.DoesNotContain(first.ContentItemId.ToString(), logger.Messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunOnce_UsesFreshTimeForEachCompletion()
    {
        var claimedAt = DateTimeOffset.Parse("2026-08-26T13:10:00Z");
        var firstCompletedAt = claimedAt.AddSeconds(10);
        var secondCompletedAt = claimedAt.AddSeconds(20);
        var first = new ScheduledContentClaimDto(Guid.CreateVersion7(), "worker", Guid.CreateVersion7());
        var second = new ScheduledContentClaimDto(Guid.CreateVersion7(), "worker", Guid.CreateVersion7());
        var claimDispatcher = Substitute.For<IScheduledPublishingDispatcher>();
        claimDispatcher.ClaimDueAsync("worker", claimedAt, TimeSpan.FromMinutes(5), 10, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ScheduledContentClaimDto>>([first, second]));
        var firstDispatcher = Substitute.For<IScheduledPublishingDispatcher>();
        firstDispatcher.CompleteClaimAsync(first, firstCompletedAt, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var secondDispatcher = Substitute.For<IScheduledPublishingDispatcher>();
        secondDispatcher.CompleteClaimAsync(second, secondCompletedAt, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var claimScope = ScopeFor(claimDispatcher);
        var firstScope = ScopeFor(firstDispatcher);
        var secondScope = ScopeFor(secondDispatcher);
        scopeFactory.CreateScope().Returns(claimScope, firstScope, secondScope);

        await new ScheduledPublishingService(scopeFactory, Configuration(), new CapturingLogger(), new SequenceTimeProvider(claimedAt, firstCompletedAt, secondCompletedAt), "worker").RunOnceAsync();

        await firstDispatcher.Received(1).CompleteClaimAsync(first, firstCompletedAt, Arg.Any<CancellationToken>());
        await secondDispatcher.Received(1).CompleteClaimAsync(second, secondCompletedAt, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesAfterATransientClaimFailureAndStopsForCallerCancellation()
    {
        var now = DateTimeOffset.Parse("2026-08-26T14:00:00Z");
        using var cancellation = new CancellationTokenSource();
        var dispatcher = Substitute.For<IScheduledPublishingDispatcher>();
        var calls = 0;
        dispatcher.ClaimDueAsync(Arg.Any<string>(), now, TimeSpan.FromMinutes(5), 10, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Task.FromException<IReadOnlyList<ScheduledContentClaimDto>>(new InvalidOperationException("temporary database failure"));
            }

            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<ScheduledContentClaimDto>>([]);
        });
        var dispatcherScope = ScopeFor(dispatcher);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(dispatcherScope);
        var logger = new CapturingLogger();
        var service = new ScheduledPublishingService(scopeFactory, Configuration(), logger, new ImmediateTimeProvider(now), "worker");

        var execute = typeof(ScheduledPublishingService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)execute.Invoke(service, [cancellation.Token])!);

        Assert.Equal(2, calls);
        Assert.Contains("Scheduled publishing cycle failed.", logger.Messages);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Scheduler:PublishingIntervalSeconds"] = "1",
        ["Scheduler:PublishingLeaseDurationSeconds"] = "300",
        ["Scheduler:PublishingBatchSize"] = "10"
    }).Build();

    private static IServiceScope ScopeFor(IScheduledPublishingDispatcher dispatcher)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IScheduledPublishingDispatcher)).Returns(dispatcher);
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

    private sealed class ImmediateTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new ImmediateTimer();
        }
    }

    private sealed class ImmediateTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger<ScheduledPublishingService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
