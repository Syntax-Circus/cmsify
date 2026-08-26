using Cmsify.Infrastructure.Security;

namespace Cmsify.Infrastructure.BackgroundServices;

internal sealed record WebhookSecretDecryptDiagnostic(string Version, string KeyId, string Reason)
{
    public static WebhookSecretDecryptDiagnostic FromTypedFailure(
        string ciphertext,
        SecretDecryptFailureException exception,
        IEnumerable<string> configuredKeyIds) =>
        Create(ciphertext, ToReason(exception.Reason), configuredKeyIds);

    public static WebhookSecretDecryptDiagnostic Create(string ciphertext, string reason, IEnumerable<string> configuredKeyIds)
    {
        var configured = configuredKeyIds.ToHashSet(StringComparer.Ordinal);
        var segments = ciphertext.Split('.', StringSplitOptions.None);
        var version = segments.Length > 0 && segments[0] is "v1" or "v2" ? segments[0] : "unknown";
        var keyId = version == "v2" && segments.Length > 1 && configured.Contains(segments[1]) ? segments[1] : "unknown";
        return new WebhookSecretDecryptDiagnostic(version, keyId, reason switch
        {
            "configuration" => "configuration",
            "unknown_version" => "unknown_version",
            "unknown_key" => "unknown_key",
            "malformed_ciphertext" => "malformed_ciphertext",
            "authentication" => "authentication",
            _ => "unknown"
        });
    }

    private static string ToReason(SecretDecryptFailureReason reason) => reason switch
    {
        SecretDecryptFailureReason.UnknownVersion => "unknown_version",
        SecretDecryptFailureReason.UnknownKey => "unknown_key",
        SecretDecryptFailureReason.Configuration => "configuration",
        SecretDecryptFailureReason.MalformedCiphertext => "malformed_ciphertext",
        SecretDecryptFailureReason.Authentication => "authentication",
        _ => "unknown"
    };
}
