using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookDeliveryProcessor
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IWebhookRepository webhookRepository;
    private readonly IWebhookDestinationValidator destinationValidator;
    private readonly TimeProvider timeProvider;

    public WebhookDeliveryProcessor(IHttpClientFactory httpClientFactory, IWebhookRepository webhookRepository, IWebhookDestinationValidator destinationValidator, TimeProvider? timeProvider = null)
    {
        this.httpClientFactory = httpClientFactory;
        this.webhookRepository = webhookRepository;
        this.destinationValidator = destinationValidator;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DeliverRetryAsync(PendingWebhookDeliveryDto delivery, int maxAttempts, CancellationToken ct)
    {
        var result = await PostAsync(delivery, ct);
        var completedAt = timeProvider.GetUtcNow();
        var completion = new WebhookDeliveryCompletionDto(delivery.Id, delivery.LeaseOwner, delivery.LeaseToken, completedAt);
        if (result.IsSuccess)
        {
            await webhookRepository.CompleteDeliverySucceededAsync(completion, result.StatusCode ?? 0, ct);
            return;
        }

        var nextAttempt = delivery.AttemptCount + 1;
        var isDeadLetter = nextAttempt >= maxAttempts;
        DateTimeOffset? nextRetryAt = isDeadLetter ? null : completedAt.Add(WebhookBackoffCalculator.CalculateDelay(nextAttempt));
        await webhookRepository.CompleteDeliveryFailedAsync(completion, result.StatusCode, result.Error, nextRetryAt, isDeadLetter, ct);
    }

    private async Task<(bool IsSuccess, int? StatusCode, string? Error)> PostAsync(PendingWebhookDeliveryDto delivery, CancellationToken ct)
    {
        try
        {
            var destination = await destinationValidator.ValidateAsync(delivery.Url, ct);
            var destinationUri = destination.DestinationUri;
            if (!destination.IsValid || destinationUri is null)
            {
                CmsifyOperationalMetrics.RecordDestinationRejection(GetDestinationRejectionReason(destination.Error));
                return (false, null, destination.Error ?? "Webhook destination validation failed.");
            }

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(delivery.Payload);
            using var content = new ByteArrayContent(payloadBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, destinationUri)
            {
                Content = content
            };
            request.Options.Set(PinnedWebhookTransport.DestinationKey, destination);
            request.Headers.Add("X-Cmsify-Signature", WebhookSigner.Sign(delivery.Secret, payloadBytes));
            request.Headers.Add("X-Cmsify-Event-Id", delivery.WebhookEventId.ToString("D"));
            request.Headers.Add("X-Cmsify-Event", delivery.EventType);

            using var response = await httpClientFactory.CreateClient(nameof(WebhookDeliveryProcessor)).SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? (true, (int)response.StatusCode, null)
                : (false, (int)response.StatusCode, $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "no reason phrase"}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CmsifyOperationalMetrics.RecordPinnedConnectionFailure(GetPinnedConnectionFailureReason(ex));
            return (false, null, ex.Message);
        }
    }

    private static string GetDestinationRejectionReason(string? error) => error switch
    {
        "Webhook URLs must use HTTPS and target a public host." => "url_policy",
        "Webhook host could not be resolved." => "resolution",
        "Webhook URLs must not resolve to private, loopback, or reserved addresses." => "address_policy",
        _ => "unknown"
    };

    private static string GetPinnedConnectionFailureReason(Exception exception) => exception switch
    {
        HttpRequestException => "connection",
        _ => "unknown"
    };
}
