# Webhook Egress Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve each webhook destination once per attempt and connect only to that attempt's validated globally routable addresses while retaining the original HTTP and TLS host.

**Architecture:** Separate DNS resolution, address classification, and socket connection behind focused seams. Validation returns an immutable URI/address result; the delivery processor attaches it to the request, and `SocketsHttpHandler.ConnectCallback` opens a fresh direct connection using only those addresses.

**Tech Stack:** .NET 10, C#, `SocketsHttpHandler`, xUnit, NSubstitute, local HTTP/TLS fixtures, `System.Diagnostics.Metrics`.

**Spec:** `docs/superpowers/specs/2026-08-26-webhook-egress-secret-rotation-design.md`

## Global Constraints

- HTTPS by default; HTTP only when `Webhook:AllowHttp=true`.
- Reject a complete DNS answer if any member is non-global or special-use.
- Resolve once per attempt; never use connection-time DNS, hostname fallback, redirects, ambient proxies, or a connection pooled from another attempt.
- Preserve the original authority for HTTP `Host`, TLS SNI, and certificate validation.
- Preserve the public API, webhook signature, event ID, and durable retry contracts.
- Never log secret material, credential-bearing URLs, signatures, or authorization headers.
- Follow strict red-green-refactor sequencing and commit each reviewed task.

## File map

- Modify `src/Cmsify.Core/Interfaces/Services/DomainServices.cs`: immutable validated destination.
- Create `src/Cmsify.Infrastructure/BackgroundServices/IWebhookDnsResolver.cs`: DNS seam and system resolver.
- Create `src/Cmsify.Infrastructure/BackgroundServices/WebhookAddressPolicy.cs`: global-address decision only.
- Modify `src/Cmsify.Infrastructure/BackgroundServices/WebhookDestinationValidator.cs`: URI and one-lookup policy.
- Create `src/Cmsify.Infrastructure/BackgroundServices/PinnedWebhookTransport.cs`: request pins, socket connector, handler.
- Modify `src/Cmsify.Infrastructure/BackgroundServices/WebhookDeliveryProcessor.cs`: attach pins per attempt.
- Modify `src/Cmsify.Infrastructure/BackgroundServices/CmsifyOperationalMetrics.cs`: bounded security counters.
- Modify `src/Cmsify.Infrastructure/Extensions/ServiceCollectionExtensions.cs`: secure transport DI.
- Test in `WebhookDestinationValidatorTests.cs`, new `PinnedWebhookTransportTests.cs`, `WebhookDurabilityRepositoryTests.cs`, and `CmsifyOperationalMetricsTests.cs`.
- Document in `README.md`, `docs/operations.md`, `.env.example`, and `src/Cmsify.Api/.env.example`.

---

### Task 1: One-resolution validation and global-address policy

**Files:** Modify `DomainServices.cs` and `WebhookDestinationValidator.cs`; create `IWebhookDnsResolver.cs` and `WebhookAddressPolicy.cs`; test `WebhookDestinationValidatorTests.cs`.

**Interfaces:** Produces `IWebhookDnsResolver.ResolveAsync(string, CancellationToken)`, `WebhookAddressPolicy.IsGlobal(IPAddress)`, and `WebhookDestinationValidationResult.Valid(Uri, IReadOnlyList<IPAddress>)`.

- [ ] **Step 1: Write failing resolver and URI-policy tests.** Inject an NSubstitute resolver and cover HTTPS success, default HTTP rejection, explicit HTTP opt-in, credentials, IP literals without DNS, empty/exceptional DNS, cancellation, normalization, exactly one lookup, and mixed public/prohibited rejection.

```csharp
resolver.ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>())
    .Returns([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")]);
var result = await CreateValidator(resolver).ValidateAsync("https://hooks.example.test/a");
Assert.False(result.IsValid);
await resolver.Received(1).ResolveAsync("hooks.example.test", Arg.Any<CancellationToken>());
```

