# HTTP Resilience Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Cmsify's duplicate manual retry loops with one locally packed `SyntaxCircus.Http.Resilience` request-factory pipeline shared by direct, DI, and Admin clients while preserving exceptions, correlations, ETags, streaming, and conservative replay safety.

**Architecture:** The sibling package owns a fresh-request-per-attempt pipeline, bounded retry/timeout/circuit behavior, safe telemetry, and DI registration. `CmsifyClient` owns request construction and final response interpretation but delegates all attempt orchestration to exactly one shared pipeline; Admin supplies scoped token/session callbacks without installing another retrying handler.

**Tech Stack:** .NET 10, C# 14, `Microsoft.Extensions.Http.Resilience`/Polly, `HttpClientFactory`, xUnit v3, Shouldly, NSubstitute, ASP.NET Core `WebApplicationFactory`, NuGet local packages.

**Spec:** `docs/superpowers/specs/2026-08-27-http-resilience-consolidation-design.md`

## Global Constraints

- Preserve all existing Cmsify branch work and public .NET SDK behavior except the explicitly documented timeout/circuit additions.
- Work on `SyntaxCircus.Http.Resilience` in an isolated non-`main` worktree; commit sibling and Cmsify changes independently.
- Pack exactly `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1`; consume the `.nupkg` through an ignored local feed and record its SHA-256.
- Do not add a sibling source project reference to Cmsify and do not commit local feed configuration or package bytes.
- Do not push, merge, tag, publish, release, or mutate NuGet/GitHub state.
- A fresh `HttpRequestMessage` is required for every attempt; never clone/re-send consumed arbitrary content.
- Cmsify automatically retries only `GET`, `HEAD`, and `OPTIONS`; `POST`, `PUT`, `PATCH`, `DELETE`, multipart, and stream-backed requests remain single-attempt.
- Retry only `408`, `429`, `500`, `502`, `503`, `504`, `HttpRequestException`, and pipeline-owned timeout; never retry caller cancellation.
- Valid delta/date `Retry-After` wins over backoff; all waits and attempts share one bounded logical-request budget.
- Telemetry is bounded and may contain only pipeline name, attempt/state, HTTP status, fixed failure category, and delay; never bodies, tokens, query values, environment data, or raw exception messages.
- Preserve `CmsifyApiException`, ProblemDetails extensions, trace/correlation IDs, response observer invocation for every received response, ETag tracking, empty-body behavior, downloads, and upload progress.
- Run one heavy .NET/Testcontainers process at a time and report environmental limits instead of skipping checks.

---

## File Map

### Sibling `SyntaxCircus.Http.Resilience`

- `src/SyntaxCircus.Http.Resilience/HttpRequestResilienceOptions.cs` — immutable validated retry, timeout, delay, circuit, time, jitter, and telemetry configuration.
- `src/SyntaxCircus.Http.Resilience/HttpResilienceTelemetry.cs` — bounded public enums/records for retry and circuit events.
- `src/SyntaxCircus.Http.Resilience/HttpResilienceExceptions.cs` — stable public total-budget and circuit-open exception types.
- `src/SyntaxCircus.Http.Resilience/HttpRequestResiliencePipeline.cs` — fresh-request execution, response/attempt ownership, budgets, retry, and circuit behavior.
- `src/SyntaxCircus.Http.Resilience/HttpRequestResilienceServiceCollectionExtensions.cs` — keyed DI registration without adding an implicit HTTP handler.
- `src/SyntaxCircus.Http.Resilience/ResilientHttpClientExtensions.cs` — preserve existing API and share transient-outcome classification.
- `tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResilienceOptionsTests.cs` — construction and validation.
- `tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResiliencePipelineTests.cs` — retry/budget/circuit/replay/disposal/telemetry behavior.
- `tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResilienceServiceCollectionExtensionsTests.cs` — direct/keyed-DI parity and registration isolation.
- `README.md` — public usage and safety contract.

### Cmsify

