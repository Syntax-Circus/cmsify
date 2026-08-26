using System.Security.Cryptography;

namespace Cmsify.Infrastructure.Security;

internal enum SecretDecryptFailureReason
{
    UnknownVersion,
    UnknownKey,
    Configuration,
    MalformedCiphertext,
    Authentication
}

internal sealed class SecretDecryptFailureException(SecretDecryptFailureReason reason, Exception? innerException = null)
    : CryptographicException("Webhook secret could not be decrypted.", innerException)
{
    public SecretDecryptFailureReason Reason { get; } = reason;
}
