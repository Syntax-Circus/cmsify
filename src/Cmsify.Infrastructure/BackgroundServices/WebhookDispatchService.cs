using Cmsify.Core.Interfaces.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookDispatchService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<WebhookDispatchService> logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan leaseDuration;
    private readonly int batchSize;
    private readonly int retentionDays;
    private readonly int cleanupBatchSize;
    private readonly TimeSpan cleanupInterval;
    private readonly string workerId;
    private DateTimeOffset nextCleanupAt = DateTimeOffset.MinValue;

    [ActivatorUtilitiesConstructor]
    public WebhookDispatchService(
        IServiceScopeFactory scopeFactory,
        IOptions<WebhookOperationalOptions> options,
        ILogger<WebhookDispatchService> logger,
        TimeProvider? timeProvider = null,
        string? workerId = null)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var values = options.Value;
        pollInterval = TimeSpan.FromSeconds(values.OutboxPollIntervalSeconds);
        leaseDuration = TimeSpan.FromSeconds(values.OutboxLeaseDurationSeconds);
        batchSize = values.OutboxBatchSize;
        retentionDays = values.RetentionDays;
        cleanupBatchSize = values.CleanupBatchSize;
        cleanupInterval = TimeSpan.FromSeconds(values.CleanupIntervalSeconds);
        this.workerId = workerId is null
            ? Guid.CreateVersion7().ToString("N")
            : ValidateWorkerId(workerId);
    }

    public WebhookDispatchService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<WebhookDispatchService> logger, TimeProvider? timeProvider = null, string? workerId = null)
        : this(scopeFactory, Options.Create(OperationalOptions.ReadWebhook(configuration)), logger, timeProvider, workerId)
    {
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ClaimedWebhookOutboxEventDto> claims;
        var now = timeProvider.GetUtcNow();
        await using (var claimScope = scopeFactory.CreateAsyncScope())
        {
            var repository = claimScope.ServiceProvider.GetRequiredService<IWebhookRepository>();
            claims = await repository.ClaimOutboxEventsAsync(workerId, now, leaseDuration, batchSize, ct);
        }

        foreach (var claim in claims)
        {
            try
            {
                await using var materializationScope = scopeFactory.CreateAsyncScope();
                var repository = materializationScope.ServiceProvider.GetRequiredService<IWebhookRepository>();
                await repository.MaterializeOutboxEventAsync(claim, timeProvider.GetUtcNow(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CmsifyOperationalMetrics.RecordOutboxFailure();
                logger.LogError(ex, "Webhook outbox materialization failed.");
            }
        }

        if (now >= nextCleanupAt)
        {
            await using var cleanupScope = scopeFactory.CreateAsyncScope();
            var repository = cleanupScope.ServiceProvider.GetRequiredService<IWebhookRepository>();
            await repository.CleanupRetentionAsync(now.AddDays(-retentionDays), cleanupBatchSize, ct);
            nextCleanupAt = now.Add(cleanupInterval);
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
                logger.LogError(ex, "Webhook outbox polling cycle failed.");
            }

            try
            {
                await Task.Delay(pollInterval, timeProvider, stoppingToken);
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
            throw new ArgumentOutOfRangeException(nameof(value), "Webhook worker IDs must be non-blank and at most 200 characters.");
        }

        return value;
    }
}
