using System.Net;
using System.Net.Sockets;
using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.BackgroundServices;

public sealed class WebhookDestinationValidator : IWebhookDestinationValidator
{
    private readonly bool allowHttp;

    public WebhookDestinationValidator(IConfiguration configuration)
    {
        allowHttp = configuration.GetValue("Webhook:AllowHttp", false);
    }

    public async Task<WebhookDestinationValidationResult> ValidateAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(allowHttp && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.IsLoopback)
        {
            return WebhookDestinationValidationResult.Invalid("Webhook URLs must use HTTPS and target a public host.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.DnsSafeHost, out var address)
                ? [address]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return WebhookDestinationValidationResult.Invalid("Webhook host could not be resolved.");
        }

        if (addresses.Length == 0 || addresses.Any(IsNonPublic))
        {
            return WebhookDestinationValidationResult.Invalid("Webhook URLs must not resolve to private, loopback, or reserved addresses.");
        }

        return WebhookDestinationValidationResult.Valid(uri.AbsoluteUri);
    }

    private static bool IsNonPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] >= 224
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19));
        }

        return address.AddressFamily != AddressFamily.InterNetworkV6
            || (bytes[0] & 0xfe) == 0xfc;
    }
}
