using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace SyntaxCircus.Cmsify;

internal sealed class DistributedCmsifyContentCacheStore(IDistributedCache cache) : ICmsifyContentCacheStore
{
    private static readonly DistributedCacheEntryOptions GenerationOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30)
    };

    public async Task<T?> GetOrCreateAsync<T>(CmsifyContentCacheKey key, string keyPrefix, string partition, TimeSpan absoluteExpiration, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken)
    {
        try
        {
            var generation = await GetGenerationAsync(key.WorkspaceId, keyPrefix, partition, cancellationToken).ConfigureAwait(false);
            var bytes = await cache.GetAsync(EntryKey(key, keyPrefix, partition, generation), cancellationToken).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                var cached = JsonSerializer.Deserialize<T>(bytes);
                if (cached is not null)
                {
                    return cached;
                }
            }
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            // The CMS remains available when the optional cache provider is unavailable or holds an invalid entry.
        }

        var value = await factory(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return value;
        }

        try
        {
            var generation = await GetGenerationAsync(key.WorkspaceId, keyPrefix, partition, cancellationToken).ConfigureAwait(false);
            await cache.SetAsync(
                EntryKey(key, keyPrefix, partition, generation),
                JsonSerializer.SerializeToUtf8Bytes(value),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = absoluteExpiration },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            // A failed cache write must not fail an otherwise successful CMS request.
        }

        return value;
    }

    public async Task RemoveAsync(CmsifyContentCacheKey key, string keyPrefix, string partition, CancellationToken cancellationToken)
    {
        try
        {
            var generation = await GetGenerationAsync(key.WorkspaceId, keyPrefix, partition, cancellationToken).ConfigureAwait(false);
            await cache.RemoveAsync(EntryKey(key, keyPrefix, partition, generation), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            // Invalidations are best effort for an unavailable optional cache.
        }
    }

    public async Task RemoveWorkspaceAsync(Guid workspaceId, string keyPrefix, string partition, CancellationToken cancellationToken)
    {
        try
        {
            await cache.SetStringAsync(GenerationKey(workspaceId, keyPrefix, partition), Guid.NewGuid().ToString("N"), GenerationOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            // Invalidations are best effort for an unavailable optional cache.
        }
    }

    private async Task<string> GetGenerationAsync(Guid workspaceId, string keyPrefix, string partition, CancellationToken cancellationToken)
    {
        var key = GenerationKey(workspaceId, keyPrefix, partition);
        var generation = await cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(generation))
        {
            return generation;
        }

        generation = Guid.NewGuid().ToString("N");
        await cache.SetStringAsync(key, generation, GenerationOptions, cancellationToken).ConfigureAwait(false);
        return generation;
    }

    private static string EntryKey(CmsifyContentCacheKey key, string keyPrefix, string partition, string generation) => $"{key.ToCacheKey(keyPrefix, partition)}:generation:{generation}";
    private static string GenerationKey(Guid workspaceId, string keyPrefix, string partition) => $"{keyPrefix}:generation:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(partition))).ToLowerInvariant()}:{workspaceId:N}";
    private static bool IsCacheFailure(Exception exception) => exception is not OperationCanceledException;
}

public static class CmsifyContentDistributedCacheServiceCollectionExtensions
{
    /// <summary>Registers the opt-in cached content facade backed by the application's IDistributedCache implementation.</summary>
    public static IServiceCollection AddCmsifyContentDistributedCache(this IServiceCollection services, Action<CmsifyContentCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        services.AddSingleton<ICmsifyContentCacheStore, DistributedCmsifyContentCacheStore>();
        services.AddTransient<ICachedCmsifyContentClient, CachedCmsifyContentClient>();
        services.AddTransient<ICmsifyContentCacheInvalidator, CachedCmsifyContentClient>();
        return services;
    }
}
