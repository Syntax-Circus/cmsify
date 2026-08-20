# SyntaxCircus.Cmsify.Client.DistributedCaching

Provider-neutral `IDistributedCache` support for cached Cmsify content reads. The host configures the provider, such as Redis; this package does not include a Redis client.

```csharp
services.AddStackExchangeRedisCache(options => options.Configuration = configuration["Redis:Configuration"]);
services.AddCmsifyContentDistributedCache(options =>
{
    options.CachePartitionProvider = _ => ValueTask.FromResult("public-site");
});
```

Resolve `ICachedCmsifyContentClient` for cached reads and `ICmsifyContentCacheInvalidator` to remove an exact generated key or all entries for a workspace. Entries use absolute expiration only and cache failures fall back to the Cmsify API.
