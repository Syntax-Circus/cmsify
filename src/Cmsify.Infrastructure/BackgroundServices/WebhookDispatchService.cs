using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookDispatchService : BackgroundService
{
    private readonly IWebhookQueue queue;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<WebhookDispatchService> logger;

    public WebhookDispatchService(IWebhookQueue queue, IServiceScopeFactory scopeFactory, ILogger<WebhookDispatchService> logger)
    {
        this.queue = queue;
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var evt in queue.DequeueAllAsync(stoppingToken))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWebhookRepository>();
            var processor = scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>();
            var targets = await repository.GetActiveEndpointsForEventAsync(evt.EventType, evt.WorkspaceId, stoppingToken);

            foreach (var target in targets)
            {
                try
                {
                    await processor.DeliverInitialAsync(evt, target, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Webhook dispatch failed for endpoint {EndpointId}", target.Id);
                }
            }
        }
    }
}
