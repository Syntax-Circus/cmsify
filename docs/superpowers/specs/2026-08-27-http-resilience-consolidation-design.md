# HTTP resilience consolidation design

**Date:** 2026-08-27  
**Remediation scope:** Task 10 / F-13  
**Status:** Approved design; implementation not started

## Purpose

Cmsify currently implements retry behavior inside the .NET client while `SyntaxCircus.Http.Resilience` exposes a separate DI-only policy. The result is duplicated policy, no reusable direct-client path, incomplete transport/timeout behavior, and a risk that Admin can stack two retry layers.

Task 10 will add one reusable request-factory resilience pipeline to `SyntaxCircus.Http.Resilience`, consume that pipeline exactly once per `CmsifyClient`, and preserve Cmsify's public HTTP contracts: authentication, correlation IDs, response observation, ProblemDetails exceptions, ETags, JSON, streaming, and conservative method replay rules.

The shared package will be packed as an exact local `0.2.0` prerelease and consumed from an ignored local feed for development and validation. Package publication remains an explicit user-owned gate. Cmsify will not push, tag, publish, or release the package during this task.

## Goals

- Give directly constructed and DI-created clients the same retry, timeout, circuit, and telemetry behavior.
- Build a fresh `HttpRequestMessage` for every attempt instead of cloning or re-sending a consumed request.
- Honor both delta-seconds and HTTP-date `Retry-After` values.
- Retry bounded transient responses, transport faults, and handler timeouts without retrying caller cancellation.
- Apply jittered exponential backoff only when the server did not provide a usable delay.
- Enforce one total logical-request timeout budget across attempts and delays.
- Keep method/body replayability explicit and conservative.
- Expose bounded retry/circuit telemetry without response bodies, request bodies, tokens, URLs with query values, or raw exception messages.
- Preserve Cmsify exception mapping, correlation IDs, response observers, ETags, downloads, uploads, and cancellation behavior.
- Remove Cmsify's duplicate retry loops and prevent Admin from adding a second policy.

## Non-goals

- Changing the TypeScript client.
- Changing API rate limits, idempotency semantics, or adding an API idempotency-key store.
- Retrying multipart uploads or replaying arbitrary/stream-backed content.
- Retrying response-body copy failures after response headers have been returned.
- Adding hedging, endpoint discovery, load balancing, caching, or webhook-delivery resilience.
- Replacing Cmsify's ProblemDetails model or public exception types.
- Rebasing `CmsifyClient` on the package's `ApiClientBase`.
- Publishing any NuGet package.

## Shared package architecture

### Request-factory pipeline

`SyntaxCircus.Http.Resilience` will expose a reusable pipeline with a call shape equivalent to:

```csharp
Task<HttpResponseMessage> SendAsync(
    Func<int, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
    Func<HttpRequestMessage, HttpCompletionOption, CancellationToken, Task<HttpResponseMessage>> sender,
    HttpCompletionOption completionOption,
    HttpRequestReplaySafety replaySafety,
    Func<HttpResponseMessage, CancellationToken, ValueTask>? responseObserver,
    CancellationToken cancellationToken);
```

The exact public names may follow the package's naming conventions, but these responsibilities and ownership rules are fixed:

- `requestFactory` receives the one-based attempt number and creates a new request for that attempt.
- The pipeline disposes every request and every response it discards for retry.
- The successful or final non-retryable response is returned to the caller, which owns disposal.
- The response observer runs once for every received response before retry classification or disposal.
- The sender is supplied by the consumer, so the same pipeline works with an injected `HttpClient`, a directly owned `HttpClient`, or a deterministic test sender.
- The pipeline never buffers a request body or guesses that an arbitrary body can be replayed.

### Configuration

An immutable options value will define:

- total maximum attempts, including the initial attempt;
- total logical-request timeout;
- exponential backoff base and maximum delay;
- injectable `TimeProvider` and jitter source for deterministic tests;
- retryable status codes and exception categories;
- circuit-breaker failure ratio, minimum throughput, sampling duration, and break duration;
- bounded retry, circuit-state, and timeout callbacks.

