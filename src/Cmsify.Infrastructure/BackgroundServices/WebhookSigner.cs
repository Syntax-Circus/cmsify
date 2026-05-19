using System.Security.Cryptography;
using System.Text;

namespace Cmsify.Infrastructure.BackgroundServices;

public static class WebhookSigner
{
    public static string Sign(string secret, ReadOnlySpan<byte> payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(payload.ToArray());
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
