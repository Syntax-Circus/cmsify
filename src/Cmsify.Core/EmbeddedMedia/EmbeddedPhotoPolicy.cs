namespace Cmsify.Core.EmbeddedMedia;

public static class EmbeddedPhotoPolicy
{
    public const long MaxBytes = 20L * 1024 * 1024;
    public const int MaxEdge = 8192;
    public const long MaxPixels = 24_000_000;
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
