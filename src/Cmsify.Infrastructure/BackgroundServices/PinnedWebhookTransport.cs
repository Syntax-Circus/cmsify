using System.Net;
using System.Net.Sockets;
using Cmsify.Core.Interfaces.Services;

namespace Cmsify.Infrastructure.BackgroundServices;

public interface IWebhookSocketConnector
{
    ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct);
}

public sealed class SocketWebhookConnector : IWebhookSocketConnector
{
    public async ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            throw new HttpRequestException("Webhook connection requires at least one approved address.");
        }

        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Exception? lastException = null;
        foreach (var address in addresses.ToArray())
        {
            if (address is null)
            {
                lastException = new HttpRequestException("Webhook connection candidates must be valid IP addresses.");
                continue;
            }

            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                socket?.Dispose();
                throw;
            }
            catch (Exception exception)
            {
                socket?.Dispose();
                lastException = exception;
            }
        }

        throw new HttpRequestException("Webhook connection to approved addresses failed.", lastException);
    }
}

public static class PinnedWebhookTransport
{
    public static readonly HttpRequestOptionsKey<WebhookDestinationValidationResult> DestinationKey =
        new("Cmsify.Webhook.ValidatedDestination");

    public static SocketsHttpHandler CreateHandler(IWebhookSocketConnector connector, TimeSpan connectTimeout)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            ConnectCallback = (context, ct) => ConnectAsync(context, connector, ct)
        };
    }

    private static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, IWebhookSocketConnector connector, CancellationToken ct)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(DestinationKey, out var destination)
            || !destination.IsValid
            || destination.DestinationUri is null
            || destination.Addresses.Count == 0)
        {
            throw new HttpRequestException("Webhook request is missing a validated destination.");
        }

        if (!string.Equals(destination.DestinationUri.IdnHost, context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase)
            || destination.DestinationUri.Port != context.DnsEndPoint.Port)
        {
            throw new HttpRequestException("Webhook request authority does not match its validated destination.");
        }

        return connector.ConnectAsync(destination.Addresses.ToArray(), destination.DestinationUri.Port, ct);
    }
}
