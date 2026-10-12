using System.Security.Cryptography;
using System.Text.Json;
using Cmsify.Core.EmbeddedMedia;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

internal static class EmbeddedPhotoManifestSerialization
{
    // Versioned field order and invariant JSON are independent of HTTP/host serializer options.
    internal static string Hash(Guid workspace, Guid asset, int revision, string sourceSha, string policy,
        string processor, EmbeddedPhotoInstructions instructions, IReadOnlyList<EmbeddedPhotoVariantDescriptor> variants)
    {
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject(); json.WriteNumber("version", 1);
            json.WriteString("workspace", workspace); json.WriteString("asset", asset); json.WriteNumber("revision", revision);
            json.WriteString("sourceSha256", sourceSha.ToUpperInvariant()); json.WriteString("policy", policy);
            json.WriteString("processor", processor); json.WriteNumber("quarterTurnsClockwise", instructions.QuarterTurnsClockwise);
            json.WriteStartArray("variants");
            foreach (var variant in variants)
            {
                json.WriteStartObject(); json.WriteString("key", variant.VariantKey); json.WriteString("contentType", variant.ContentType);
                json.WriteNumber("width", variant.Width); json.WriteNumber("height", variant.Height);
                json.WriteNumber("length", variant.Length); json.WriteString("sha256", variant.Sha256.ToUpperInvariant()); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(output.ToArray()));
    }
    internal static string Intent(params object?[] fields) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
}
