using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Cmsify.Api.Auth;

public static class TokenUtility
{
    public static string GenerateSessionToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));

    public static string GenerateApiToken() => $"cmsify_{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48))}";

    public static string Sha256Hash(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
