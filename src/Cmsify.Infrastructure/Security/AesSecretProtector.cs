using System.Security.Cryptography;
using System.Text;
using Cmsify.Core.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace Cmsify.Infrastructure.Security;

public sealed class AesSecretProtector : ISecretProtector
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly string activeKeyId;
    private readonly byte[] activeKey;
    private readonly IReadOnlyDictionary<string, byte[]> encryptionKeys;
    private readonly string? legacyEncryptionKey;

    public AesSecretProtector(IOptions<SecretProtectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (!SecretProtectionOptionsValidator.TryDecodeCanonicalKey(value.EncryptionKeys.GetValueOrDefault(value.ActiveKeyId), out activeKey))
        {
            throw new InvalidOperationException("Webhook secret protection configuration is invalid.");
        }

        activeKeyId = value.ActiveKeyId;
        encryptionKeys = value.EncryptionKeys.ToDictionary(
            entry => entry.Key,
            entry => DecodeKey(entry.Value),
            StringComparer.Ordinal);
        legacyEncryptionKey = value.EncryptionKey;
    }

    public string Protect(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(activeKey, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(activeKeyId));

        return $"v2.{activeKeyId}.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(ciphertext)}";
    }

    public string Unprotect(string protectedSecret)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(protectedSecret);
            var parts = protectedSecret.Split('.', StringSplitOptions.None);
            if (parts[0] is not "v1" and not "v2")
            {
                throw new SecretDecryptFailureException(SecretDecryptFailureReason.UnknownVersion);
            }

            return parts switch
            {
                ["v1", var nonce, var tag, var ciphertext] => DecryptV1(nonce, tag, ciphertext),
                ["v2", var keyId, var nonce, var tag, var ciphertext] => DecryptV2(keyId, nonce, tag, ciphertext),
                _ => throw new SecretDecryptFailureException(SecretDecryptFailureReason.MalformedCiphertext),
            };
        }
        catch (SecretDecryptFailureException)
        {
            throw;
        }
        catch (FormatException exception)
        {
            throw new SecretDecryptFailureException(SecretDecryptFailureReason.MalformedCiphertext, exception);
        }
        catch (ArgumentException exception) when (exception is not ArgumentNullException)
        {
            throw new SecretDecryptFailureException(SecretDecryptFailureReason.MalformedCiphertext, exception);
        }
    }

    private string DecryptV1(string nonceValue, string tagValue, string ciphertextValue)
    {
        if (string.IsNullOrWhiteSpace(legacyEncryptionKey))
        {
            throw new SecretDecryptFailureException(SecretDecryptFailureReason.Configuration);
        }

        var nonce = DecodeSegment(nonceValue, NonceLength);
        var tag = DecodeSegment(tagValue, TagLength);
        var ciphertext = DecodeSegment(ciphertextValue, minimumLength: 1);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(DeriveLegacyKey(legacyEncryptionKey), tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException exception)
        {
            throw new SecretDecryptFailureException(SecretDecryptFailureReason.Authentication, exception);
        }
        return Encoding.UTF8.GetString(plaintext);
    }

    private string DecryptV2(string keyId, string nonceValue, string tagValue, string ciphertextValue)
    {
        if (!IsKeyId(keyId) || !encryptionKeys.TryGetValue(keyId, out var key))
        {
            throw new SecretDecryptFailureException(SecretDecryptFailureReason.UnknownKey);
        }

        var nonce = DecodeSegment(nonceValue, NonceLength);
        var tag = DecodeSegment(tagValue, TagLength);
        var ciphertext = DecodeSegment(ciphertextValue, minimumLength: 1);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(keyId));
        }
        catch (CryptographicException exception)
        {
            throw new SecretDecryptFailureException(SecretDecryptFailureReason.Authentication, exception);
        }
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DecodeKey(string configuredKey)
    {
        if (SecretProtectionOptionsValidator.TryDecodeCanonicalKey(configuredKey, out var key))
        {
            return key;
        }

        throw new InvalidOperationException("Webhook secret protection configuration is invalid.");
    }

    private static byte[] DeriveLegacyKey(string configuredKey)
    {
        if (Convert.TryFromBase64String(configuredKey, new byte[32], out var bytesWritten) && bytesWritten == 32)
        {
            return Convert.FromBase64String(configuredKey);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
    }

    private static byte[] DecodeSegment(string value, int? exactLength = null, int? minimumLength = null)
    {
        var decoded = Convert.FromBase64String(value);
        if (!string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal)
            || (exactLength.HasValue && decoded.Length != exactLength.Value)
            || (minimumLength.HasValue && decoded.Length < minimumLength.Value))
        {
            throw new SecretDecryptFailureException(SecretDecryptFailureReason.MalformedCiphertext);
        }

        return decoded;
    }

    private static byte[] AssociatedData(string keyId) => Encoding.UTF8.GetBytes($"v2.{keyId}");

    private static bool IsKeyId(string keyId) =>
        keyId.Length is >= 1 and <= 64 && keyId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
