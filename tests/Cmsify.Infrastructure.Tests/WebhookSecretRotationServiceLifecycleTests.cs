using System.Reflection;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookSecretRotationServiceLifecycleTests
{
    [Fact]
    public async Task ExecuteAsync_WhenRotationIsDisabled_DoesNotCreateAScope()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = CreateService(scopeFactory, enabled: false);

        await ExecuteAsync(service, CancellationToken.None);

        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task ExecuteAsync_AdvancesCursorThenRefreshesCountsBeforeDelayingAndResetting()
    {
        using var cancellation = new CancellationTokenSource();
        var firstCursor = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondCursor = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var processor = Substitute.For<IWebhookSecretRotationProcessor>();
        processor.RotateBatchAsync(null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SecretRotationBatchResult(firstCursor, 1, 1, 0, 0, false)));
        processor.RotateBatchAsync(firstCursor, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SecretRotationBatchResult(secondCursor, 1, 0, 1, 0, true)));
        processor.CountRemainingAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromResult<IReadOnlyList<SecretCiphertextCount>>([new("v2", "key_current", 3)]);
            });
        var scopeFactory = ScopeFactoryFor(processor);
        var delay = new CancellingDelayTimeProvider(cancellation, cancelOnDelay: 2);
        var service = CreateService(scopeFactory, enabled: true, delay);

        await ExecuteAsync(service, cancellation.Token);

        await processor.Received(1).RotateBatchAsync(null, Arg.Any<CancellationToken>());
        await processor.Received(1).RotateBatchAsync(firstCursor, Arg.Any<CancellationToken>());
        await processor.Received(1).CountRemainingAsync(Arg.Any<CancellationToken>());
        scopeFactory.Received(2).CreateScope();
        Assert.Equal(TimeSpan.FromSeconds(7), delay.LastDueTime);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheProcessorIsCancelled_StopsWithoutDelaying()
    {
        using var cancellation = new CancellationTokenSource();
        var processor = Substitute.For<IWebhookSecretRotationProcessor>();
        processor.RotateBatchAsync(null, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<SecretRotationBatchResult>(cancellation.Token);
            });
        var scopeFactory = ScopeFactoryFor(processor);
        var delay = new CancellingDelayTimeProvider(cancellation);
        var service = CreateService(scopeFactory, enabled: true, delay);

        await ExecuteAsync(service, cancellation.Token);

        Assert.Null(delay.LastDueTime);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheProcessorFails_RecordsBoundedFailureAndDelaysBeforeStopping()
    {
        using var cancellation = new CancellationTokenSource();
        var processor = Substitute.For<IWebhookSecretRotationProcessor>();
        processor.RotateBatchAsync(null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SecretRotationBatchResult>(new InvalidOperationException("database endpoint=untrusted")));
        var scopeFactory = ScopeFactoryFor(processor);
        var delay = new CancellingDelayTimeProvider(cancellation);
        var logger = new CapturingLogger();
        var service = CreateService(scopeFactory, enabled: true, delay, logger);

        await ExecuteAsync(service, cancellation.Token);

        Assert.Equal(TimeSpan.FromSeconds(7), delay.LastDueTime);
        Assert.Contains("Webhook secret rotation cycle failed.", logger.Messages);
        Assert.DoesNotContain("endpoint=untrusted", logger.Messages[0], StringComparison.Ordinal);
    }

    private static WebhookSecretRotationService CreateService(
        IServiceScopeFactory scopeFactory,
        bool enabled,
        TimeProvider? timeProvider = null,
        ILogger<WebhookSecretRotationService>? logger = null) =>
        new(scopeFactory, Options.Create(new SecretProtectionOptions
        {
            ActiveKeyId = "key_current",
            EncryptionKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["key_current"] = "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2A="
            },
            Rotation = new SecretRotationOptions { Enabled = enabled, DelaySeconds = 7 }
        }), logger ?? new CapturingLogger(), timeProvider);

    private static IServiceScopeFactory ScopeFactoryFor(IWebhookSecretRotationProcessor processor)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IWebhookSecretRotationProcessor)).Returns(processor);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        return scopeFactory;
    }

    private static Task ExecuteAsync(WebhookSecretRotationService service, CancellationToken cancellationToken) =>
        (Task)typeof(WebhookSecretRotationService)
            .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [cancellationToken])!;

    private sealed class CancellingDelayTimeProvider(CancellationTokenSource cancellation, int cancelOnDelay = 1) : TimeProvider
    {
        private int delayCount;
        public TimeSpan? LastDueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            LastDueTime = dueTime;
            var delayNumber = Interlocked.Increment(ref delayCount);
            callback(state);
            if (delayNumber >= cancelOnDelay)
            {
                cancellation.Cancel();
            }
            return new NoopTimer();
        }
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger<WebhookSecretRotationService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
