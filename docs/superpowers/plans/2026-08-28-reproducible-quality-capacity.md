# Reproducible Quality and Capacity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Task 11 reproducible and warning-free, move resolved-content paging fully into PostgreSQL, and add deterministic capacity/coverage evidence without making shared-runner latency a blocking gate.

**Architecture:** Repository-wide toolchain and lock policy forms the outer build gate. A scoped API query component owns resolved-content selection and projection. Existing API, infrastructure, Admin, and client test projects own their corresponding capacity invariants; small Node scripts aggregate open coverage and capacity fragments into versioned reports.

**Tech Stack:** .NET SDK 10.0.400, ASP.NET Core/EF Core 10, PostgreSQL/Testcontainers, xUnit v3, coverlet Cobertura output, Dart Sass through AspNetCore.SassCompiler, Node.js 22 standard library, GitHub Actions, Dependabot.

**Spec:** `docs/superpowers/specs/2026-08-28-reproducible-quality-capacity-design.md`

## Global Constraints

- Do not redo Tasks 1–10 or alter their production decisions.
- Keep `SyntaxCircus.Http.Resilience` at exact version `0.2.0-cmsify.1`; use the ignored `artifacts/local-nuget/NuGet.Config` for local restore until the user publishes it. Do not track a local-feed configuration.
- Do not push, merge, tag, publish packages, create releases, or mutate a public feed.
- Use TDD for behavior changes: add the smallest failing test, observe the intended failure, implement, and rerun the focused test.
- Do not hand-edit `sdk/typescript/src/generated` or commit generated CSS, restored LibMan assets, TestResults, coverage output, or capacity output.
- Preserve all existing HTTP request/response shapes, authorization behavior, ETags, effective-range semantics, and pass counts.
- Do not add nullable suppressions, broad warning suppressions, client evaluation, pre-page materialization, or speculative database indexes.

---

## Task 1: Pin the SDK and Define the Locked-Restore Contract

**Files:**

- Create: `global.json`
- Modify: `Directory.Build.props`
- Create: `tests/release-contract/quality-policy.test.mjs`
- Generate: one `packages.lock.json` beside each of the twelve projects in `Cmsify.slnx`

- [ ] **Step 1: Add failing structural contract tests**

Add Node tests that parse repository files and assert:

```js
assert.deepEqual(globalJson.sdk, {
  version: "10.0.400",
  rollForward: "latestPatch",
  allowPrerelease: false,
});
assert.equal(directoryBuildProps.includes("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>"), true);
assert.equal(lockFiles.length, 12);
```

Derive expected lock locations from the twelve `<Project Path>` entries in `Cmsify.slnx`; do not hard-code a count without also comparing the exact path set. Assert no tracked `NuGet.Config` contains `artifacts/local-nuget`.

- [ ] **Step 2: Run the new contract and observe failure**

Run: `node --test tests/release-contract/quality-policy.test.mjs`

Expected: failures for missing `global.json`, restore property, and lock inventory.

- [ ] **Step 3: Add the SDK and restore policy**

Create:

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

Add to `Directory.Build.props`:

```xml
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
```

- [ ] **Step 4: Generate all lock files from the approved local source configuration**

Run:

```powershell
dotnet --version
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --force-evaluate
```

Expected: SDK `10.0.400`; twelve project-adjacent lock files; the exact resilience prerelease is locked without tracking the local source.

- [ ] **Step 5: Prove both sides of the lock contract**

Run:

```powershell
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --locked-mode
```

Temporarily change one central version in the working tree, run the same locked restore and confirm `NU1004`, then restore that single edit with `apply_patch`. Never regenerate locks during this negative check.

- [ ] **Step 6: Rerun the structural tests and commit**

Run: `node --test tests/release-contract/quality-policy.test.mjs`

Commit: `Pin SDK and lock the solution restore graph`

---

## Task 2: Standardize Every Test Project on xUnit v3

**Files:**

