using System.Threading.Channels;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class InProcessWebhookQueue : IWebhookQueue
{
    private readonly Channel<WebhookEvent> channel;

    public InProcessWebhookQueue(IConfiguration configuration)
    {
        var capacity = configuration.GetValue("Webhook:QueueCapacity", 1024);
        channel = Channel.CreateBounded<WebhookEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(WebhookEvent evt, CancellationToken ct = default) =>
        channel.Writer.WriteAsync(evt, ct);

    public async IAsyncEnumerable<WebhookEvent> DequeueAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await channel.Reader.WaitToReadAsync(ct))
        {
            while (channel.Reader.TryRead(out var evt))
            {
                yield return evt;
            }
        }
    }
}
