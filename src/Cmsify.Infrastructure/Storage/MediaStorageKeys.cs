namespace Cmsify.Infrastructure.Storage;

internal static class MediaStorageKeys
{
    internal const string PhotoPrefix = "cmsify/media/photos/";

    // One portable relative spelling is required for ownership and physical deletion.
    internal static bool IsCanonical(string key) => !string.IsNullOrWhiteSpace(key) &&
        !key.Contains('\\') && !key.Contains(':') && !key.Contains('\0') &&
        key.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".." &&
            !segment.EndsWith('.') && !segment.EndsWith(' '));

    internal static bool IsPhoto(string key) => key.StartsWith(PhotoPrefix, StringComparison.OrdinalIgnoreCase);
}