Invalid counts, durations, ratios, or callback limits fail during construction. The package will keep its existing `AddResilientHttpClient` public surface source-compatible. Internally, shared decision/configuration helpers will prevent the legacy DI helper and the new request-factory pipeline from drifting. A new DI registration helper will register a configured pipeline without adding a retrying `HttpMessageHandler` automatically.

### Replay safety

The package will make replay safety an explicit input for each logical request. Its generic default classifier may support broader RFC-idempotent use, but Cmsify will select this policy:

- automatic replay: `GET`, `HEAD`, and `OPTIONS` with a fresh request factory;
- no automatic replay: `POST`, `PUT`, `PATCH`, and `DELETE`;
- no replay for multipart, caller-owned stream, or otherwise non-repeatable bodies;
- future unsafe-method replay requires an explicit idempotency marker plus a request factory known to create repeatable content.

This intentionally preserves Cmsify's existing write-safety boundary even though some HTTP methods are nominally idempotent. ETag-protected writes can produce ambiguous outcomes after a lost response and will remain single-attempt.

## Retry, timeout, and circuit semantics

The default transient set is `408`, `429`, `500`, `502`, `503`, and `504`, plus `HttpRequestException` and timeout caused by the pipeline's own budget. Caller cancellation is never retryable.

For a retryable response:

1. Parse `Retry-After` as either delta seconds or an HTTP date.
2. Ignore invalid, negative, or already-expired values.
3. Use a valid server delay without adding jitter.
4. Otherwise use exponential backoff plus bounded jitter.
5. Clamp against the configured maximum delay and remaining total request budget.
6. If no positive budget remains, surface the logical-request timeout instead of starting another attempt.

The pipeline will use one linked budget cancellation source for the logical request and will not rely on competing per-attempt `HttpClient.Timeout` timers. Directly owned and DI-created `HttpClient` instances used by Cmsify will therefore use an infinite client timeout; the shared pipeline owns the configured total budget.

Outcomes remain distinguishable:

- caller cancellation propagates as cancellation using the caller token;
- exhausted total budget surfaces as a documented timeout exception;
- exhausted transport retries surface the final transport exception;
- an open circuit surfaces a documented circuit-open exception with no raw response/body data;
- a final HTTP response is returned to Cmsify for its existing ProblemDetails mapping.

The circuit counts the same bounded transient categories as retry, but retry eligibility still respects replay safety. Circuit telemetry uses only client/pipeline name, attempt or circuit state, HTTP status when present, a fixed fault category, and bounded delay. Raw exception messages, request/response bodies, credentials, and query values are forbidden telemetry fields.

## Cmsify .NET client integration

`SyntaxCircus.Cmsify.Client` will reference the locally packed prerelease and create or receive one shared pipeline per client configuration:

- `new CmsifyClient(options)` creates its owned `HttpClient` and one private pipeline;
- `new CmsifyClient(httpClient, options)` uses the supplied client and one pipeline built from those options;
- `AddCmsifyClient(...)` registers the `HttpClient`, configured pipeline, and typed client together;
- Admin's scoped construction resolves the same registered pipeline configuration while retaining circuit-safe scoped token/session callbacks on the `CmsifyClient` itself.

`CmsifyClientOptions` keeps its existing retry and timeout properties source-compatible. `MaxRetryAttempts` retains its current meaning as total attempts and maps to the shared pipeline. New optional telemetry/circuit configuration will be additive. `EnableRetries=false` produces one attempt while retaining the total timeout budget.

The client will replace manual `for`/`Task.Delay` retry loops with one pipeline invocation. Its request factory will rebuild, for every attempt:

- URI, method, Accept header, and repeatable JSON content;
- current bearer token from `TokenProvider` or `ApiToken`;
- a fresh correlation ID;
- explicit or tracked `If-Match` values.

The response observer runs for every actual response before retry or final handling, preserving sliding-session metadata behavior. Only the final response enters JSON/ProblemDetails handling and ETag caching. Intermediate response ETags are not cached.

Downloads may retry only before a response is returned to the copy stage. Once headers have been accepted and content copying begins, a stream failure is surfaced and the destination is not silently replayed or appended. Multipart uploads remain single-attempt. No handler beneath `CmsifyClient` will also retry.