- `Directory.Packages.props` — exact local prerelease pin.
- `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj` — package reference.
- `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyClientOptions.cs` — additive circuit/telemetry options and mapping.
- `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyClient.cs` — exactly one pipeline layer per client path and fresh request factories; remove manual retry loops.
- `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/CmsifyClientTests.cs` — direct behavior and preserved contracts.
- `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/CmsifyClientDependencyInjectionTests.cs` — DI/direct parity and one-policy proof.
- `src/Cmsify.Admin/Program.cs` — singleton pipeline configuration plus scoped client callbacks; no retry handler.
- `tests/Cmsify.Admin.Integration.Tests/AdminHttpResilienceTests.cs` — token/session/circuit isolation and no double retries.
- `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/README.md`, `docs/integrating.md`, `docs/authentication-and-authorization.md`, `docs/v1-release-readiness.md`, `docs/v1-release-remediation-handoff.md` — public behavior, local prerelease gate, and evidence.

## Shared Interfaces

The sibling package tasks produce these exact public shapes; later tasks consume them unchanged:

```csharp
public enum HttpRequestReplaySafety { NotReplayable, Replayable }
public enum HttpResilienceFailureCategory { HttpStatus, Transport, Timeout, CircuitOpen }
public enum HttpResilienceCircuitState { Open, HalfOpen, Closed }

public sealed class HttpRequestTimeoutException : TimeoutException
{
    public HttpRequestTimeoutException(string pipelineName, TimeSpan timeout, Exception? innerException = null);
    public string PipelineName { get; }
    public TimeSpan Timeout { get; }
}

public sealed class HttpCircuitOpenException : HttpRequestException
{
    public HttpCircuitOpenException(string pipelineName, TimeSpan? retryAfter, Exception? innerException = null);
    public string PipelineName { get; }
    public TimeSpan? RetryAfter { get; }
}

public sealed record HttpRetryTelemetry(
    string PipelineName,
    int AttemptNumber,
    HttpStatusCode? StatusCode,
    HttpResilienceFailureCategory FailureCategory,
    TimeSpan Delay);

public sealed record HttpCircuitTelemetry(
    string PipelineName,
    HttpResilienceCircuitState State,
    HttpStatusCode? StatusCode,
    HttpResilienceFailureCategory FailureCategory);

public sealed class HttpRequestResilienceOptions
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan TotalRequestTimeout { get; init; } = TimeSpan.FromSeconds(100);
    public TimeSpan BackoffBaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);
    public double CircuitFailureRatio { get; init; } = 0.5;
    public int CircuitMinimumThroughput { get; init; } = 5;
    public TimeSpan CircuitSamplingDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Func<double> JitterProvider { get; init; } = Random.Shared.NextDouble;
    public Func<HttpRetryTelemetry, CancellationToken, ValueTask>? OnRetry { get; init; }
    public Func<HttpCircuitTelemetry, CancellationToken, ValueTask>? OnCircuitStateChanged { get; init; }
}

public sealed class HttpRequestResiliencePipeline
{
    public HttpRequestResiliencePipeline(string name, HttpRequestResilienceOptions options);

    public Task<HttpResponseMessage> SendAsync(
        Func<int, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        Func<HttpRequestMessage, HttpCompletionOption, CancellationToken, Task<HttpResponseMessage>> sender,
        HttpCompletionOption completionOption,
        HttpRequestReplaySafety replaySafety,
        Func<HttpResponseMessage, CancellationToken, ValueTask>? responseObserver = null,
        CancellationToken cancellationToken = default);
}

public static IServiceCollection AddHttpRequestResiliencePipeline(
    this IServiceCollection services,
    string name,
    HttpRequestResilienceOptions options);
```

`AddHttpRequestResiliencePipeline` registers a keyed singleton resolved with `GetRequiredKeyedService<HttpRequestResiliencePipeline>(name)`. Telemetry callback exceptions are swallowed after bounded invocation and never replace request success, final failure, timeout, or caller cancellation.

---

### Task 1: Isolate the sibling repository and define the resilience contracts

**Files:**
- Create: sibling worktree/branch `feature/cmsify-resilience`
- Create: `src/SyntaxCircus.Http.Resilience/HttpRequestResilienceOptions.cs`
- Create: `src/SyntaxCircus.Http.Resilience/HttpResilienceTelemetry.cs`
- Create: `src/SyntaxCircus.Http.Resilience/HttpResilienceExceptions.cs`
- Create: `src/SyntaxCircus.Http.Resilience/HttpRequestResiliencePipeline.cs` with validated constructor and Task 2 method stub
- Create: `tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResilienceOptionsTests.cs`

