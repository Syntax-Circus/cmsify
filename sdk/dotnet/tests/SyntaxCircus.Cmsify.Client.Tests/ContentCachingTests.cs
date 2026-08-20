using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace SyntaxCircus.Cmsify.Client.Tests;

public sealed class ContentCachingTests
{
    [Fact]
    public void GeneratedKeys_VaryByRequestShapeAndPartitionWithoutExposingPartition()
    {
        var workspaceId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var unresolved = CmsifyContentCacheKeys.Get(workspaceId, itemId).ToCacheKey("cmsify:content", "reader-a");
        var resolved = CmsifyContentCacheKeys.Get(workspaceId, itemId, resolve: true).ToCacheKey("cmsify:content", "reader-a");
        var otherPartition = CmsifyContentCacheKeys.Get(workspaceId, itemId).ToCacheKey("cmsify:content", "reader-b");

        unresolved.ShouldNotBe(resolved);
        unresolved.ShouldNotBe(otherPartition);
        unresolved.ShouldNotContain("reader-a");
    }

    [Fact]
    public async Task MemoryCache_HitsAndSupportsExactAndWorkspaceBusting()
    {
        var calls = 0;
        using var provider = CreateMemoryProvider(_ => Json(Content(++calls)));
        var cached = provider.GetRequiredService<ICachedCmsifyContentClient>();
        var invalidator = provider.GetRequiredService<ICmsifyContentCacheInvalidator>();
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        (await cached.GetAsync(workspaceId, contentId))!.Slug.ShouldBe("post-1");
        (await cached.GetAsync(workspaceId, contentId))!.Slug.ShouldBe("post-1");
        calls.ShouldBe(1);

        await invalidator.RemoveAsync(CmsifyContentCacheKeys.Get(workspaceId, contentId));
        (await cached.GetAsync(workspaceId, contentId))!.Slug.ShouldBe("post-2");
        calls.ShouldBe(2);

        await invalidator.RemoveWorkspaceAsync(workspaceId);
        (await cached.GetAsync(workspaceId, contentId))!.Slug.ShouldBe("post-3");
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task MemoryCache_UsesPerCallAbsoluteExpiry()
    {
        var calls = 0;
        using var provider = CreateMemoryProvider(_ => Json(Content(++calls)));
        var cached = provider.GetRequiredService<ICachedCmsifyContentClient>();
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();
        var entryOptions = new CmsifyContentCacheEntryOptions { AbsoluteExpiration = TimeSpan.FromMilliseconds(10) };

        await cached.GetAsync(workspaceId, contentId, cacheOptions: entryOptions);
        await Task.Delay(50);
        await cached.GetAsync(workspaceId, contentId, cacheOptions: entryOptions);

        calls.ShouldBe(2);
    }

    [Fact]
    public async Task DistributedCache_HitsAndWorkspaceBustChangesGeneration()
    {
        var calls = 0;
        var cache = new DictionaryDistributedCache();
        using var provider = CreateDistributedProvider(cache, _ => Json(Content(++calls)));
        var cached = provider.GetRequiredService<ICachedCmsifyContentClient>();
        var invalidator = provider.GetRequiredService<ICmsifyContentCacheInvalidator>();
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        await cached.GetAsync(workspaceId, contentId);
        await cached.GetAsync(workspaceId, contentId);
        calls.ShouldBe(1);

        await invalidator.RemoveWorkspaceAsync(workspaceId);
        await cached.GetAsync(workspaceId, contentId);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task DistributedCache_FailsOpenWhenProviderIsUnavailable()
    {
        var calls = 0;
        var cache = new DictionaryDistributedCache { ThrowOnAccess = true };
        using var provider = CreateDistributedProvider(cache, _ => Json(Content(++calls)));
        var cached = provider.GetRequiredService<ICachedCmsifyContentClient>();
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        await cached.GetAsync(workspaceId, contentId);
        await cached.GetAsync(workspaceId, contentId);

        calls.ShouldBe(2);
    }

    private static ServiceProvider CreateMemoryProvider(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var services = CreateServices(handler);
        services.AddCmsifyContentMemoryCache(ConfigureCache);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateDistributedProvider(IDistributedCache cache, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var services = CreateServices(handler);
        services.AddSingleton(cache);
        services.AddCmsifyContentDistributedCache(ConfigureCache);
        return services.BuildServiceProvider();
    }

    private static ServiceCollection CreateServices(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new CmsifyClient(new HttpClient(new StubHandler(handler)), new CmsifyClientOptions { BaseUrl = new Uri("https://cms.test") }));
        return services;
    }

    private static void ConfigureCache(CmsifyContentCacheOptions options) => options.CachePartitionProvider = _ => ValueTask.FromResult("test-reader");

    private static ContentItemDetailResponse Content(int call) => new(Guid.NewGuid(), Guid.NewGuid(), "post", ContentStatus.Published, $"post-{call}", null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    private sealed class DictionaryDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        public bool ThrowOnAccess { get; init; }

        public byte[]? Get(string key) => Access(() => entries.TryGetValue(key, out var value) ? value : null);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) => Access(() => { });
        public Task RefreshAsync(string key, CancellationToken token = default) { Refresh(key); return Task.CompletedTask; }
        public void Remove(string key) => Access(() => entries.TryRemove(key, out _));
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Access(() => entries[key] = value);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { Set(key, value, options); return Task.CompletedTask; }

        private T Access<T>(Func<T> action)
        {
            if (ThrowOnAccess) throw new InvalidOperationException("Cache unavailable");
            return action();
        }

        private void Access(Action action)
        {
            if (ThrowOnAccess) throw new InvalidOperationException("Cache unavailable");
            action();
        }
    }
}
