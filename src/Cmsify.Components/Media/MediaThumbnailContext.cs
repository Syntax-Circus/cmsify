using System.Collections.Concurrent;

namespace SyntaxCircus.Cmsify.Components;

/// <summary>
/// How media thumbnails turn a <see cref="MediaAssetResponse"/> into something an <c>&lt;img&gt;</c> can show.
/// The API's <see cref="MediaAssetResponse.Url"/> is relative to Cmsify and its file endpoint needs a bearer
/// token, so a browser <c>&lt;img src&gt;</c> cannot use it directly from an embedding host. Supplied to the
/// editor components through <see cref="MediaThumbnailScope"/>.
/// </summary>
public sealed class MediaThumbnailContext
{
    /// <summary>Default ceiling for inlining an image as a data URL (2 MiB).</summary>
    public const long DefaultMaxInlineBytes = 2 * 1024 * 1024;

    private const int MaxConcurrentDownloads = 4;
    private readonly SemaphoreSlim gate = new(MaxConcurrentDownloads);
    private readonly ConcurrentDictionary<(Guid Id, DateTimeOffset UpdatedAt), Task<string?>> cache = new();

    public MediaThumbnailContext(
        CmsifyClient? client, Guid workspaceId, Func<MediaAssetResponse, string?>? urlResolver = null,
        long maxInlineBytes = DefaultMaxInlineBytes)
    {
        Client = client;
        WorkspaceId = workspaceId;
        UrlResolver = urlResolver;
        MaxInlineBytes = maxInlineBytes;
    }

    public CmsifyClient? Client { get; }
    public Guid WorkspaceId { get; }

    /// <summary>Opt-in. Maps an asset to the URL the browser should load (absolute, proxied, CDN...). Null means "no image".</summary>
    public Func<MediaAssetResponse, string?>? UrlResolver { get; }

    public long MaxInlineBytes { get; }

    /// <summary>True when thumbnails can be fetched through the authenticated client.</summary>
    public bool CanLoad => Client is not null && WorkspaceId != Guid.Empty;

    /// <summary>
    /// Downloads an image asset through the authenticated client and returns it as a <c>data:</c> URL, or null when
    /// it is not an image, is too large to inline, or cannot be fetched. Results are cached per asset version, and
    /// concurrent downloads are throttled so a large grid cannot flood the API.
    /// </summary>
    public async Task<string?> LoadDataUrlAsync(MediaAssetResponse asset, CancellationToken cancellationToken = default)
    {
        if (!CanLoad || !asset.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || asset.SizeBytes > MaxInlineBytes)
        {
            return null;
        }

        var key = (asset.Id, asset.UpdatedAt);
        var task = cache.GetOrAdd(key, _ => DownloadAsync(asset));
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            cache.TryRemove(key, out _); // let a later render retry a transient failure
            return null;
        }
    }

    private async Task<string?> DownloadAsync(MediaAssetResponse asset)
    {
        await gate.WaitAsync();
        try
        {
            var bytes = await Client!.Media.DownloadAsync(WorkspaceId, asset.Id);
            return $"data:{asset.MimeType};base64,{Convert.ToBase64String(bytes)}";
        }
        finally
        {
            gate.Release();
        }
    }
}