**Interfaces:**
- Produces: every enum, record, option property, default, and validation rule in **Shared Interfaces**.
- Consumes: existing sibling .NET 10/xUnit conventions.

- [ ] **Step 1: Create an isolated sibling worktree.** Use `superpowers:using-git-worktrees`, verify sibling `main` is clean, create `feature/cmsify-resilience`, and record the absolute worktree path in the Task 10 SDD ledger. Do not edit sibling `main` in place.

- [ ] **Step 2: Write option-default and invalid-value tests.** Add tests asserting the exact defaults above and rejection of blank names, `MaxAttempts < 1`, non-positive total/backoff/maximum/circuit durations, backoff greater than maximum, failure ratio outside `(0,1]`, throughput below 2, and null time/jitter providers. Add constructor tests for safe copied values and exact timeout/circuit exception properties.

```csharp
[Theory]
[InlineData(0)]
[InlineData(-1)]
public void Constructor_RejectsNonPositiveMaxAttempts(int value)
{
    var options = new HttpRequestResilienceOptions { MaxAttempts = value };
    Should.Throw<ArgumentOutOfRangeException>(() => new HttpRequestResiliencePipeline("cmsify", options));
}
```

- [ ] **Step 3: Run RED.** Run:

```powershell
dotnet test tests/SyntaxCircus.Http.Resilience.Tests/SyntaxCircus.Http.Resilience.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~HttpRequestResilienceOptionsTests"
```

Expected: compile failure because the new public contracts do not exist.

- [ ] **Step 4: Implement the immutable contracts, exception types, and validated pipeline constructor.** Copy option values at pipeline construction so callers cannot mutate live behavior. Validate all inputs before building Polly state. The Task 1 `SendAsync` body throws `NotSupportedException("Request execution is implemented in Task 2.")`; Task 2 replaces that entire stub. Categorize failures only through the four fixed enum values; no string message becomes telemetry.

- [ ] **Step 5: Run GREEN and sibling regression tests.** Run the focused filter, then the full sibling suite. Expected: new tests and all existing `ApiClientBase`, ProblemDetails, token-cache, and `AddResilientHttpClient` tests pass.

- [ ] **Step 6: Commit sibling Task 1.** Commit only sibling files:

```powershell
git add src/SyntaxCircus.Http.Resilience/HttpRequestResilienceOptions.cs src/SyntaxCircus.Http.Resilience/HttpResilienceTelemetry.cs src/SyntaxCircus.Http.Resilience/HttpResilienceExceptions.cs src/SyntaxCircus.Http.Resilience/HttpRequestResiliencePipeline.cs tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResilienceOptionsTests.cs
git commit -m "Define request resilience contracts"
```

---

### Task 2: Implement fresh-request retry, timeout, circuit, and disposal behavior

**Files:**
- Modify: `src/SyntaxCircus.Http.Resilience/HttpRequestResiliencePipeline.cs`
- Create: `tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResiliencePipelineTests.cs`
- Modify: `src/SyntaxCircus.Http.Resilience/ResilientHttpClientExtensions.cs`
- Modify: `tests/SyntaxCircus.Http.Resilience.Tests/ResilientHttpClientExtensionsTests.cs`

**Interfaces:**
- Consumes: Task 1 contracts.
- Produces: `HttpRequestResiliencePipeline.SendAsync(...)` with exact ownership/retry/error semantics.

- [ ] **Step 1: Write retry and fresh-request tests.** For `503 -> 200`, require two distinct request instances, attempt numbers `1,2`, disposal of request/response one, ownership of final response by the caller, and one retry event with fixed `HttpStatus` category. Add corresponding `HttpRequestException -> 200`, `429` delta, and HTTP-date cases using deterministic time/jitter.

