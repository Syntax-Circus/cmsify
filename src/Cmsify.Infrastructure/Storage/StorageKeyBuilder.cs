using System.Text.RegularExpressions;

namespace Cmsify.Infrastructure.Storage;

public static partial class StorageKeyBuilder
{
    public static string Build(Guid workspaceId, Guid assetId, string fileName, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var safeFileName = UnsafeFileNameCharacters().Replace(Path.GetFileName(fileName), "-");
        return $"cmsify/media/{workspaceId}/{timestamp.Year:0000}/{timestamp.Month:00}/{assetId}_{safeFileName}";
    }

    public static string Build(string fileName, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var safeFileName = UnsafeFileNameCharacters().Replace(Path.GetFileName(fileName), "-");
        return Path.Combine("default", timestamp.Year.ToString("0000"), timestamp.Month.ToString("00"), $"{Guid.CreateVersion7()}_{safeFileName}")
            .Replace('\\', '/');
    }

    [GeneratedRegex("[^a-zA-Z0-9._-]+")]
    private static partial Regex UnsafeFileNameCharacters();
}
