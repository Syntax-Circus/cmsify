using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class MediaReconciliationService(
    IServiceScopeFactory scopeFactory,
    IOptions<MediaOperationalOptions> options,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    ILogger<MediaReconciliationService> logger,
    TimeProvider? timeProvider = null,
    string? workerId = null) : BackgroundService
{
    private readonly string providerName = (configuration["Storage:Provider"] ?? "local").ToLowerInvariant();
    private readonly string workerId = workerId ?? Guid.CreateVersion7().ToString("N");

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var processor = new MediaReconciliationProcessor(
            scope.ServiceProvider.GetRequiredService<IMediaReconciliationRepository>(),
            scope.ServiceProvider.GetRequiredService<IStorageProvider>(),
            options,
            providerName,
            loggerFactory.CreateLogger<MediaReconciliationProcessor>(),
            timeProvider ?? TimeProvider.System);
        await processor.RunCycleAsync(workerId, (timeProvider ?? TimeProvider.System).GetUtcNow(), ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.ReconciliationIntervalSeconds);
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
            catch (Exception)
            {
                CmsifyOperationalMetrics.RecordMediaCycleFailure();
                logger.LogError("Media reconciliation cycle failed.");
            }

            try
            {
                await Task.Delay(interval, timeProvider ?? TimeProvider.System, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
