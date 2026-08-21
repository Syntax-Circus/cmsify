# Integrating with Cmsify

Cmsify is an authenticated, workspace-scoped API. There is no anonymous public content-delivery surface in the MVP, so a public website should fetch content in its server/runtime layer and render or proxy the result.

For credential selection, bearer-token lifecycle, and workspace authorization, see [Authentication and authorization](authentication-and-authorization.md).

## Authentication choices

For machine-to-machine integrations, create an API client in the admin UI and send its token on every request:

```http
Authorization: Bearer cmsify_your_token
```

API clients have a role, optional expiry, and optional workspace scope. Use a workspace-scoped `Reader` client for a read-only website. Use a broader role only when the integration actually performs mutations. Local user session tokens and optional OIDC/JWT bearer tokens are also accepted for their intended interactive or identity-provider workflows.

## Base URL and workspace

API routes are versioned under `/api/v1`. Most content, template, media, webhook, and package routes include a workspace GUID:

```text
https://cms.example.com/api/v1/workspaces/{workspaceId}/content
```

The workspace slug is useful for discovery and configuration, but requests to workspace-scoped endpoints use the workspace ID. A caller without access commonly receives `404` to avoid disclosing another workspace.

## REST conventions

- Use Swagger at `/swagger` or `/swagger/v1/swagger.json` for the complete request/response reference.
- List responses are paginated; send `page` and `pageSize` and follow the returned totals.
- Non-success responses use RFC 7807 `application/problem+json` with a Cmsify error type and a `traceId` extension.
- Mutable resources use `ETag` and require a matching `If-Match` value when the endpoint enforces optimistic concurrency.
- Respect `429` responses and `Retry-After`; the published client handles this automatically.
- Send and store timestamps as UTC/ISO 8601 values.

## TypeScript client

The repository contains `@cmsify/client`, generated from the API OpenAPI document and wrapped with typed helpers for workspaces, content, templates, media, health, and authentication. Its npm publication is pending; use the package from `sdk/typescript` during repository development. See [`sdk/typescript/README.md`](../sdk/typescript/README.md) for installation, examples, retries, ETags, and regeneration.

Example server-side configuration:

```ts
import { CmsifyClient } from "@cmsify/client";

const cms = new CmsifyClient({
  baseUrl: process.env.CMSIFY_API_URL!,
  apiToken: process.env.CMSIFY_API_TOKEN!,
  workspace: process.env.CMSIFY_WORKSPACE!,
});

const posts = await cms.content.list({
  templateSlug: "blog-post",
  status: "Published",
  pageSize: 10,
});
```

Keep `CMSIFY_API_TOKEN` in a server-only secret namespace. The existing examples show the correct pattern for Next.js App Router, Astro, and SvelteKit.

## .NET client and NuGet packages

The first-party .NET SDK is split into two packages so API hosts and consumers share the same wire contracts:

- `SyntaxCircus.Cmsify.Contracts` — request/response records, enums, pagination, and JSON options.
- `SyntaxCircus.Cmsify.Client` — an `HttpClient`-based management client built on those contracts.

Install the client (it targets .NET 10):

```powershell
dotnet add package SyntaxCircus.Cmsify.Client
```

Register it with dependency injection:

```csharp
builder.Services.AddCmsifyClient(options =>
{
    options.BaseUrl = new Uri(builder.Configuration["Cmsify:BaseUrl"]!);
    options.ApiToken = builder.Configuration["Cmsify:ApiToken"];
});
```

For delegated credentials, set `TokenProvider` instead of `ApiToken`. The client forwards correlation IDs, retries transient failures and `429 Retry-After`, serializes enum values as strings, and throws `CmsifyApiException` containing ProblemDetails for non-success responses. See the [.NET SDK README](../sdk/dotnet/README.md) and [focused sample](../examples/dotnet/CmsifyClientSample.cs).

### Cached content reads

Content caching is opt-in and applies only through `ICachedCmsifyContentClient`; the existing `CmsifyClient.Content` methods always remain live API calls. For a single application instance, register the in-memory backend after registering the client:

```csharp
builder.Services.AddCmsifyContentMemoryCache(options =>
{
    options.CachePartitionProvider = _ => ValueTask.FromResult("website-reader");
    options.DefaultAbsoluteExpiration = TimeSpan.FromMinutes(5);
});
```

The partition must be stable and non-secret, and must distinguish authorization audiences when using per-user or rotating credentials. Cached reads use absolute expiration only; individual calls can supply `CmsifyContentCacheEntryOptions`. Use `ICmsifyContentCacheInvalidator.RemoveAsync(CmsifyContentCacheKeys.Get(...))` for an exact entry or `RemoveWorkspaceAsync` to bust a workspace.

For Redis or another shared cache, install `SyntaxCircus.Cmsify.Client.DistributedCaching`, configure an `IDistributedCache` provider in the host, and call `AddCmsifyContentDistributedCache` instead. The add-on stores JSON values, is provider-neutral, and fails open to a live Cmsify request when the cache is unavailable.

## Direct HTTP smoke test

When diagnosing an integration, make one authenticated request outside the application:

```powershell
$headers = @{ Authorization = "Bearer $env:CMSIFY_API_TOKEN" }
Invoke-RestMethod -Uri "$env:CMSIFY_API_URL/api/v1/workspaces/$env:CMSIFY_WORKSPACE/content?page=1&pageSize=10" -Headers $headers
```

If this fails, inspect the returned ProblemDetails before debugging framework code.