- Modify: `Directory.Packages.props`
- Modify: `tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj`
- Modify: `tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj`
- Modify: `tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj`
- Modify: `tests/Cmsify.Admin.Integration.Tests/Cmsify.Admin.Integration.Tests.csproj`
- Modify: `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj`
- Modify: `tests/Cmsify.Infrastructure.Tests/WorkspaceAuthorizationServiceTests.cs`
- Modify: `tests/Cmsify.Infrastructure.Tests/WebhookSecretRotationTests.cs`
- Modify: `tests/Cmsify.Infrastructure.Tests/MediaReconciliationRepositoryTests.cs`
- Modify: `tests/Cmsify.Infrastructure.Tests/ScheduledPublishingDurabilityTests.cs`
- Modify: `tests/Cmsify.Infrastructure.Tests/WebhookDurabilityRepositoryTests.cs`
- Modify: `tests/Cmsify.Admin.Integration.Tests/AdminAuthEndpointTests.cs`
- Modify: `tests/Cmsify.Admin.Integration.Tests/ReconnectModalRenderingTests.cs`
- Modify: `tests/Cmsify.Admin.Integration.Tests/OidcDistributedTokenCacheTests.cs`
- Modify: `tests/Cmsify.Admin.Integration.Tests/OidcCircuitTokenForwardingTests.cs`
- Modify: `tests/Cmsify.Admin.Integration.Tests/BlazorStaticAssetTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/WorkspaceVisibilityApiTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/WebhookAuditApiTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/HealthCheckApiTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/TemplateApiTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/DatabaseMigrationTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/ContentPublishRangeTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/OidcAuthenticationApiTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/QueryApiTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/MediaApiTests.cs`
- Modify: affected `packages.lock.json` files

- [ ] **Step 1: Extend the policy test to reject a mixed stack**

Assert that all five test projects contain `<PackageReference Include="xunit.v3" />`, retain `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk`, set `<OutputType>Exe</OutputType>`, and contain no `PackageReference Include="xunit"`. Assert `Directory.Packages.props` has no central `xunit` v2 entry.

- [ ] **Step 2: Observe the expected failures**

Run: `node --test tests/release-contract/quality-policy.test.mjs`

- [ ] **Step 3: Migrate the project files atomically**

Keep exact versions:

```xml
<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
<PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
<PackageVersion Include="xunit.v3" Version="3.2.2" />
```

Use `xunit.v3` in Core, Infrastructure, API integration, Admin integration, and client tests; add `OutputType=Exe`; preserve runner assets/private-assets metadata.

- [ ] **Step 4: Convert asynchronous lifetimes without changing behavior**

Use xUnit v3 signatures throughout:

```csharp
public ValueTask InitializeAsync() => new(postgres.StartAsync());

public async ValueTask DisposeAsync()
{
    await postgres.DisposeAsync();
}
```

Return `ValueTask.CompletedTask` for synchronous/no-op cases. Preserve cleanup ordering and every existing cancellation token.

- [ ] **Step 5: Regenerate locks and run all five focused suites**

Run the local `--force-evaluate` restore from Task 1, then:

```powershell
dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --configuration Release --no-restore
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore
dotnet test tests/Cmsify.Admin.Integration.Tests/Cmsify.Admin.Integration.Tests.csproj --configuration Release --no-restore
dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj --configuration Release --no-restore
```

Expected: the pre-migration pass counts remain unchanged; Docker-backed tests are not skipped.

- [ ] **Step 6: Commit**

Commit: `Standardize tests on xUnit v3`

---

## Task 3: Enforce Warning-Free First-Party Release Builds

**Files:**

