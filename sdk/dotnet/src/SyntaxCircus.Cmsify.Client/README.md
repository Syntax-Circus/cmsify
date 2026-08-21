# SyntaxCircus.Cmsify.Client

Typed .NET client for connecting to and managing Cmsify. The package uses the shared `SyntaxCircus.Cmsify.Contracts` wire models and supports both direct construction and Microsoft dependency injection.

```powershell
dotnet add package SyntaxCircus.Cmsify.Client
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Cmsify;

services.AddCmsifyClient(options =>
{
    options.BaseUrl = new Uri("https://cms.example.com");
    options.ApiToken = configuration["Cmsify:ApiToken"];
});

var posts = await cms.Content.ListAsync(
    workspaceId,
    new ContentListQuery(null, null, null, ContentStatus.Published, null, null, null, "featured", null, null, null, null, false, null, "publishedAt", true, 1, 10),
    cancellationToken);
```

The client also exposes templates, media, picklists, tags, webhooks, audit, users, API clients, settings, packages, authentication, and health services. Requests attach bearer authentication and correlation IDs, map RFC 7807 failures to `CmsifyApiException`, and preserve ETags for mutation concurrency. Retries apply only to safe read requests; writes are never replayed automatically. Mutating methods also accept an optional trailing `ifMatch` parameter when callers need to provide an ETag explicitly.

Use `Media.DownloadToAsync` or `Packages.ExportToAsync` to stream large files directly to a destination stream. The existing `DownloadAsync` and `ExportAsync` methods retain their byte-array convenience behavior.

Use `DownloadWithMetadataAsync` or `Packages.ExportWithMetadataAsync` when the response filename and content type are needed alongside the bytes. Media uploads accept an optional `IProgress<long>` for sent-byte progress. Consumers that need to inspect response headers can set `CmsifyClientOptions.ResponseObserver`; it runs for each received response before retry, deserialization, or error mapping.

## Cached content reads

Caching is opt-in and leaves `CmsifyClient.Content` unchanged. Register the in-memory cached facade and use a stable, non-secret partition to keep authorization audiences isolated:

```csharp
services.AddCmsifyContentMemoryCache(options =>
{
    options.CachePartitionProvider = _ => ValueTask.FromResult("public-site");
    options.DefaultAbsoluteExpiration = TimeSpan.FromMinutes(5);
});
```

Resolve `ICachedCmsifyContentClient` for cached `GetAsync`, `BySlugAsync`, `ListAsync`, and `ListAllAsync` calls. Per-call `CmsifyContentCacheEntryOptions` can override the absolute expiry. Resolve `ICmsifyContentCacheInvalidator` to remove a key from `CmsifyContentCacheKeys` or bust all cached content for a workspace. For Redis or another distributed provider, install `SyntaxCircus.Cmsify.Client.DistributedCaching`, configure the host's `IDistributedCache`, and register `AddCmsifyContentDistributedCache` instead of the in-memory backend.

Use `CmsifyClientOptions.TokenProvider` for rotating or request-time credentials. Keep tokens in server-side secret storage and never send them to browser code.

Treat API tokens as opaque bearer credentials. Newly issued tokens use a `cmsify_<identifier>_<secret>` shape, but applications must not parse or reconstruct them.

Webhook create and update calls validate destinations before sending: URLs must use HTTPS, omit embedded credentials, and cannot use unsafe literal IP addresses. Hostname DNS checks remain enforced by the server.

The shared `SyntaxCircus.Cmsify.Contracts` package contains the public request, response, enum, pagination, and dynamic JSON models without the HTTP client implementation.
