using Cmsify.Core.Interfaces.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookRetryService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<WebhookRetryService> logger;
    private readonly TimeSpan interval;
    private readonly int maxAttempts;

    public WebhookRetryService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<WebhookRetryService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        interval = TimeSpan.FromSeconds(configuration.GetValue("Webhook:RetryIntervalSeconds", 30));
        maxAttempts = configuration.GetValue("Webhook:MaxAttempts", 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWebhookRepository>();
            var processor = scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>();
            var pending = await repository.ClaimPendingDeliveryLogsAsync(DateTimeOffset.UtcNow, 100, stoppingToken);

            foreach (var delivery in pending)
            {
                try
                {
                    await processor.DeliverRetryAsync(delivery, maxAttempts, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Webhook retry failed for delivery {DeliveryLogId}", delivery.Id);
                }
            }
        }
    }
}