```csharp
[Fact]
public async Task SendAsync_RebuildsAndDisposesEveryRetriedAttempt()
{
    var sends = 0;
    var firstRequestContent = new TrackingContent();
    var firstResponseContent = new TrackingContent();
    var pipeline = new HttpRequestResiliencePipeline("test", new HttpRequestResilienceOptions
    {
        BackoffBaseDelay = TimeSpan.FromMilliseconds(1),
        MaximumDelay = TimeSpan.FromMilliseconds(1),
        JitterProvider = () => 0,
    });

    using var final = await pipeline.SendAsync(
        (attempt, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, $"https://example.test/{attempt}")
        {
            Content = attempt == 1 ? firstRequestContent : null,
        }),
        (_, _, _) => Task.FromResult(++sends == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = firstResponseContent }
            : new HttpResponseMessage(HttpStatusCode.OK)),
        HttpCompletionOption.ResponseHeadersRead,
        HttpRequestReplaySafety.Replayable);

    sends.ShouldBe(2);
    firstRequestContent.Disposed.ShouldBeTrue();
    firstResponseContent.Disposed.ShouldBeTrue();
    final.StatusCode.ShouldBe(HttpStatusCode.OK);
}

private sealed class TrackingContent : HttpContent
{
    public bool Disposed { get; private set; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
    protected override bool TryComputeLength(out long length) { length = 0; return true; }
    protected override void Dispose(bool disposing) { Disposed = disposing; base.Dispose(disposing); }
}
```

- [ ] **Step 2: Write timeout/cancellation/replay tests.** Prove one total budget includes delays and attempts, over-budget `Retry-After` throws `HttpRequestTimeoutException` without another send, caller cancellation keeps the caller token and performs no retry, `NotReplayable` returns the first transient response, and final transport failure is the last `HttpRequestException`. Make the jitter provider return `-0.1`, `1.0`, and `double.NaN` and require a deterministic `InvalidOperationException` before any delay.

- [ ] **Step 3: Write circuit/telemetry tests.** Use low deterministic throughput to open the circuit, reject before request construction, transition half-open/closed through `TimeProvider`, and emit safe events. Make retry/circuit callbacks throw and prove they do not change the request outcome. Inspect all telemetry fields and reject any raw exception/body/query value.

- [ ] **Step 4: Run RED.** Run the new test class; expected compile failure for the missing pipeline.

- [ ] **Step 5: Implement the pipeline.** Use Polly resilience primitives for retry/circuit state, but keep request creation and disposal inside the execution callback. Dispose intermediate responses in retry handling, dispose each attempt request after its send completes, return only the final response, and distinguish caller cancellation from the linked total-budget token.

- [ ] **Step 6: Share outcome classification with the legacy helper.** Extract one internal predicate covering `408`, `429`, `500`, `502`, `503`, `504`, transport, and timeout. Preserve `aiMode` behavior for existing `AddResilientHttpClient`: AI mode still excludes `429` from that legacy helper.

- [ ] **Step 7: Run GREEN and full sibling tests.** Expected: focused pipeline/callback/legacy tests pass with no warnings; full sibling suite passes.

- [ ] **Step 8: Commit sibling Task 2.**

```powershell
git add src/SyntaxCircus.Http.Resilience/HttpRequestResiliencePipeline.cs src/SyntaxCircus.Http.Resilience/ResilientHttpClientExtensions.cs tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResiliencePipelineTests.cs tests/SyntaxCircus.Http.Resilience.Tests/ResilientHttpClientExtensionsTests.cs
git commit -m "Add fresh request resilience pipeline"
```

---

### Task 3: Add keyed DI registration, documentation, and the exact local package

**Files:**
- Create: `src/SyntaxCircus.Http.Resilience/HttpRequestResilienceServiceCollectionExtensions.cs`
- Create: `tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResilienceServiceCollectionExtensionsTests.cs`
- Modify: `README.md`
- Create temporarily outside git: clean-consumer project and `artifacts/local-nuget/http-resilience/`

**Interfaces:**
- Consumes: Tasks 1–2 public pipeline.
- Produces: keyed DI registration and exact `SyntaxCircus.Http.Resilience.0.2.0-cmsify.1.nupkg` plus SHA-256.

- [ ] **Step 1: Write keyed-registration parity tests.** Register two names with different attempt/delay values, resolve each keyed singleton twice, require same-name identity and different-name isolation, and run the same `503 -> 200` scenario against direct and DI pipelines.

- [ ] **Step 2: Run RED.** Expected compile failure for missing extension.

- [ ] **Step 3: Implement keyed DI registration.** Require a non-null immutable options value, copy and validate it when constructing the keyed singleton, use `AddKeyedSingleton<HttpRequestResiliencePipeline>`, reject blank names, and add no `DelegatingHandler` or `HttpClient` registration.

- [ ] **Step 4: Document direct, keyed-DI, replay, timeout, telemetry, ownership, and migration behavior.** Include one direct sample and one `GetRequiredKeyedService` sample using the exact public signatures. State that callers must supply a fresh request factory and that callback failures are non-fatal.

