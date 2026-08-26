using Cmsify.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.BackgroundServices;

/// <summary>
/// Publishes one read-only rotation inventory before rotation is enabled.
/// </summary>
public sealed class WebhookSecretRotationInventoryPreflightService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<WebhookSecretRotationInventoryPreflightService> logger;
    private readonly TimeProvider timeProvider;
    private readonly bool rotationEnabled;
    private readonly TimeSpan retryDelay;
    private readonly string[] configuredKeyIds;

    public WebhookSecretRotationInventoryPreflightService(
        IServiceScopeFactory scopeFactory,
        IOptions<SecretProtectionOptions> options,
        ILogger<WebhookSecretRotationInventoryPreflightService> logger,
        TimeProvider? timeProvider = null)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var values = options.Value;
        rotationEnabled = values.Rotation.Enabled;
        retryDelay = TimeSpan.FromSeconds(values.Rotation.DelaySeconds);
        configuredKeyIds = values.EncryptionKeys.Keys.ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (rotationEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IWebhookSecretRotationProcessor>();
                CmsifyOperationalMetrics.ReportSecretRotationRemaining(
                    await processor.CountRemainingAsync(stoppingToken),
                    configuredKeyIds);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogWarning("Webhook secret rotation inventory preflight failed; retrying.");
                try
                {
                    await Task.Delay(retryDelay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
