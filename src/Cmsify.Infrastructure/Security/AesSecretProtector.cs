using System.Security.Cryptography;
using System.Text;
using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;

namespace Cmsify.Infrastructure.Security;

public sealed class AesSecretProtector : ISecretProtector
{
    private readonly byte[] key;

    public AesSecretProtector(IConfiguration configuration)
    {
        var configuredKey = configuration["Secrets:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            throw new InvalidOperationException("Secrets:EncryptionKey is required for webhook secret encryption.");
        }

        key = DeriveKey(configuredKey);
    }

    public string Protect(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(ciphertext)}";
    }

    public string Unprotect(string protectedSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSecret);
        var parts = protectedSecret.Split('.');
        if (parts.Length != 4 || parts[0] != "v1")
        {
            throw new InvalidOperationException("Webhook secret is not in a supported encrypted format.");
        }

        var nonce = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var ciphertext = Convert.FromBase64String(parts[3]);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string configuredKey)
    {
        if (Convert.TryFromBase64String(configuredKey, new byte[32], out var bytesWritten) && bytesWritten == 32)
        {
            return Convert.FromBase64String(configuredKey);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
    }
}
