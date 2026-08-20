using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SyntaxCircus.Cmsify.Contracts;

namespace SyntaxCircus.Cmsify;

/// <summary>Configures opt-in caching for Cmsify content reads.</summary>
public sealed class CmsifyContentCacheOptions
{
    /// <summary>The absolute expiry used when a request does not override it.</summary>
    public TimeSpan DefaultAbsoluteExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A non-secret prefix that isolates Cmsify entries from other cache users.</summary>
    public string KeyPrefix { get; set; } = "cmsify:content";

    /// <summary>Returns a stable, non-secret identifier for the authorization audience of the current request.</summary>
    public Func<CancellationToken, ValueTask<string>>? CachePartitionProvider { get; set; }
}

/// <summary>Overrides the caching behavior for a single content read.</summary>
public sealed class CmsifyContentCacheEntryOptions
{
    /// <summary>The absolute expiry for this entry. Sliding expiration is intentionally not supported.</summary>
    public TimeSpan? AbsoluteExpiration { get; set; }
}

/// <summary>A logical, deterministic key for a cached Cmsify content response.</summary>
public sealed record CmsifyContentCacheKey(Guid WorkspaceId, string Value)
{
    public string ToCacheKey(string keyPrefix, string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        return $"{keyPrefix}:{Encode(partition)}:{WorkspaceId:N}:{Value}";
    }

