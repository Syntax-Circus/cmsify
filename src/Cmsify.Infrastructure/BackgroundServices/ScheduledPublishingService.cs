using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class ScheduledPublishingService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<ScheduledPublishingService> logger;
    private readonly TimeSpan interval;

    public ScheduledPublishingService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<ScheduledPublishingService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        interval = TimeSpan.FromSeconds(configuration.GetValue("Scheduler:PublishingIntervalSeconds", 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IScheduledPublishingDispatcher>();
                await dispatcher.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled publishing run failed.");
            }
        }
    }
}
