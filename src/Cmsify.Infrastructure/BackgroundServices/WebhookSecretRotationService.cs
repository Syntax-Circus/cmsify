using System.Diagnostics;
using Cmsify.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookSecretRotationService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<WebhookSecretRotationService> logger;
    private readonly TimeProvider timeProvider;
    private readonly bool enabled;
    private readonly TimeSpan delay;
    private readonly string[] configuredKeyIds;

    public WebhookSecretRotationService(
        IServiceScopeFactory scopeFactory,
        IOptions<SecretProtectionOptions> options,
        ILogger<WebhookSecretRotationService> logger,
        TimeProvider? timeProvider = null)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var values = options.Value;
        enabled = values.Rotation.Enabled;
        delay = TimeSpan.FromSeconds(values.Rotation.DelaySeconds);
        configuredKeyIds = values.EncryptionKeys.Keys.ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!enabled)
        {
            return;
        }

        Guid? cursor = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var resetCursorAfterDelay = false;
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IWebhookSecretRotationProcessor>();
                var result = await processor.RotateBatchAsync(cursor, stoppingToken);
                CmsifyOperationalMetrics.RecordSecretRotationRows(result);
                cursor = result.NextCursor;
                if (result.ReachedEnd)
                {
                    CmsifyOperationalMetrics.ReportSecretRotationRemaining(
                        await processor.CountRemainingAsync(stoppingToken),
                        configuredKeyIds);
                    resetCursorAfterDelay = true;
                }
                CmsifyOperationalMetrics.RecordSecretRotationCycle("succeeded");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                CmsifyOperationalMetrics.RecordSecretRotationCycle("failed");
                logger.LogError("Webhook secret rotation cycle failed.");
            }
            finally
            {
                CmsifyOperationalMetrics.RecordSecretRotationDuration(Stopwatch.GetElapsedTime(startedAt));
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (resetCursorAfterDelay)
            {
                cursor = null;
            }
        }
    }
}