- [ ] **Step 5: Run full sibling Release validation.** Run:

```powershell
dotnet build SyntaxCircus.Http.Resilience.slnx --configuration Release
dotnet test SyntaxCircus.Http.Resilience.slnx --configuration Release --no-restore --verbosity minimal
dotnet pack src/SyntaxCircus.Http.Resilience/SyntaxCircus.Http.Resilience.csproj --configuration Release --no-restore -p:Version=0.2.0-cmsify.1 --output artifacts/local-nuget/http-resilience
```

Expected: warning-free build, full tests pass, and exactly one `.nupkg` plus symbols package.

- [ ] **Step 6: Inspect and hash the package.** Verify `.nuspec` identity/version/dependencies/license/readme, required DLL/XML assets, and absence of test/source/build artifacts. Record `Get-FileHash -Algorithm SHA256` in the task report.

- [ ] **Step 7: Run clean-consumer tests.** Create a temporary .NET 10 xUnit project outside both tracked trees, reference only the `.nupkg`, and prove direct and keyed-DI `503 -> 200`, caller cancellation, and `NotReplayable` behavior. Delete the validated temporary directory afterward.

- [ ] **Step 8: Commit sibling Task 3.** Commit source/tests/README only:

```powershell
git add src/SyntaxCircus.Http.Resilience/HttpRequestResilienceServiceCollectionExtensions.cs tests/SyntaxCircus.Http.Resilience.Tests/HttpRequestResilienceServiceCollectionExtensionsTests.cs README.md
git commit -m "Expose request resilience registration"
```

---

### Task 4: Consume the local package and consolidate `CmsifyClient`

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj`
- Modify: `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyClientOptions.cs`
- Modify: `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/CmsifyClient.cs`
- Modify: `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/CmsifyClientTests.cs`
- Create: `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/CmsifyClientDependencyInjectionTests.cs`

**Interfaces:**
- Consumes: exact local sibling package from Task 3.
- Produces: one shared pipeline per direct/DI `CmsifyClient`, no manual retry loop, source-compatible options/constructors.

- [ ] **Step 1: Configure an ignored local feed and pin the exact prerelease.** Keep `NuGet.Config`/package bytes beneath ignored `artifacts/local-nuget`; change the central version to `0.2.0-cmsify.1`, add a package reference, restore, and prove `project.assets.json` resolves that exact version and no sibling project path.

- [ ] **Step 2: Write direct retry parity tests.** Cover `503 -> 200`, delta/date `Retry-After`, `HttpRequestException -> 200`, total-budget timeout, caller cancellation, and exhausted transport failure. Inject deterministic pipeline options through an additive constructor overload accepting `HttpRequestResiliencePipeline`.

```csharp
[Fact]
public async Task DirectClient_RebuildsTokenAndCorrelationForEverySafeRetry()
{
    var tokens = new Queue<string>(["first", "second"]);
    var observed = new List<(string Token, string Correlation)>();
    var client = CreateResilientClient(request =>
    {
        observed.Add((request.Headers.Authorization!.Parameter!, request.Headers.GetValues("X-Correlation-Id").Single()));
        return observed.Count == 1 ? Response(HttpStatusCode.ServiceUnavailable) : Json(HttpStatusCode.OK, new { value = "ok" });
    }, options => options.TokenProvider = _ => ValueTask.FromResult<string?>(tokens.Dequeue()));

    await client.GetAsync<JsonValue>("/api/v1/test", TestContext.Current.CancellationToken);

    observed.Select(x => x.Token).ShouldBe(["first", "second"]);
    observed.Select(x => x.Correlation).Distinct().Count().ShouldBe(2);
}

private static CmsifyClient CreateResilientClient(
    Func<HttpRequestMessage, HttpResponseMessage> handler,
    Action<CmsifyClientOptions>? configure = null)
{
    var options = new CmsifyClientOptions { BaseUrl = new Uri("https://cms.test"), EnableRetries = true };
    configure?.Invoke(options);
    var pipeline = new HttpRequestResiliencePipeline("cmsify-test", new HttpRequestResilienceOptions
    {
        MaxAttempts = options.MaxRetryAttempts,
        TotalRequestTimeout = options.RequestTimeout,
        BackoffBaseDelay = TimeSpan.FromMilliseconds(1),
        MaximumDelay = TimeSpan.FromMilliseconds(1),
        JitterProvider = () => 0,
    });
    return new CmsifyClient(new HttpClient(new StubHandler(handler)), options, pipeline);
}

