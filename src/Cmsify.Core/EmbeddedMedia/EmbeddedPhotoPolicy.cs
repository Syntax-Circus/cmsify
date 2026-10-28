namespace Cmsify.Core.EmbeddedMedia;

public static class EmbeddedPhotoPolicy
{
    public const long MaxBytes = 20L * 1024 * 1024;
    public const long MaximumSupportedOriginalBytes = 64L * 1024 * 1024;
    public static bool ValidOriginalLimit(long value) => value > 0 && value <= MaximumSupportedOriginalBytes;
    public const int MaxEdge = 8192;
    public const long MaxPixels = 24_000_000;
    public const string PngNormalizationPolicyId = "png-sdr-webp-v1";
    public const string PngNormalizationVersion = "png-q8-srgb-strip-v1";
    public const string PngCicpSdrPolicyId = "png-cicp-sdr-webp-v1";
    public const string PngCicpSdrVersion = "png-cicp-p3-q8-srgb-strip-v1";
    public const string JpegSdrBasePolicyId = "jpeg-sdr-base-webp-v1";
    public const string JpegSdrBaseVersion = "apple-jpeg-sdr-base-q8-srgb-strip-v1";
    public const long PngMaxPixels = 30_000_000;
    private static readonly IReadOnlyList<EmbeddedPhotoVariantRequirement> PngVariants = Array.AsReadOnly(
        new[] { new EmbeddedPhotoVariantRequirement("320", 320, 85), new("640", 640, 85), new("1280", 1280, 85), new("4096", 4096, 85) });
    // Unknown/custom policy IDs retain the historical contract. Only this explicit opt-in changes it.
    public static IReadOnlyList<EmbeddedPhotoVariantRequirement> VariantsFor(string policyId) =>
        policyId is PngNormalizationPolicyId or PngCicpSdrPolicyId ? PngVariants : RequiredVariants;
    public static bool IsVariantKey(string key) => PngVariants.Any(x => x.VariantKey == key);
    public static bool ValidMetadata(EmbeddedPhotoSourceMetadata source, string policyId) =>
        ValidateDimensions(source.Width, source.Height, policyId) && source.BitDepth == 8 &&
        !string.IsNullOrWhiteSpace(source.ColorDescription) && source.ColorDescription.Length <= 256 &&
        (policyId is PngNormalizationPolicyId or PngCicpSdrPolicyId
            ? source.Format == "PNG" && source.ColorDescription == "sRGB" && !source.UsedSdrBase &&
              source.Conversion is { OriginalBitDepth: 8 or 16 } &&
              source.Conversion.Version == (policyId == PngCicpSdrPolicyId ? PngCicpSdrVersion : PngNormalizationVersion)
            : policyId == JpegSdrBasePolicyId
                ? source.Format == "JPEG" && source.ColorDescription == "sRGB" && source.UsedSdrBase && !source.HasAlpha &&
                  source.Conversion is { Version: JpegSdrBaseVersion, OriginalBitDepth: 8 }
                : source.Format is "JPEG" or "PNG" or "WebP" or "HEIC" && source.Conversion is null);
    public static bool ValidateDimensions(int width, int height, string policyId) =>
        width > 0 && height > 0 && width <= MaxEdge && height <= MaxEdge &&
        (long)width * height <= (policyId is PngNormalizationPolicyId or PngCicpSdrPolicyId ? PngMaxPixels : MaxPixels);
    public const string WebpContentType = "image/webp";
    public const int Quality = 80;
    public const int AutomaticAttempts = 3;
    public const int MaxReferenceManifests = 1000;
    public const int DeadlineSeconds = 30;
    public const long MemoryLimitBytes = 512L * 1024 * 1024;
    public const int PidLimit = 128;
    public static IReadOnlyList<EmbeddedPhotoVariantRequirement> RequiredVariants { get; } = Array.AsReadOnly(
        new[] { new EmbeddedPhotoVariantRequirement("320", 320, Quality), new("640", 640, Quality), new("1280", 1280, Quality) });
    public static bool ValidateInstructions(EmbeddedPhotoInstructions instructions) => instructions.QuarterTurnsClockwise is >= 0 and <= 3;
    public static bool ValidateDimensions(int width, int height) =>
        width > 0 && height > 0 && width <= MaxEdge && height <= MaxEdge && (long)width * height <= MaxPixels;
    public static (int Width, int Height) Fit(int width, int height, int bound)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bound, 1);
        var scale = Math.Min(1d, (double)bound / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
    }
    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed record EmbeddedPhotoVariantRequirement(string VariantKey, int Bound, int Quality);
public sealed record EmbeddedPhotoPolicyIdentity(string PolicyId, string ProcessorVersion);
