using Cmsify.Core.EmbeddedMedia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class EmbeddedPhotoProcessingService(IServiceScopeFactory scopes,
    IOptions<EmbeddedPhotoOptions> options, ILogger<EmbeddedPhotoProcessingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.ProcessingEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<IProcessEmbeddedPhotoQueueRequestHandler>();
                await handler.HandleAsync(new(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Photo processing poll failed; the durable lease governs recovery."); }
            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }
}