private static HttpResponseMessage Response(HttpStatusCode status) => new(status);
```

- [ ] **Step 3: Write preservation and replay-fence tests.** Require observer invocation on intermediate/final responses; final ProblemDetails extensions/trace/correlation; ETag read/update behavior; `204`; no retry for POST/PUT/DELETE, multipart, or stream-copy failure; safe download retry before final headers without duplicated destination bytes.

- [ ] **Step 4: Write DI/direct parity and one-policy tests.** Resolve through `AddCmsifyClient`, compare attempts/outcomes with direct construction, and use a counting primary handler to prove a single logical retry produces exactly two physical sends rather than nested retries.

- [ ] **Step 5: Run RED.** Run the .NET client test project. Expected failures show manual loops do not retry transport faults/use the shared pipeline and the package is not yet wired.

- [ ] **Step 6: Map options and implement exactly one pipeline layer per client path.** Preserve `EnableRetries`, `MaxRetryAttempts` as total attempts, and `RequestTimeout` as total logical budget. Set `HttpClient.Timeout = Timeout.InfiniteTimeSpan`. Add only additive circuit/telemetry properties. Add `public CmsifyClient(HttpClient httpClient, CmsifyClientOptions options, HttpRequestResiliencePipeline resiliencePipeline)`; public existing constructors delegate to it with a private pipeline, while DI/Admin may supply one keyed shared instance.

- [ ] **Step 7: Replace manual loops with request factories.** Delete `ShouldRetry` and `GetRetryDelay`. Build auth, fresh correlation ID, JSON content, Accept, and ETag headers inside each attempt factory. Return final responses to the existing observer/error/ETag/JSON path. Keep multipart and post-header stream copy outside replay.

- [ ] **Step 8: Run GREEN and client regressions.** Run `SyntaxCircus.Cmsify.Client.Tests`; expected all tests pass with no double retry and no public contract regression.

- [ ] **Step 9: Commit Cmsify Task 4.** Commit the central pin, package reference, client/options/tests; never commit local feed files.

```powershell
git add Directory.Packages.props sdk/dotnet/src/SyntaxCircus.Cmsify.Client sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests
git commit -m "Consolidate Cmsify client resilience"
```

---

### Task 5: Wire Admin once and prove scoped authentication/session behavior

**Files:**
- Modify: `src/Cmsify.Admin/Program.cs`
- Create: `tests/Cmsify.Admin.Integration.Tests/AdminHttpResilienceTests.cs`
- Modify: `tests/Cmsify.Admin.Integration.Tests/AdminAuthTestFactory.cs`
- Modify: `tests/Cmsify.Admin.Integration.Tests/OidcCircuitTokenForwardingTests.cs`

**Interfaces:**
- Consumes: Task 4 pipeline-aware `CmsifyClient` and exact package.
- Produces: one shared Admin pipeline configuration with scoped token/observer callbacks above pooled transports.

- [ ] **Step 1: Write Admin retry/isolation tests.** Configure fake handler responses `503 -> 200` for two concurrent rendered circuits. Require two physical sends per circuit, each attempt carries only its circuit's bearer token, each attempt has a distinct correlation ID, and session-expiry observer receives both responses for only the owning circuit.

- [ ] **Step 2: Write no-double-policy and cancellation tests.** Inspect service registrations to reject `ResilienceHandler`/standard resilience on `CmsifyApi`; count physical sends; cancel one circuit during delay and prove the other circuit/pipeline remains usable.

- [ ] **Step 3: Run RED.** Run Admin integration filters for `AdminHttpResilienceTests` and OIDC circuit forwarding. Expected failure because Admin does not register/pass the shared pipeline and existing fake handlers do not expose per-attempt behavior.

- [ ] **Step 4: Register one pipeline and retain scoped callbacks.** Configure a singleton `HttpRequestResiliencePipeline` named `CmsifyApi`, pass it into each scoped `CmsifyClient`, and leave `ApiAuthHandler` as transport authentication only where OIDC requires it. Do not add a retrying message handler. Keep `IApiTokenAccessor` and `ResponseObserver` closures inside scoped client construction.

- [ ] **Step 5: Run GREEN and full Admin integration.** Run focused tests, then `Cmsify.Admin.Integration.Tests`; expected token/session/cancellation isolation and all existing tests pass.

- [ ] **Step 6: Commit Cmsify Task 5.**

```powershell
git add src/Cmsify.Admin/Program.cs tests/Cmsify.Admin.Integration.Tests
git commit -m "Use shared Admin HTTP resilience"
```

---

### Task 6: Document, validate, and hand off the publication gate

**Files:**
- Modify: `sdk/dotnet/src/SyntaxCircus.Cmsify.Client/README.md`
- Modify: `docs/integrating.md`
- Modify: `docs/authentication-and-authorization.md`
- Modify: `docs/v1-release-readiness.md`
- Modify: `docs/v1-release-remediation-handoff.md`

**Interfaces:**
- Consumes: final sibling commits/package hash and Cmsify implementation/test evidence.
- Produces: operator/consumer contract and exact stable-publication handoff.

- [ ] **Step 1: Document the exact client behavior.** State safe methods, fresh auth/correlation per attempt, supported transient conditions, delta/date `Retry-After`, total budget, caller cancellation, circuit telemetry, observer ordering, streaming/upload replay fences, direct/DI parity, and no double retries.

- [ ] **Step 2: Record exact local package evidence.** Add sibling branch/commit range, package ID/version, SHA-256, pack/clean-consumer commands, and the explicit user-owned publication gate. State that CI/public restore cannot consume the prerelease until publication/replacement.

- [ ] **Step 3: Run complete sibling validation.** From the isolated sibling worktree:

```powershell
dotnet build SyntaxCircus.Http.Resilience.slnx --configuration Release --no-restore
dotnet test SyntaxCircus.Http.Resilience.slnx --configuration Release --no-restore --verbosity minimal
dotnet pack src/SyntaxCircus.Http.Resilience/SyntaxCircus.Http.Resilience.csproj --configuration Release --no-restore -p:Version=0.2.0-cmsify.1 --output artifacts/local-nuget/http-resilience
```

Recompute the package SHA-256 and rerun the clean consumer against the final package bytes.

- [ ] **Step 4: Run focused Cmsify validation serially.** Run:

```powershell
dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj --configuration Release --no-restore --verbosity minimal
dotnet test tests/Cmsify.Admin.Integration.Tests/Cmsify.Admin.Integration.Tests.csproj --configuration Release --no-restore --verbosity minimal
```

- [ ] **Step 5: Run full Cmsify validation.** Run:

```powershell
dotnet build Cmsify.slnx --configuration Release --no-restore
dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal
```

Expected: all tests pass; record existing Task 11 warnings separately without suppression.

- [ ] **Step 6: Audit the spec requirement by requirement.** Map each goal, non-goal, public interface, replay rule, retry condition, budget/circuit outcome, telemetry exclusion, preserved Cmsify behavior, direct/DI/Admin path, packaging rule, and acceptance criterion to a file plus fresh command output. Fix missing or indirect evidence before handoff.

- [ ] **Step 7: Check both worktrees and commit docs.** Run `git diff --check` and `git status --short` in both repositories. Commit Cmsify documentation:

```powershell
git add sdk/dotnet/src/SyntaxCircus.Cmsify.Client/README.md docs/integrating.md docs/authentication-and-authorization.md docs/v1-release-readiness.md docs/v1-release-remediation-handoff.md
git commit -m "Document shared HTTP resilience"
```

Expected: both tracked worktrees are clean; local package/feed artifacts remain ignored and no external action occurred.

---

## Completion Gate

- [ ] Every plan task has a clean task-scoped review.
- [ ] One final cross-repository review verifies public API shape, package bytes, direct/DI/Admin parity, retry ownership, timeout/cancellation, telemetry safety, and documentation.
- [ ] Sibling package build/tests/pack/clean-consumer pass from final sibling HEAD.
- [ ] Cmsify client/Admin/full solution checks pass from final Cmsify HEAD.
- [ ] The exact `0.2.0-cmsify.1` package hash and restore identity are recorded.
- [ ] No local feed/package bytes, secrets, generated output, push, merge, tag, publish, or release are present.
- [ ] The stable-publication replacement remains explicit and actionable for the user.
