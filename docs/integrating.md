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
- Workspace responses include an actor-specific `canWrite` capability. Treat it as a UI hint; the API remains the authorization authority for workspace updates and deletion.
- JSON uses camel-case property names and string enum values, such as `"Editor"`, `"Write"`, `"Draft"`, and `"Text"`.
- List responses are paginated; send `page` and `pageSize` and follow the returned totals.
- Non-success responses use RFC 7807 `application/problem+json` with a Cmsify error type and a `traceId` extension.
- Mutable resources use `ETag` and require a matching `If-Match` value when the endpoint enforces optimistic concurrency.
- Respect `429` responses and `Retry-After`; the .NET client behavior described below handles this automatically, while direct HTTP consumers must implement it themselves.
- Send and store timestamps as UTC/ISO 8601 values.

## Webhook consumers

Webhook delivery is at least once. Persist `X-Cmsify-Event-Id` and deduplicate by that stable event identity; retries reuse it. Verify `X-Cmsify-Signature` against the exact received request bytes using the endpoint secret. Non-success responses and transport failures are retried with exponential backoff until the configured maximum, then remain available to operators as dead letters. Cmsify revalidates the configured destination before every attempt.

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

For delegated credentials, set `TokenProvider` instead of `ApiToken`. Direct construction and `AddCmsifyClient` use the same single shared resilience pipeline; Admin also passes one pipeline to its scoped clients and does not add a retry handler beneath them. This prevents stacked retries.

Automatic replay is deliberately conservative: only `GET`, `HEAD`, and `OPTIONS` are eligible. `POST`, `PUT`, `PATCH`, `DELETE`, multipart uploads, and arbitrary or caller-owned stream bodies remain single-attempt. Every eligible retry rebuilds the request, repeatable JSON body, token lookup, and correlation ID. Cmsify uses the shared defaults `408`, `429`, `500`, `502`, `503`, and `504`, plus `Transport` (`HttpRequestException`) and non-caller `Timeout` failures. The shared package exposes configurable status and exception-category sets, validates exception categories as only `Transport`/`Timeout`, and uses the same frozen classifier for retry and the logical-request circuit breaker; Cmsify intentionally does not override those defaults. All resilience settings—including `EnableRetries`, attempt/time budgets, circuit settings, classifier sets, and callbacks—are construction-time snapshots, so later options mutation cannot change an existing client. Both delta and HTTP-date `Retry-After` values are honored; invalid, expired, or non-positive values fall back to bounded jittered exponential backoff.

`RequestTimeout` is a hard monotonic logical deadline across request factories, senders, observers, attempts, and waits, while the underlying `HttpClient.Timeout` remains infinite. Every positive finite duration is supported even beyond the platform's single-timer range, and `Timeout.InfiniteTimeSpan` keeps an unbounded deadline. A non-cooperative factory, sender, or observer is raced against cancellation/deadline, so caller cancellation or `HttpRequestTimeoutException` returns promptly. Late work is observed and retains owned request/response state until its once-only observer and best-effort cleanup finish; it cannot mutate retry, timeout, or circuit state a second time. Caller cancellation wins before circuit entry and every terminal mapping, retains the exact caller token, and is never retried or counted by the circuit. Budget exhaustion, final transport failure, circuit-open state, and final HTTP responses remain distinguishable; final HTTP responses still become `CmsifyApiException` with ProblemDetails extensions, trace/correlation IDs, and existing ETag/JSON behavior.

The response observer runs before classification for every received response. The thread-safe circuit breaker evaluates one completed logical request outside retry, maintains the configured rolling sample window, admits one half-open probe, and excludes caller cancellation and observer failures entirely from throughput and state changes. Retry, logical-budget timeout, and circuit callbacks are best-effort and expose only bounded safe fields—never bodies, credentials, tokens, query values, URLs, or raw exception messages. `OnTimeout` is emitted exactly once on budget expiry with pipeline name, `Timeout` category, and configured budget; callback or cleanup failures cannot replace the request outcome or caller cancellation. Safe downloads may retry only before the final response is handed to the copy stage; copy failures are not replayed or appended. See the [client package README](../sdk/dotnet/src/SyntaxCircus.Cmsify.Client/README.md) and [focused sample](../examples/dotnet/CmsifyClientSample.cs).

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