## Admin integration

Admin will keep the named `CmsifyApi` transport and its OIDC/circuit-specific `HttpClient` factory, but will register the shared pipeline configuration once and pass it to each scoped `CmsifyClient`. The named client will not add `AddResilienceHandler`, `AddStandardResilienceHandler`, or another retrying handler.

Token acquisition and session-expiry observation remain in the scoped client so pooled handler scopes never capture per-user state. Concurrent Blazor circuits must retain isolated bearer tokens and observers across every retry attempt.

## Package and restore boundary

The sibling package changes will be committed independently on a non-`main` feature branch. The package will be packed as an exact local prerelease in the `0.2.0` line, with package SHA-256 recorded. No publish script or registry command will run.

Cmsify will pin that exact prerelease in `Directory.Packages.props` and restore it from an ignored, workspace-local feed. No sibling source project reference will be added. Documentation and the remediation handoff will state that public/CI restore remains blocked until the user publishes the corresponding stable package; after publication, Cmsify must replace the prerelease pin with the exact stable version and rerun restore/build/tests.

## Testing strategy

### Shared package

- Direct construction and DI-resolved pipeline parity.
- Delta/date/invalid/past `Retry-After` handling.
- Exponential backoff, deterministic jitter, maximum delay, and remaining-budget clamps.
- Retryable HTTP statuses, transport faults, handler timeout, caller cancellation, and open-circuit behavior.
- Conservative method/body replay classification and fresh request/auth material per attempt.
- Intermediate request/response disposal and final-response ownership.
- Circuit open/half-open/close telemetry with bounded safe fields.
- Existing `AddResilientHttpClient`, `ApiClientBase`, ProblemDetails, and token-cache regressions.
- A clean temporary consumer referencing only the packed `.nupkg`, exercising both direct and DI registration.

### Cmsify

- Direct and `AddCmsifyClient` parity for attempts, delays, timeout, and final outcomes.
- Response observer invocation for every received response.
- Fresh token and correlation ID per attempt without cross-circuit leakage.
- `CmsifyApiException`, ProblemDetails extensions, trace/correlation IDs, ETags, empty bodies, and JSON behavior unchanged.
- `Retry-After`, transport, timeout-budget, caller-cancellation, and circuit telemetry behavior.
- No replay for POST/PUT/DELETE, multipart upload, or stream-copy failure.
- Safe download retry before headers/final copy, with no destination duplication.
- Admin integration coverage for token forwarding, session observation, direct/DI parity, and exactly one retry layer.

### Required validation

- Sibling Release build, full package tests, pack, package-content inspection, SHA-256, and clean-consumer tests.
- Cmsify .NET client tests and Admin integration tests.
- Full solution Release build/test when central package references change.
- Documentation/link checks and `git diff --check` in both repositories.
- Exact recorded commands and environment limitations in the handoff.

## Error handling and compatibility

The implementation is additive at the shared-package API boundary and source-compatible at Cmsify's public options/constructors. Any unavoidable exception-type change for total timeout or circuit-open state must be documented and covered as an explicit public contract before package packing.

No retry may hide a final HTTP response that Cmsify must translate. Intermediate response observers may have side effects, so their once-per-response order is a tested contract. Disposal or telemetry callback failure must not leak response/request bodies or replace caller cancellation; callback failure policy will be fail-safe and documented in the implementation plan.

## Acceptance criteria

- One shared package pipeline drives both direct and DI Cmsify clients.
- Cmsify contains no manual retry/delay loop and Admin adds no second retry handler.
- Every retry gets a fresh request, token lookup, correlation ID, and repeatable body.
- Conservative Cmsify method/body replay rules are preserved.
- Delta/date `Retry-After`, transport faults, jitter, total timeout budget, circuit telemetry, and caller cancellation are proven deterministically.
- Cmsify ProblemDetails exceptions, correlations, response observation, ETags, streaming, and uploads remain correct.
- The exact locally packed prerelease is consumed by package reference, not sibling source.
- Shared package, clean-consumer, SDK, Admin, and full solution checks pass.
- Publication remains unperformed and explicitly gated on the user.