- Modify: `Directory.Build.props`
- Create: `src/Cmsify.Admin/Services/ApiResponse.cs`
- Create: `src/Cmsify.Admin/Properties/AssemblyInfo.cs`
- Create: `tests/Cmsify.Admin.Integration.Tests/ApiResponseTests.cs`
- Modify: `src/Cmsify.Admin/Components/Pages/Home.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Account/Preferences.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Onboarding/TemplatePackages.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Settings/AuditLog.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Media/MediaLibrary.razor`
- Modify: `src/Cmsify.Admin/Components/Shared/MediaPickerModal.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Workspaces/WorkspaceDetail.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Settings/Webhooks.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Settings/ApiClients.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Settings/Users.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Content/ContentEditor.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Templates/TemplateList.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/PickLists/PickListEditor.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Settings/Packages.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Components/ComponentEditor.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Content/ContentList.razor`
- Modify: `src/Cmsify.Admin/Components/Pages/Templates/TemplateBuilder.razor`

- [ ] **Step 1: Add helper tests before changing component code**

Test that `Required` returns a non-null reference, throws an operation-specific `InvalidOperationException` for a missing successful payload, and `ItemsOrEmpty` returns either the response items or an empty list.

```csharp
internal static class ApiResponse
{
    public static T Required<T>(T? value, string operation) where T : class =>
        value ?? throw new InvalidOperationException($"Cmsify API returned no payload after {operation}.");

    public static IReadOnlyList<T> ItemsOrEmpty<T>(PagedResponse<T>? page) =>
        page?.Items ?? [];
}
```

Expose Admin internals only to `Cmsify.Admin.Integration.Tests` with `InternalsVisibleTo`.

- [ ] **Step 2: Record the clean-build baseline and make warnings fatal**

Run:

```powershell
dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental
```

Confirm the known unique inventory: `CS8602` x40, `CS8604` x3, `CS8601` x1, `CS8603` x1. Then set `TreatWarningsAsErrors=true` for Release builds in `Directory.Build.props`; do not broaden any `NoWarn` entry.

- [ ] **Step 3: Fix nullable flow at API response boundaries**

Use `ApiResponse.Required` for required get/create/update/import/preview results and `ItemsOrEmpty` for nullable pages. Snapshot nullable state before use:

```csharp
if (Workspace.Current is not { } workspace)
{
    return;
}

var loaded = ApiResponse.Required(
    await Api.Content.GetAsync(workspace.Id, ContentId, ct),
    "loading content");
```

Use `response.PickLists?.Count ?? 0` for nullable collection members. Preserve current loading, empty, not-found, and error UI; do not use `!` merely to suppress a warning.

- [ ] **Step 4: Run helper/Admin tests and the forced build**

Run:

```powershell
dotnet test tests/Cmsify.Admin.Integration.Tests/Cmsify.Admin.Integration.Tests.csproj --configuration Release --no-restore
dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental
```

Expected: zero first-party compiler/analyzer warnings and zero errors.

- [ ] **Step 5: Extend policy enforcement and commit**

The Node policy test must assert Release warning enforcement exists and that no new `NoWarn`, `WarningsNotAsErrors`, or nullable-disable policy was introduced.

Commit: `Enforce warning-free Release builds`

---

## Task 4: Move Cmsify Sass to the Module System

**Files:**

- Modify: `src/Cmsify.Admin/wwwroot/scss/app.scss`
- Modify: `src/Cmsify.Admin/wwwroot/scss/_variables.scss` only if names must be made module-visible
- Modify: `src/Cmsify.Admin/wwwroot/scss/_custom.scss` for any deprecated first-party global calls discovered by the build
- Modify: `src/Cmsify.Admin/sasscompiler.json`
- Modify: `tests/release-contract/quality-policy.test.mjs`

- [ ] **Step 1: Add failing Sass policy assertions**

Recursively inspect `src/Cmsify.Admin/wwwroot/scss` and reject `@import`, deprecated global color helpers, and legacy Sass `if()` syntax. Assert the compiler arguments include Bootstrap's load path and `--quiet-deps`, and reject a standalone/global `--quiet` flag.

- [ ] **Step 2: Convert the entry point**

Use Bootstrap as a dependency and keep every existing override:

