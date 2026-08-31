using System.Net;
using System.Net.Sockets;

namespace Cmsify.Infrastructure.BackgroundServices;

public static class WebhookAddressPolicy
{
    // IANA registries reviewed 2026-08-26:
    // https://www.iana.org/assignments/iana-ipv4-special-registry/iana-ipv4-special-registry.xhtml
    // https://www.iana.org/assignments/iana-ipv6-special-registry/iana-ipv6-special-registry.xhtml
    private static readonly IpPrefix[] NonGlobalIpv4 =
    [
        Prefix("0.0.0.0/8"), Prefix("10.0.0.0/8"), Prefix("100.64.0.0/10"), Prefix("127.0.0.0/8"),
        Prefix("169.254.0.0/16"), Prefix("172.16.0.0/12"), Prefix("192.0.0.0/24"), Prefix("192.0.2.0/24"),
        Prefix("192.88.99.0/24"), Prefix("192.168.0.0/16"), Prefix("198.18.0.0/15"), Prefix("198.51.100.0/24"),
        Prefix("203.0.113.0/24"), Prefix("224.0.0.0/4"), Prefix("240.0.0.0/4")
    ];

    private static readonly IpPrefix[] NonGlobalIpv6 =
    [
        Prefix("::/128"), Prefix("::1/128"), Prefix("64:ff9b:1::/48"), Prefix("100::/64"),
        Prefix("100:0:0:1::/64"), Prefix("2001::/23"), Prefix("2001:2::/48"), Prefix("2001:db8::/32"),
        Prefix("2002::/16"), Prefix("3fff::/20"), Prefix("5f00::/16"), Prefix("fc00::/7"), Prefix("fe80::/10"),
        Prefix("ff00::/8")
    ];

    private static readonly IPAddress[] GlobalIpv4Exceptions = [IPAddress.Parse("192.0.0.9"), IPAddress.Parse("192.0.0.10")];
    private static readonly IPAddress[] GlobalIpv6Exceptions =
    [
        IPAddress.Parse("2001:1::1"), IPAddress.Parse("2001:1::2"), IPAddress.Parse("2001:1::3")
    ];
    private static readonly IpPrefix[] GlobalIpv6ExceptionPrefixes =
    [
        Prefix("2001:3::/32"), Prefix("2001:4:112::/48"), Prefix("2001:20::/28"), Prefix("2001:30::/28")
    ];

    private static readonly IpPrefix GlobalIpv6Unicast = Prefix("2000::/3");
    private static readonly IpPrefix GlobalIpv6Translation = Prefix("64:ff9b::/96");

    public static bool IsGlobal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsGlobalIpv4(address),
            AddressFamily.InterNetworkV6 => IsGlobalIpv6(address),
            _ => false
        };
    }

    private static bool IsGlobalIpv4(IPAddress address) =>
        GlobalIpv4Exceptions.Contains(address) || !NonGlobalIpv4.Any(prefix => prefix.Contains(address));

    private static bool IsGlobalIpv6(IPAddress address)
    {
        if (GlobalIpv6Translation.Contains(address))
        {
            return IsGlobalIpv4(new IPAddress(address.GetAddressBytes()[^4..]));
        }

        return GlobalIpv6Exceptions.Contains(address)
            || GlobalIpv6ExceptionPrefixes.Any(prefix => prefix.Contains(address))
            || (!NonGlobalIpv6.Any(prefix => prefix.Contains(address)) && GlobalIpv6Unicast.Contains(address));
    }

    private static IpPrefix Prefix(string value)
    {
        var parts = value.Split('/');
        return new IpPrefix(IPAddress.Parse(parts[0]).GetAddressBytes(), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class IpPrefix(byte[] network, int length)
    {
        public bool Contains(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != network.Length)
            {
                return false;
            }

            var wholeBytes = length / 8;
            for (var index = 0; index < wholeBytes; index++)
            {
                if (bytes[index] != network[index])
                {
                    return false;
                }
            }

            var remainingBits = length % 8;
            var mask = (byte)(0xff << (8 - remainingBits));
            return remainingBits == 0 || (bytes[wholeBytes] & mask) == (network[wholeBytes] & mask);
        }
    }
}
