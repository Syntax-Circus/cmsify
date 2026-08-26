using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class ScheduledPublishingService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<ScheduledPublishingService> logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan interval;
    private readonly TimeSpan leaseDuration;
    private readonly int batchSize;
    private readonly string workerId;

    [ActivatorUtilitiesConstructor]
    public ScheduledPublishingService(IServiceScopeFactory scopeFactory, IOptions<SchedulerOperationalOptions> options, ILogger<ScheduledPublishingService> logger, TimeProvider? timeProvider = null, string? workerId = null)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var values = options.Value;
        interval = TimeSpan.FromSeconds(values.PublishingIntervalSeconds);
        leaseDuration = TimeSpan.FromSeconds(values.PublishingLeaseDurationSeconds);
        batchSize = values.PublishingBatchSize;
        this.workerId = workerId is null ? Guid.CreateVersion7().ToString("N") : ValidateWorkerId(workerId);
    }

    public ScheduledPublishingService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<ScheduledPublishingService> logger, TimeProvider? timeProvider = null, string? workerId = null)
        : this(scopeFactory, Options.Create(OperationalOptions.ReadScheduler(configuration)), logger, timeProvider, workerId)
    {
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ScheduledContentClaimDto> claims;
        var now = timeProvider.GetUtcNow();
        await using (var claimScope = scopeFactory.CreateAsyncScope())
        {
            var dispatcher = claimScope.ServiceProvider.GetRequiredService<IScheduledPublishingDispatcher>();
            claims = await dispatcher.ClaimDueAsync(workerId, now, leaseDuration, batchSize, ct);
        }

        foreach (var claim in claims)
        {
            try
            {
                await using var completionScope = scopeFactory.CreateAsyncScope();
                var dispatcher = completionScope.ServiceProvider.GetRequiredService<IScheduledPublishingDispatcher>();
                await dispatcher.CompleteClaimAsync(claim, timeProvider.GetUtcNow(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CmsifyOperationalMetrics.RecordScheduledFailure();
                logger.LogError(ex, "Scheduled publishing completion failed.");
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
                logger.LogError(ex, "Scheduled publishing cycle failed.");
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

    private static string ValidateWorkerId(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), "Scheduled publishing worker IDs must be nonblank and at most 200 characters.");
        }

        return workerId;
    }
}