- [ ] **Step 2: Write failing address-table tests.** Cover boundaries for IPv4 `0/8`, `10/8`, `100.64/10`, `127/8`, `169.254/16`, `172.16/12`, `192.0.0/24`, `192.0.2/24`, `192.168/16`, `198.18/15`, `198.51.100/24`, `203.0.113/24`, `224/4`, `240/4`; and IPv6 unspecified, loopback, mapped IPv4, `64:ff9b:1::/48`, `100::/64`, `2001:db8::/32`, unique-local, link-local, multicast, reserved/non-global IANA assignments. Include adjacent permitted boundaries and representative global values.

- [ ] **Step 3: Run RED.**

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WebhookDestinationValidatorTests
```

Expected: FAIL because the resolver seam, address set, and exhaustive policy do not exist.

- [ ] **Step 4: Implement these exact result/resolver shapes.**

```csharp
public interface IWebhookDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed record WebhookDestinationValidationResult(
    bool IsValid, Uri? DestinationUri, IReadOnlyList<IPAddress> Addresses, string? Error)
{
    public string? NormalizedUrl => DestinationUri?.AbsoluteUri;
    public static WebhookDestinationValidationResult Valid(Uri uri, IReadOnlyList<IPAddress> addresses) =>
        new(true, uri, addresses.ToArray(), null);
    public static WebhookDestinationValidationResult Invalid(string error) => new(false, null, [], error);
}
```

`SystemWebhookDnsResolver` delegates to `Dns.GetHostAddressesAsync`. IP literals create a one-address set without DNS. Copy all returned arrays.

- [ ] **Step 5: Implement explicit prefix matching.** Normalize IPv4-mapped IPv6 before the IPv4 table, default-deny unknown families, and record the official IANA IPv4/IPv6 Special-Purpose Address Registry URLs plus review date `2026-08-26` beside the immutable tables.

- [ ] **Step 6: Run GREEN and compile consumers.** Run the Step 3 command, then `dotnet build src/Cmsify.Api/Cmsify.Api.csproj --configuration Release --no-restore`. Update controller/test call sites to supply a URI and approved list without changing API shapes.

- [ ] **Step 7: Commit.** Stage the four production files, validator tests, and mechanically updated call sites; commit `Harden webhook destination resolution`.

### Task 2: Pinned direct transport and original TLS identity

**Files:** Create `PinnedWebhookTransport.cs` and `PinnedWebhookTransportTests.cs`; modify `ServiceCollectionExtensions.cs`.

**Interfaces:** Consumes Task 1's destination. Produces `PinnedWebhookTransport.DestinationKey`, `IWebhookSocketConnector.ConnectAsync(IReadOnlyList<IPAddress>, int, CancellationToken)`, and `CreateHandler(IWebhookSocketConnector, TimeSpan connectTimeout)`.

- [ ] **Step 1: Write failing fail-closed/pin tests.** Assert missing pins and host/port mismatches fail before connection, connector candidates equal only the validated set, failed candidates never trigger DNS fallback, cancellation disposes sockets, and two sequential requests invoke the connector twice.

- [ ] **Step 2: Write failing observable network tests.** Use local listeners and a certificate for `hooks.example.test`; construct an already-validated loopback result only inside this transport fixture. Assert the listener receives the socket while SNI/certificate validation uses `hooks.example.test`, a wrong-host certificate fails, an ambient proxy listener is untouched, and a 302 target listener is untouched.

- [ ] **Step 3: Run RED.**

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~PinnedWebhookTransportTests
```

Expected: FAIL because pinned transport is absent.

- [ ] **Step 4: Implement the transport seam and handler.**

```csharp
public interface IWebhookSocketConnector
{
    ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct);
}

public static readonly HttpRequestOptionsKey<WebhookDestinationValidationResult> DestinationKey =
    new("Cmsify.Webhook.ValidatedDestination");

new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    ConnectTimeout = connectTimeout,
    PooledConnectionLifetime = TimeSpan.Zero,
    PooledConnectionIdleTimeout = TimeSpan.Zero,
    ConnectCallback = (context, ct) => ConnectAsync(context, connector, ct)
};
```

