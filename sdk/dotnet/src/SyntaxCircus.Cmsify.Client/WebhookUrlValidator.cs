using System.Net;
using System.Net.Sockets;

namespace SyntaxCircus.Cmsify;

internal static class WebhookUrlValidator
{
    public static void Validate(string url, string parameterName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.IsLoopback)
        {
            throw new ArgumentException("Webhook URLs must use HTTPS, omit credentials, and target a public host.", parameterName);
        }

        if (IPAddress.TryParse(uri.DnsSafeHost, out var address) && IsNonPublic(address))
        {
            throw new ArgumentException("Webhook URLs must not target private, loopback, link-local, multicast, or reserved IP addresses.", parameterName);
        }
    }

    private static bool IsNonPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
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
