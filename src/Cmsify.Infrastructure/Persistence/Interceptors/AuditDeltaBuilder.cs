using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cmsify.Infrastructure.Persistence.Interceptors;

public static class AuditDeltaBuilder
{
    public const string RedactedPrefix = "redacted:";

    /// <summary>
    /// Property names whose values are secrets and must never be written verbatim to the audit log
    /// (User.PasswordHash, WebhookEndpoint.Secret; TokenHash is listed defensively although ApiClient
    /// and UserSession are not audited). The scrub migration uses the same list.
    /// </summary>
    public static IReadOnlySet<string> SensitivePropertyNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "PasswordHash",
        "Secret",
        "TokenHash"
    };

    /// <summary>Non-reversible marker: "redacted:" + first 12 lowercase hex chars of SHA-256(UTF-8 value).</summary>
    public static string Fingerprint(string value)
        => RedactedPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    private static object? Redact(object? value)
        => value is string text ? Fingerprint(text) : value;

    public static JsonElement? Build(EntityEntry entry)
    {
        var changes = new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsShadowProperty() && property.Metadata.Name == "xmin")
            {
                continue;
            }

            var sensitive = SensitivePropertyNames.Contains(property.Metadata.Name);
            var current = sensitive ? Redact(property.CurrentValue) : property.CurrentValue;
            var original = sensitive ? Redact(property.OriginalValue) : property.OriginalValue;

            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Added)
            {
                changes[property.Metadata.Name] = new { after = current };
            }
            else if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Deleted)
            {
                changes[property.Metadata.Name] = new { before = original };
            }
            else if (property.IsModified)
            {
                changes[property.Metadata.Name] = new
                {
                    before = original,
                    after = current
                };
            }
        }

        if (changes.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(changes);
    }
}
