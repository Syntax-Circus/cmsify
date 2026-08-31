using Cmsify.Core.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookRetryService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<WebhookRetryService> logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan interval;
    private readonly TimeSpan leaseDuration;
    private readonly int batchSize;
    private readonly int maxAttempts;
    private readonly string workerId;

    [ActivatorUtilitiesConstructor]
    public WebhookRetryService(
        IServiceScopeFactory scopeFactory,
        IOptions<WebhookOperationalOptions> options,
        ILogger<WebhookRetryService> logger,
        TimeProvider? timeProvider = null,
        string? workerId = null)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var values = options.Value;
        interval = TimeSpan.FromSeconds(values.RetryIntervalSeconds);
        leaseDuration = TimeSpan.FromSeconds(values.DeliveryLeaseDurationSeconds);
        batchSize = values.DeliveryBatchSize;
        maxAttempts = values.MaxAttempts;
        this.workerId = workerId is null ? Guid.CreateVersion7().ToString("N") : ValidateWorkerId(workerId);
    }

    public WebhookRetryService(IServiceScopeFactory scopeFactory, Microsoft.Extensions.Configuration.IConfiguration configuration, ILogger<WebhookRetryService> logger, TimeProvider? timeProvider = null, string? workerId = null)
        : this(scopeFactory, Options.Create(OperationalOptions.ReadWebhook(configuration)), logger, timeProvider, workerId)
    {
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        IReadOnlyList<PendingWebhookDeliveryDto> pending;
        await using (var claimScope = scopeFactory.CreateAsyncScope())
        {
            var repository = claimScope.ServiceProvider.GetRequiredService<IWebhookRepository>();
            pending = await repository.ClaimPendingDeliveryLogsAsync(workerId, now, leaseDuration, batchSize, ct);
        }

        foreach (var delivery in pending)
        {
            try
            {
                await using var deliveryScope = scopeFactory.CreateAsyncScope();
                var processor = deliveryScope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>();
                await processor.DeliverRetryAsync(delivery, maxAttempts, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook retry delivery failed.");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook retry cycle failed.");
            }

            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static string ValidateWorkerId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Webhook retry worker IDs must be nonblank and at most 200 characters.");
        }

        return value;
    }
}