Validate the option and original host/port before delegating. `SocketWebhookConnector` tries only supplied candidates, disposes failed sockets, returns an owning `NetworkStream`, and throws a bounded `HttpRequestException` after exhaustion.

- [ ] **Step 5: Register singletons and replace the named client's `HttpClientHandler`.** Pass the validated `WebhookOperationalOptions.RequestTimeoutSeconds` as both the existing overall request timeout and the bounded connect timeout; add no proxy or certificate bypass.

- [ ] **Step 6: Run GREEN.** Run the Step 3 command and require every direct, TLS, redirect, proxy, cancellation, and fresh-connection test to pass.

- [ ] **Step 7: Commit.** Stage transport, DI, and tests; commit `Pin webhook delivery connections`.

### Task 3: Attempt wiring, retry revalidation, and bounded metrics

**Files:** Modify `WebhookDeliveryProcessor.cs`, `CmsifyOperationalMetrics.cs`, `WebhookDurabilityRepositoryTests.cs`, and `CmsifyOperationalMetricsTests.cs`.

**Interfaces:** Consumes `DestinationKey`; produces `RecordDestinationRejection(string)` and `RecordPinnedConnectionFailure(string)` with fixed `reason` tags.

- [ ] **Step 1: Write failing rebinding and retry tests.** In the full validator-to-transport path, configure a resolver that would return a public answer first and a private answer on a second call; assert one lookup and a connector call containing only the first public address. Then return address set A on the first durable attempt and B on the retry; capture request options and assert each request carries its matching result, validation occurs twice, and invalid validation creates no client/send.

- [ ] **Step 2: Write failing metric tests.** Require `cmsify.webhook.destination.rejected` and `cmsify.webhook.connection.failed`; arbitrary inputs normalize to `unknown`, and each measurement has only one fixed `reason` tag.

- [ ] **Step 3: Run RED.** Run the Infrastructure project filtered to `WebhookDurabilityRepositoryTests|CmsifyOperationalMetricsTests`; expect missing pins/instruments.

- [ ] **Step 4: Attach the complete validation result.** Build the request from `DestinationUri` and call `request.Options.Set(PinnedWebhookTransport.DestinationKey, destination)`. Preserve cancellation, signatures, event IDs, status/error persistence, and completion-time retry scheduling. Map exception/validation categories through fixed switches; never use raw messages as tags.

- [ ] **Step 5: Run GREEN.** Run the same filter, then the complete `Cmsify.Infrastructure.Tests` project in Release with `--no-restore`; require all PostgreSQL cases to pass.

- [ ] **Step 6: Commit.** Stage the four files; commit `Enforce pinned webhook attempts`.

### Task 4: Egress documentation and regression gate

**Files:** Modify `README.md`, `docs/operations.md`, `.env.example`, and `src/Cmsify.Api/.env.example`.

- [ ] **Step 1: Establish documentation RED.** Search for direct-only egress, ambient proxy bypass, one resolution per attempt, mixed-result rejection, redirects disabled, and retry revalidation; record missing clauses.

- [ ] **Step 2: Document all behaviors and both metric names.** Keep `Webhook__AllowHttp=false` and label opt-in HTTP as controlled-development-only.

- [ ] **Step 3: Verify prose and diff.** Run `rg -n "direct-only|ambient proxy|revalidat|redirect|mixed" README.md docs/operations.md` and `git diff --check`.

- [ ] **Step 4: Run serial regression.** Run API Release build, then Core, Infrastructure, and API integration Release tests with `--no-restore`, one heavy process at a time. Stop only exact orphaned child build/test processes after their parent exits.

- [ ] **Step 5: Commit.** Stage the four documentation/config files; commit `Document secure webhook egress`.
