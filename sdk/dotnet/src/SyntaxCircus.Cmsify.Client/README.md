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

The client also exposes templates, media, picklists, tags, webhooks, audit, users, API clients, settings, packages, authentication, and health services. Requests attach bearer authentication and correlation IDs, map RFC 7807 failures to `CmsifyApiException`, and preserve ETags for mutation concurrency. Mutating methods also accept an optional trailing `ifMatch` parameter when callers need to provide an ETag explicitly.

## HTTP resilience contract

Every `CmsifyClient` uses exactly one `SyntaxCircus.Http.Resilience.HttpRequestResiliencePipeline`, whether the client is directly constructed, created by `AddCmsifyClient`, or scoped by Cmsify Admin. `GET`, `HEAD`, and `OPTIONS` are replayable when `EnableRetries` is true. `POST`, `PUT`, `PATCH`, and `DELETE` are never replayed automatically, even when they carry `If-Match`; multipart uploads and arbitrary or caller-owned stream content are also single-attempt. `MaxRetryAttempts` retains its historical name but is the total attempt count, including the initial attempt. Setting `EnableRetries` to `false` selects one attempt while preserving the request-timeout budget. All resilience settings—including `EnableRetries`, attempt and timeout values, circuit settings, and telemetry callbacks—are captured when the client/pipeline is constructed; mutating the supplied options object later does not change an existing client's replay fencing or policy.

Each attempt receives a newly constructed `HttpRequestMessage`, repeatable JSON content where applicable, a fresh token lookup through `TokenProvider` (or the current `ApiToken`), and a new `X-Correlation-Id`. Cmsify uses the shared pipeline defaults: retryable status codes `408`, `429`, `500`, `502`, `503`, and `504`, plus the `Transport` (`HttpRequestException`) and `Timeout` (handler timeout that is not caller cancellation) exception categories. The underlying package allows callers that construct their own pipeline to replace both classifier sets, validates exception categories as only `Transport` or `Timeout`, and snapshots both sets at pipeline construction; Cmsify intentionally provides no classifier override. Retry and circuit classification use the same snapshot. A valid `Retry-After` delta or HTTP date is used without jitter; invalid, non-positive, or expired values fall back to bounded exponential backoff plus jitter. Delays are clamped to both the configured maximum and the remaining total budget.

`RequestTimeout` is one hard monotonic logical-request deadline covering request construction, every attempt, every observer, and every retry wait. Every positive finite value is supported, including durations beyond the platform's single-timer range; `Timeout.InfiniteTimeSpan` maps to an unbounded deadline. The underlying `HttpClient.Timeout` is infinite so a second timer cannot race it. Budget exhaustion returns `HttpRequestTimeoutException` promptly while both a delegate's synchronous blocking prefix and its returned asynchronous work are raced against the deadline, and no attempt begins when the remaining budget is zero. Scheduled sender and observer delegates receive stable non-null request/response snapshots, so terminal ownership transfer cannot change queued arguments. Late work is observed: a late response's complete observer invocation—including its synchronous prefix and asynchronous tail—is queued before user code can run, terminal cancellation or timeout does not wait for it, the observer still runs once, owned request/response state remains alive until that work completes, and cleanup is best-effort. Caller cancellation is checked before circuit entry and before every terminal mapping, is never retried or counted by the circuit, and propagates as `OperationCanceledException` carrying the caller's exact token. Exhausted transport retries surface the final transport exception, an open circuit throws `HttpCircuitOpenException`, and a final HTTP response still reaches the existing `CmsifyApiException`/ProblemDetails mapping.

`OnRetry` receives only pipeline name, attempt number, optional status, fixed failure category, and bounded delay. `OnTimeout` runs exactly once when the total logical budget expires and receives only pipeline name, the `Timeout` failure category, and configured budget. The thread-safe circuit breaker guards one complete logical request outside retry, uses the same frozen classifier, admits one half-open probe, and excludes caller cancellation and observer-failure tunnels from both throughput and state changes. Its locked completion check is the terminal linearization point: cancellation observed before the sample/state mutation—including cancellation initiated while the injected completion timestamp is acquired—wins with the exact caller token and no breaker effect. Once that mutation commits, cancellation first initiated by the subsequent circuit-state callback is post-terminal and cannot replace the committed response, failure, or timeout. Circuit callbacks remain outside the breaker lock; no rollback is attempted. `OnCircuitStateChanged` receives only pipeline name, open/half-open/closed state, optional status, and fixed failure category. None of these callbacks receives request or response bodies, credentials, raw exception messages, tokens, or URLs/query values. Callback failures are non-fatal and cannot replace success, the final failure, timeout, or caller cancellation.

`ResponseObserver` runs once for every received response, including intermediate retry responses, before retry classification, disposal, deserialization, ETag caching, or ProblemDetails mapping. If it fails, that failure is preserved and no retry or circuit failure is recorded. Request and intermediate-response cleanup is always attempted best-effort; a throwing `Dispose` cannot replace observer failure, timeout, or caller cancellation. The pipeline disposes every request and discarded intermediate response; ownership of the final response transfers to `CmsifyClient`, which disposes it after handling. Only the final ETag is cached.

Use `Media.DownloadToAsync` or `Packages.ExportToAsync` to stream large files directly to a destination stream. The existing `DownloadAsync` and `ExportAsync` methods retain their byte-array convenience behavior. A safe download may retry only before the final response is returned to the copy stage. Once content copying starts, a stream failure is surfaced without replaying or appending to the destination.

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
