using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookDeliveryProcessor
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IWebhookRepository webhookRepository;
    private readonly IWebhookDestinationValidator destinationValidator;

    public WebhookDeliveryProcessor(IHttpClientFactory httpClientFactory, IWebhookRepository webhookRepository, IWebhookDestinationValidator destinationValidator)
    {
        this.httpClientFactory = httpClientFactory;
        this.webhookRepository = webhookRepository;
        this.destinationValidator = destinationValidator;
    }

    public async Task DeliverInitialAsync(WebhookEvent evt, WebhookDispatchTargetDto target, CancellationToken ct)
    {
        var payload = evt.Payload;
        var result = await PostAsync(target.Url, target.Secret, payload, ct);
        var now = DateTimeOffset.UtcNow;

        await webhookRepository.AddDeliveryLogAsync(new WebhookDeliveryLogDto(
            Guid.CreateVersion7(),
            target.Id,
            evt.EventType,
            payload,
            1,
            now,
            result.IsSuccess ? null : now.Add(WebhookBackoffCalculator.CalculateDelay(1)),
            result.StatusCode,
            result.IsSuccess,
            false,
            now),
            ct);
    }

    public async Task DeliverRetryAsync(PendingWebhookDeliveryDto delivery, int maxAttempts, CancellationToken ct)
    {
        var result = await PostAsync(delivery.Url, delivery.Secret, delivery.Payload, ct);
        if (result.IsSuccess)
        {
            await webhookRepository.MarkDeliverySucceededAsync(delivery.Id, result.StatusCode ?? 0, ct);
            return;
        }

        var nextAttempt = delivery.AttemptCount + 1;
        var isFailed = nextAttempt >= maxAttempts;
        var nextRetryAt = DateTimeOffset.UtcNow.Add(WebhookBackoffCalculator.CalculateDelay(nextAttempt));
        await webhookRepository.MarkDeliveryFailedAsync(delivery.Id, result.StatusCode, nextRetryAt, isFailed, ct);
    }

    private async Task<(bool IsSuccess, int? StatusCode)> PostAsync(string url, string secret, JsonElement payload, CancellationToken ct)
    {
        var destination = await destinationValidator.ValidateAsync(url, ct);
        if (!destination.IsValid)
        {
            return (false, null);
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var content = new ByteArrayContent(payloadBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, destination.NormalizedUrl)
        {
            Content = content
        };
        request.Headers.Add("X-Cmsify-Signature", WebhookSigner.Sign(secret, payloadBytes));

        try
        {
            using var response = await httpClientFactory.CreateClient(nameof(WebhookDeliveryProcessor)).SendAsync(request, ct);
            return (response.IsSuccessStatusCode, (int)response.StatusCode);
        }
        catch (HttpRequestException)
        {
            return (false, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, null);
        }
    }
}