    private static string Encode(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Builds logical cache keys for the cached content facade.</summary>
public static class CmsifyContentCacheKeys
{
    public static CmsifyContentCacheKey Get(Guid workspaceId, Guid id, bool resolve = false, DateTimeOffset? asOf = null) =>
        new(workspaceId, $"item:{id:N}:resolve={resolve}:as-of={asOf?.ToUniversalTime().ToString("O") ?? "none"}");

    public static CmsifyContentCacheKey BySlug(Guid workspaceId, string slug, DateTimeOffset? asOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return new(workspaceId, $"slug:{Encode(slug)}:as-of={asOf?.ToUniversalTime().ToString("O") ?? "none"}");
    }

    public static CmsifyContentCacheKey List(Guid workspaceId, ContentListQuery? query) =>
        new(workspaceId, $"list:{Encode(query is null ? "<default>" : JsonSerializer.Serialize(query))}");

    private static string Encode(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Invalidates cached Cmsify content without making a CMS API request.</summary>
public interface ICmsifyContentCacheInvalidator
{
    Task RemoveAsync(CmsifyContentCacheKey key, CancellationToken cancellationToken = default);
    Task RemoveWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>Provides opt-in cached read operations for Cmsify content.</summary>
public interface ICachedCmsifyContentClient
{
    Task<PagedResponse<ContentItemSummaryResponse>?> ListAsync(Guid workspaceId, ContentListQuery? query = null, CmsifyContentCacheEntryOptions? cacheOptions = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ContentItemSummaryResponse> ListAllAsync(Guid workspaceId, ContentListQuery? query = null, CmsifyContentCacheEntryOptions? cacheOptions = null, CancellationToken cancellationToken = default);
    Task<ContentItemDetailResponse?> GetAsync(Guid workspaceId, Guid id, bool resolve = false, DateTimeOffset? asOf = null, CmsifyContentCacheEntryOptions? cacheOptions = null, CancellationToken cancellationToken = default);
    Task<ContentItemDetailResponse?> BySlugAsync(Guid workspaceId, string slug, DateTimeOffset? asOf = null, CmsifyContentCacheEntryOptions? cacheOptions = null, CancellationToken cancellationToken = default);
}

/// <summary>Backend contract implemented by the in-memory client package and distributed add-on package.</summary>
public interface ICmsifyContentCacheStore
{
    Task<T?> GetOrCreateAsync<T>(CmsifyContentCacheKey key, string keyPrefix, string partition, TimeSpan absoluteExpiration, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken);
    Task RemoveAsync(CmsifyContentCacheKey key, string keyPrefix, string partition, CancellationToken cancellationToken);
    Task RemoveWorkspaceAsync(Guid workspaceId, string keyPrefix, string partition, CancellationToken cancellationToken);
}

public sealed class CachedCmsifyContentClient(CmsifyClient client, ICmsifyContentCacheStore cacheStore, IOptions<CmsifyContentCacheOptions> options) : ICachedCmsifyContentClient, ICmsifyContentCacheInvalidator
{
    public Task<PagedResponse<ContentItemSummaryResponse>?> ListAsync(Guid workspaceId, ContentListQuery? query = null, CmsifyContentCacheEntryOptions? cacheOptions = null, CancellationToken cancellationToken = default) =>
        GetOrCreateAsync(CmsifyContentCacheKeys.List(workspaceId, query), cacheOptions, ct => client.Content.ListAsync(workspaceId, query, ct), cancellationToken);

    public async IAsyncEnumerable<ContentItemSummaryResponse> ListAllAsync(Guid workspaceId, ContentListQuery? query = null, CmsifyContentCacheEntryOptions? cacheOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var page = 1; ; page++)
        {
            var pageQuery = query is null
                ? new ContentListQuery(null, null, null, null, null, null, null, null, null, null, null, null, false, null, "createdAt", true, page, 20)
                : query with { Page = page };
            var result = await ListAsync(workspaceId, pageQuery, cacheOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cmsify returned an empty page response.");
            foreach (var item in result.Items)
            {
                yield return item;
            }

            if (page >= result.TotalPages || result.Items.Count == 0)
            {
                yield break;
            }
        }
    }

    public Task<ContentItemDetailResponse?> GetAsync(Guid workspaceId, Guid id, bool resolve = false, DateTimeOffset? asOf = null, CmsifyContentCacheEntryOptions? cacheOptions = null, CancellationToken cancellationToken = default) =>
        GetOrCreateAsync(CmsifyContentCacheKeys.Get(workspaceId, id, resolve, asOf), cacheOptions, ct => client.Content.GetAsync(workspaceId, id, resolve, asOf, ct), cancellationToken);

    public Task<ContentItemDetailResponse?> BySlugAsync(Guid workspaceId, string slug, DateTimeOffset? asOf = null, CmsifyContentCacheEntryOptions? cacheOptions = null, CancellationToken cancellationToken = default) =>
        GetOrCreateAsync(CmsifyContentCacheKeys.BySlug(workspaceId, slug, asOf), cacheOptions, ct => client.Content.BySlugAsync(workspaceId, slug, asOf, ct), cancellationToken);

    public async Task RemoveAsync(CmsifyContentCacheKey key, CancellationToken cancellationToken = default)
    {
        var (settings, partition) = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        await cacheStore.RemoveAsync(key, settings.KeyPrefix, partition, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var (settings, partition) = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        await cacheStore.RemoveWorkspaceAsync(workspaceId, settings.KeyPrefix, partition, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetOrCreateAsync<T>(CmsifyContentCacheKey key, CmsifyContentCacheEntryOptions? cacheOptions, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken)
    {
        var (settings, partition) = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var expiry = cacheOptions?.AbsoluteExpiration ?? settings.DefaultAbsoluteExpiration;
        if (expiry <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheOptions), "Absolute cache expiration must be greater than zero.");
        }

        return await cacheStore.GetOrCreateAsync(key, settings.KeyPrefix, partition, expiry, factory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(CmsifyContentCacheOptions Settings, string Partition)> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.DefaultAbsoluteExpiration <= TimeSpan.Zero)
        {
            throw new OptionsValidationException(nameof(CmsifyContentCacheOptions), typeof(CmsifyContentCacheOptions), ["DefaultAbsoluteExpiration must be greater than zero."]);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(settings.KeyPrefix);
        var provider = settings.CachePartitionProvider ?? throw new OptionsValidationException(nameof(CmsifyContentCacheOptions), typeof(CmsifyContentCacheOptions), ["CachePartitionProvider is required to prevent authorization cache sharing."]);
        var partition = await provider(cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        return (settings, partition);
    }
}

internal sealed class MemoryCmsifyContentCacheStore(IMemoryCache cache) : ICmsifyContentCacheStore
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> workspaceTokens = new(StringComparer.Ordinal);

    public async Task<T?> GetOrCreateAsync<T>(CmsifyContentCacheKey key, string keyPrefix, string partition, TimeSpan absoluteExpiration, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken)
    {
        var cacheKey = key.ToCacheKey(keyPrefix, partition);
        if (cache.TryGetValue(cacheKey, out T? existing))
        {
            return existing;
        }

        var value = await factory(cancellationToken).ConfigureAwait(false);
        if (value is not null)
        {
            var token = workspaceTokens.GetOrAdd(WorkspaceTokenKey(key, keyPrefix, partition), _ => new CancellationTokenSource());
            cache.Set(cacheKey, value, new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(absoluteExpiration)
                .AddExpirationToken(new CancellationChangeToken(token.Token)));
        }

        return value;
    }

    public Task RemoveAsync(CmsifyContentCacheKey key, string keyPrefix, string partition, CancellationToken cancellationToken)
    {
        cache.Remove(key.ToCacheKey(keyPrefix, partition));
        return Task.CompletedTask;
    }

    public Task RemoveWorkspaceAsync(Guid workspaceId, string keyPrefix, string partition, CancellationToken cancellationToken)
    {
        if (workspaceTokens.TryRemove(WorkspaceTokenKey(workspaceId, keyPrefix, partition), out var token))
        {
            token.Cancel();
            token.Dispose();
        }

        return Task.CompletedTask;
    }

    private static string WorkspaceTokenKey(CmsifyContentCacheKey key, string keyPrefix, string partition) => WorkspaceTokenKey(key.WorkspaceId, keyPrefix, partition);
    private static string WorkspaceTokenKey(Guid workspaceId, string keyPrefix, string partition) => $"{keyPrefix}:{partition}:{workspaceId:N}";
}

public static class CmsifyContentMemoryCacheServiceCollectionExtensions
{
    /// <summary>Registers the opt-in in-memory cached content facade.</summary>
    public static IServiceCollection AddCmsifyContentMemoryCache(this IServiceCollection services, Action<CmsifyContentCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddMemoryCache();
        services.Configure(configure);
        services.AddSingleton<ICmsifyContentCacheStore, MemoryCmsifyContentCacheStore>();
        services.AddTransient<ICachedCmsifyContentClient, CachedCmsifyContentClient>();
        services.AddTransient<ICmsifyContentCacheInvalidator, CachedCmsifyContentClient>();
        return services;
    }
}
