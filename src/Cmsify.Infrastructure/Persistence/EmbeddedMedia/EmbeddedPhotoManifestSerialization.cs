using System.Security.Cryptography;
using System.Text.Json;
using Cmsify.Core.EmbeddedMedia;

namespace Cmsify.Infrastructure.Persistence.EmbeddedMedia;

internal static class EmbeddedPhotoManifestSerialization
{
    private const int PngCicpManifestVersion = 4;
    internal static bool ValidVersionedManifest(PhotoManifestRecord manifest, IReadOnlyList<PhotoVariantRecord> variants)
    {
        if (manifest.PolicyId is not (EmbeddedPhotoPolicy.PngNormalizationPolicyId or EmbeddedPhotoPolicy.JpegSdrBasePolicyId or EmbeddedPhotoPolicy.PngCicpSdrPolicyId)) return true;
        if (manifest.MetadataJson is null) return false;
        EmbeddedPhotoSourceMetadata? metadata;
        try { metadata = JsonSerializer.Deserialize<EmbeddedPhotoSourceMetadata>(manifest.MetadataJson); }
        catch (JsonException) { return false; }
        if (metadata is null || !EmbeddedPhotoPolicy.ValidMetadata(metadata, manifest.PolicyId)) return false;
        var requirements = EmbeddedPhotoPolicy.VariantsFor(manifest.PolicyId);
        if (variants.Count != requirements.Count || requirements.Any(r => variants.Count(v => v.VariantKey == r.VariantKey) != 1)) return false;
        var ordered = requirements.Select(r => variants.Single(v => v.VariantKey == r.VariantKey))
            .Select(v => new EmbeddedPhotoVariantDescriptor(v.VariantKey, v.ContentType, v.Width, v.Height, v.Length, v.Sha256)).ToArray();
        return string.Equals(manifest.ManifestSha256, Hash(manifest.WorkspaceId, manifest.AssetId, manifest.Revision,
            manifest.SourceSha256, manifest.PolicyId, manifest.ProcessorVersion, new(manifest.QuarterTurnsClockwise), ordered, metadata), StringComparison.OrdinalIgnoreCase);
    }
    // Versioned field order and invariant JSON are independent of HTTP/host serializer options.
    internal static string Hash(Guid workspace, Guid asset, int revision, string sourceSha, string policy,
        string processor, EmbeddedPhotoInstructions instructions, IReadOnlyList<EmbeddedPhotoVariantDescriptor> variants,
        EmbeddedPhotoSourceMetadata? metadata = null)
    {
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            var png = policy == EmbeddedPhotoPolicy.PngNormalizationPolicyId;
            var jpeg = policy == EmbeddedPhotoPolicy.JpegSdrBasePolicyId;
            var cicp = policy == EmbeddedPhotoPolicy.PngCicpSdrPolicyId;
            json.WriteStartObject(); json.WriteNumber("version", cicp ? PngCicpManifestVersion : png ? 2 : jpeg ? 3 : 1);
            json.WriteString("workspace", workspace); json.WriteString("asset", asset); json.WriteNumber("revision", revision);
            json.WriteString("sourceSha256", sourceSha.ToUpperInvariant()); json.WriteString("policy", policy);
            json.WriteString("processor", processor); json.WriteNumber("quarterTurnsClockwise", instructions.QuarterTurnsClockwise);
            if (png || jpeg || cicp)
            {
                ArgumentNullException.ThrowIfNull(metadata);
                json.WriteStartObject("source");
                json.WriteString("format", metadata.Format); json.WriteNumber("width", metadata.Width); json.WriteNumber("height", metadata.Height);
                json.WriteNumber("selectedBitDepth", metadata.BitDepth); json.WriteString("color", metadata.ColorDescription);
                json.WriteBoolean("alpha", metadata.HasAlpha); json.WriteBoolean("sdrBase", metadata.UsedSdrBase);
                json.WriteString("conversion", metadata.Conversion!.Version); json.WriteNumber("originalBitDepth", metadata.Conversion.OriginalBitDepth);
                json.WriteEndObject();
            }
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