```scss
@use "variables";
@use "bootstrap" with (
  $primary: variables.$primary,
  $success: variables.$success,
  $warning: variables.$warning,
  $danger: variables.$danger,
  $info: variables.$info,
  $font-family-base: variables.$font-family-base
);
@use "custom";
```

Configure `sasscompiler.json` arguments as:

```json
"--style=compressed --load-path=wwwroot/lib/bootstrap/scss --quiet-deps"
```

- [ ] **Step 3: Verify compilation and UI behavior**

Run the Admin integration suite and a forced Admin Release build. Confirm generated `wwwroot/css/app.css` remains ignored, first-party Sass emits no deprecation, and Bootstrap dependency diagnostics do not hide Cmsify diagnostics.

- [ ] **Step 4: Commit**

Commit: `Adopt Sass modules for Admin styles`

---

## Task 5: Extract and Specify the Resolved-Content Database Query

**Files:**

- Create: `src/Cmsify.Api/Queries/ResolvedContentListQuery.cs`
- Create: `src/Cmsify.Api/Properties/AssemblyInfo.cs`
- Modify: `src/Cmsify.Api/Program.cs`
- Modify: `src/Cmsify.Api/Controllers/ContentController.cs`
- Create: `tests/Cmsify.Api.Integration.Tests/ResolvedContentListQueryTests.cs`
- Modify: `tests/Cmsify.Api.Integration.Tests/QueryApiTests.cs`

- [ ] **Step 1: Add semantic HTTP tests that fail on the current implementation's query shape**

Cover active bounded-over-unbounded selection, shortest active duration, published/version tie breaking, exact effective end exclusion, all pre-selection filters, post-selection `Q`, non-Published early empty response, stable published/slug ordering, and overflow. Preserve the existing wire response and authorization tests.

- [ ] **Step 2: Define the focused query boundary**

Create internal types:

```csharp
internal interface IResolvedContentListQuery
{
    Task<ResolvedContentListPage> ExecuteAsync(
        Guid workspaceId,
        ContentListQuery query,
        DateTimeOffset asOf,
        CancellationToken ct);
}

internal sealed record ResolvedContentListRow(
    Guid ContentItemId,
    Guid TemplateVersionId,
    string TemplateName,
    string? Slug,
    string? LocaleCode,
    Guid? TranslationGroupId,
    IReadOnlyList<string> Tags,
    DateTimeOffset PublishedAt);

internal sealed record ResolvedContentListPage(
    IReadOnlyList<ResolvedContentListRow> Items,
    int TotalCount);
```

Expose API internals only to the API integration tests, register `IResolvedContentListQuery` as scoped, inject it into `ContentController`, and map rows to the unchanged `ContentItemSummaryResponse` (`CreatedAt` and `UpdatedAt` remain the resolved version's `PublishedAt`).

- [ ] **Step 3: Implement candidate filtering and correlated winner selection in IQueryable form**

Start with the published, workspace, owner-not-deleted, and effective predicates. Apply `TemplateVersionId`, `TemplateId` through the template-version relationship, locale, translation group, exact slug, normalized tags, and published bounds before selection. Do not apply `CreatedAfter`/`CreatedBefore`, matching current resolved behavior.

For each outer candidate, compare its ID with the first same-content candidate ordered by:

```csharp
.OrderBy(v => v.EffectiveStartAt.HasValue && v.EffectiveEndAt.HasValue ? 0 : 1)
.ThenBy(v => v.EffectiveStartAt.HasValue && v.EffectiveEndAt.HasValue
    ? v.EffectiveEndAt!.Value - v.EffectiveStartAt!.Value
    : TimeSpan.MaxValue)
.ThenByDescending(v => v.PublishedAt)
.ThenByDescending(v => v.VersionNumber)
.Select(v => v.Id)
.First()
```

Keep the expression server-translatable; inspect `ToQueryString()` before adding any raw SQL fallback.

- [ ] **Step 4: Apply post-selection search, count, stable sort, and page projection**

Apply `EF.Functions.ILike(v.Slug ?? string.Empty, $"%{query.Q}%")` only to winners. Execute `CountAsync`, return after count when offset overflows, otherwise sort by current published/slug rules plus `ContentItemId`, `Skip`, bounded `Take`, and project through `TemplateVersions`/`Templates` in one page SQL command.

- [ ] **Step 5: Add an exact SQL-command contract**

Use a `DbCommandInterceptor` that starts counting after setup. Assert valid requests issue exactly two content commands (count + page), overflow issues one (count), non-Published issues zero, normalized SQL contains a template join in the page command, and no command count grows with result size. Do not assert generated aliases.

- [ ] **Step 6: Inspect the representative PostgreSQL plan before changing indexes**

Run `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` for the generated page query against the Task 6 dataset and save the diagnostic output outside tracked files. Retain existing indexes unless the plan shows the version selection dominated by an avoidable sequential scan. If evidence requires an index, add an EF migration for exactly the demonstrated key order and repeat EXPLAIN plus all query tests; otherwise record “existing indexes retained” in `docs/performance.md`.

- [ ] **Step 7: Run focused API tests and commit**

Commit: `Bound resolved content listing in PostgreSQL`

---

## Task 6: Add Representative Resolved-Content Capacity Invariants

**Files:**

- Extend: `tests/Cmsify.Api.Integration.Tests/ResolvedContentListQueryTests.cs`
- Create: `tests/Cmsify.Api.Integration.Tests/CapacityReportFragmentWriter.cs`

- [ ] **Step 1: Seed a deterministic representative dataset**

Seed at least 500 content items, 2,500 published versions, multiple templates, tags/locales, overlapping bounded and unbounded candidates, exact boundary cases, and deleted owners. Build entities in memory and use batched `AddRange`/`SaveChanges`, outside command counting.

- [ ] **Step 2: Prove bounded behavior at both page sizes**

Add `[Trait("Category", "Capacity")]` tests for `PageSize=1` and `PageSize=100`, first and later pages. Assert totals, stable IDs, selection correctness, exactly two commands, no per-template lookup, and the same two-command count when the eligible dataset grows.

- [ ] **Step 3: Emit an optional timing fragment without weakening invariants**

When `CMSIFY_CAPACITY_REPORT_DIR` is set, write `resolved-content.json` atomically with database version, dataset counts, query counts, sample count, sorted elapsed milliseconds, p50/p95/p99, and booleans for the 250/500 ms reference budgets. The test always fails on semantic/query-count violations; timing misses only set report booleans and write a warning.

- [ ] **Step 4: Run the focused capacity filter and commit**

Run:

```powershell
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore --filter Category=Capacity
```

Commit: `Add resolved content capacity invariants`

---

## Task 7: Enforce Media Limits and Incremental Streaming

**Files:**

- Modify: `tests/Cmsify.Api.Integration.Tests/MediaApiTests.cs`
- Modify: `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/CmsifyClientTests.cs`
- Create: `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/GuardedStreams.cs`

- [ ] **Step 1: Add the configured-limit rejection test**

With the existing 1 MiB test configuration, submit `1 MiB + 1 byte`; assert 413 ProblemDetails with the existing `bad-request` contract. Verify no `MediaAsset`, `MediaDeletionIntent`, or blob exists for the rejected file. Also retain a test that the default configuration resolves to 50 MiB.

- [ ] **Step 2: Add guarded upload and download tests**

Implement a non-seekable source that throws if a read request exceeds 128 KiB and a destination that records first-write timing and throws if a write exceeds 128 KiB. Use content that produces multiple reads/writes. Assert:

- multipart upload reads incrementally and does not dispose the caller-owned source;
- `DownloadToAsync` requests `HttpCompletionOption.ResponseHeadersRead`, writes before the entire response is produced, and does not dispose the caller-owned destination;
- server `GetFile` transfers the storage stream without converting it to a byte array and disposes the stored object after the response.

- [ ] **Step 3: Add the opt-in local 50 MiB timing sample**

Under `CMSIFY_CAPACITY_TIMING=true`, stream a generated 50 MiB payload without allocating one 50 MiB byte array. Write `media-streaming.json` into the report directory with bytes, sample count, time-to-first-byte, total duration, maximum observed read/write request, and the diagnostic 500 ms TTFB result.

- [ ] **Step 4: Run API and client focused tests and commit**

Commit: `Verify bounded media upload and streaming`

---

## Task 8: Prove Webhook Claim Batch Bounds

**Files:**

- Modify: `tests/Cmsify.Infrastructure.Tests/WebhookDurabilityRepositoryTests.cs`
- Create: `tests/Cmsify.Infrastructure.Tests/CommandCountingInterceptor.cs`
- Create: `tests/Cmsify.Infrastructure.Tests/CapacityReportFragmentWriter.cs`

- [ ] **Step 1: Add red tests for outbox and delivery batches**

For each repository claim method, seed 251 ready rows for a batch of 100. Assert exactly 100 distinct leases, leave 151 unclaimed, and then claim the next disjoint 100. Run two claimers concurrently and assert no duplicate row or lease token.

- [ ] **Step 2: Assert command-count independence**

Count commands only around `ClaimOutboxEventsAsync` and `ClaimPendingDeliveryLogsAsync`. Record the baseline command count for one eligible row and assert the same count for 251 eligible rows; also assert the returned count never exceeds the requested batch. Keep supported bounds 1–500 and default 100 covered by `WebhookOperationalConfigurationTests`.

- [ ] **Step 3: Add scheduled timing fragments**

With the report directory set, sample a claim of 100 after warm-up and write `webhook-claim.json` with database version, eligible rows, batch size, command count, sample count, p50/p95/p99, duplicate count, overclaim count, and the diagnostic p95<=250 ms result. Timing does not determine the xUnit result.

- [ ] **Step 4: Run focused infrastructure tests and commit**

Commit: `Add bounded webhook claim capacity tests`

---

## Task 9: Collect Open Coverage and Produce a Stable Trend Summary

**Files:**

- Modify: `Directory.Packages.props`
- Modify: all five test `.csproj` files
- Create: `scripts/quality/summarize-coverage.mjs`
- Create: `tests/release-contract/coverage-summary.test.mjs`
- Modify: affected lock files

- [ ] **Step 1: Test the aggregator with fixed Cobertura fixtures**

Create temporary XML files inside the Node test and assert the script groups by assembly, sums `lines-covered/lines-valid` and `branches-covered/branches-valid`, sorts assembly names, and writes deterministic JSON and Markdown. Malformed/missing input must fail; percentages must never become pass thresholds.

- [ ] **Step 2: Add the exact collector dependency**

Pin `coverlet.collector` `10.0.1` centrally and reference it in all five test projects:

```xml
<PackageReference Include="coverlet.collector">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

Regenerate locks from the approved local feed.

- [ ] **Step 3: Implement deterministic raw-report aggregation**

Accept `--input artifacts/coverage --json artifacts/coverage/summary.json --markdown artifacts/coverage/summary.md`. Recursively load `coverage.cobertura.xml`, preserve raw reports, and emit schema `cmsify.coverage.v1` with source SHA plus per-assembly line/branch valid, covered, and percentage fields.

- [ ] **Step 4: Generate and verify real reports**

Run:

```powershell
dotnet test Cmsify.slnx --configuration Release --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/coverage
node scripts/quality/summarize-coverage.mjs --input artifacts/coverage --json artifacts/coverage/summary.json --markdown artifacts/coverage/summary.md
node --test tests/release-contract/coverage-summary.test.mjs
```

- [ ] **Step 5: Commit**

Commit: `Publish open coverage trend summaries`

---

## Task 10: Build the Scheduled Capacity Report Runner

**Files:**

- Create: `scripts/quality/run-capacity.mjs`
- Create: `scripts/quality/merge-capacity-reports.mjs`
- Create: `tests/release-contract/capacity-report.test.mjs`
- Create: `.github/workflows/capacity-trends.yml`

- [ ] **Step 1: Define and test the report schema**

The merged JSON is deterministic except measurement values and has:

```json
{
  "schema": "cmsify.capacity.v1",
  "sourceSha": "40 lowercase hex characters",
  "sdkVersion": "10.0.400",
  "databaseVersion": "PostgreSQL ...",
  "generatedAtUtc": "ISO-8601",
  "datasets": {},
  "measurements": {},
  "diagnosticBudgets": {},
  "blockingInvariantsPassed": true
}
```

Tests reject missing fragments, identity disagreement, non-finite latencies, absent sample/query counts, or a false blocking-invariant flag. Budget misses remain represented as diagnostic `passed:false` values and do not change process exit status.

- [ ] **Step 2: Implement the opt-in runner**

The Node runner creates `artifacts/capacity/fragments`, captures `git rev-parse HEAD` and `dotnet --version`, sets `CMSIFY_CAPACITY_TIMING=true` and the report directory, then invokes the API, Infrastructure, and client projects with `--filter Category=Capacity --no-build`. It merges fragments into `artifacts/capacity/capacity-report.json` and prints a concise Markdown summary.

- [ ] **Step 3: Add scheduled/manual automation**

Run weekly and on `workflow_dispatch`. Use the already-reviewed action identities `actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683`, `actions/setup-dotnet@c2fa09f4bde5ebb9d1777cf28262a3eb3db3ced7`, and `actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02`. Use `global.json`, locked restore, a Release build, the capacity runner, and artifact upload. Print `::warning::` for a diagnostic budget miss while allowing the job to pass; invariant test failures remain blocking.

- [ ] **Step 4: Run the schema tests and one local report**

Run:

```powershell
node --test tests/release-contract/capacity-report.test.mjs
node scripts/quality/run-capacity.mjs
```

- [ ] **Step 5: Commit**

Commit: `Add scheduled capacity trend reports`

---

## Task 11: Add Dependency Automation and Apply Locked Restore Everywhere

**Files:**

- Binding spec: `docs/superpowers/specs/2026-08-28-reproducible-quality-capacity-design.md`
- Create: `.github/dependabot.yml`
- Modify: `.github/workflows/dotnet-test.yml`
- Modify: `.github/workflows/admin-accessibility.yml`
- Modify: `.github/workflows/openapi-contract.yml`
- Modify: `.github/workflows/publish-cmsify.yml`
- Modify: `.github/workflows/typescript-sdk.yml`
- Modify: `src/Cmsify.Api/Dockerfile`
- Modify: `src/Cmsify.Admin/Dockerfile`
- Modify: `tests/release-contract/quality-policy.test.mjs`
- Create: `tests/release-contract/yaml-subset.mjs`

- [ ] **Step 1: Add failing workflow/Docker/Dependabot policy tests**

Assert solution restores use `--locked-mode`, .NET setup honors `global.json`, Docker build stages use SDK `10.0.400`, copy central build files and relevant locks before restore, and restore with locked mode. Assert four weekly Dependabot ecosystems: NuGet `/`, npm `/sdk/typescript`, GitHub Actions `/`, and one Docker update entry with `directories: ["/src/Cmsify.Api", "/src/Cmsify.Admin"]`; each groups patch/minor updates and leaves majors separate; no auto-merge workflow exists.

- [ ] **Step 2: Upgrade the .NET PR workflow in required order**

Use:

1. checkout and setup from `global.json`;
2. locked restore;
3. `dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental`;
4. full tests with `--no-build`;
5. XPlat Code Coverage plus aggregation and raw/summary artifact upload;
6. deterministic `Category=Capacity` invariants without timing mode.

Do not broaden action-pin work beyond touched/new steps; Task 12 owns repository-wide action pinning.

- [ ] **Step 3: Lock accessibility, release, and Docker restores**

Replace independent `10.0.x` SDK selection with `global-json-file: global.json`. Use `dotnet restore Cmsify.slnx --locked-mode` in workflows. In both Dockerfiles, pin the build stage to `mcr.microsoft.com/dotnet/sdk:10.0.400`, copy `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, project files, and the corresponding `packages.lock.json` before `dotnet restore --locked-mode`. Runtime image digest pinning remains Task 12.

- [ ] **Step 4: Add Dependabot configuration and run policy tests**

Use weekly intervals and `groups` with `update-types: ["minor", "patch"]`; major updates remain individual PRs by omission from groups.

- [ ] **Step 5: Commit**

Commit: `Automate locked dependency maintenance`

---

## Task 12: Document the Quality and Capacity Operating Contract

**Files:**

- Modify: `docs/performance.md`
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `docs/v1-release-readiness.md`
- Modify: `docs/v1-release-remediation-handoff.md`
- Create: `tests/release-contract/quality-documentation.test.mjs`

- [ ] **Step 1: Document exact local commands**

Include SDK verification, approved local-feed restore, normal public locked restore after publication, lock regeneration, forced warning build, focused capacity filters, coverage aggregation, scheduled capacity runner, and the single-MSBuild-node full test command. State explicitly that `-m:1` does not serialize xUnit test cases.

- [ ] **Step 2: Document evidence interpretation**

Explain that query counts, batch bounds, no duplicate leases, max+1 rejection, and incremental streaming are blocking. Coverage percentages and latency budgets are trends. List p95/p99 budgets, report schema fields, dataset size, and the current index-plan decision with its EXPLAIN evidence.

- [ ] **Step 3: Update readiness and handoff without closing later gates**

Mark F-11/F-16/F-17 with exact committed evidence only after validation. Retain the user-owned resilience publication gate and Task 12 carries: action pins, SBOM/signing, accessibility trigger expansion, artifact smoke, governance, the Task 9 rollback diagnostic omission, and the pre-existing media reconciliation/API race.

- [ ] **Step 4: Run documentation contracts and commit**

Commit: `Document quality and capacity operations`

---

## Task 13: Fresh Final Verification and Independent Review

**Files:**

- Verify all Task 11 changes; edit only to fix demonstrated failures.

- [ ] **Step 1: Verify source and restore identity**

Run `git status --short`, `dotnet --version`, the release-contract Node tests, lock inventory comparison, and locked restore using the approved local config. Confirm no generated CSS, LibMan assets, TestResults, coverage, capacity artifacts, `.env`, or local-feed config is tracked.

- [ ] **Step 2: Run clean compilation and every focused suite**

Run the forced Release build and all five test projects. Record exact pass totals and confirm zero first-party warnings/errors.

- [ ] **Step 3: Run SDK and report checks**

From `sdk/typescript`, run `npm ci`, `npm run generate:check`, `npm run typecheck`, `npm test`, and `npm run build`. Generate fresh coverage and a fresh capacity report; validate both schemas.

- [ ] **Step 4: Run single-MSBuild-node full validation**

Run:

```powershell
dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -m:1
```

This limits MSBuild project orchestration to one node; it does not serialize xUnit test cases within a project. Use PostgreSQL, MinIO, and Redis Testcontainers. If Docker is unavailable, report the exact limitation rather than skipping or claiming success.

- [ ] **Step 5: Perform change hygiene and independent review**

Run `git diff --check`, inspect `git diff --stat`, compare every result against the approved Task 11 spec, and request an independent code review. Resolve only demonstrated Task 11 defects and rerun affected checks.

- [ ] **Step 6: Commit verification fixes, if any, and hand off**

Report exact commits, commands, pass counts, warning count, report paths, Docker limitations, retained Task 10 publication gate, and Task 12 carries. Do not push, merge, tag, publish, or release.
