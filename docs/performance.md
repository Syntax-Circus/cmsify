# Quality and capacity operations

Cmsify separates deterministic release gates from environment-sensitive trends. Query counts, database paging, batch bounds, upload rejection, and streaming behavior are blocking. Coverage percentages and latency budgets are diagnostic trends; they never replace the named behavioral tests.

## Reproducible toolchain and restore

Run commands from the repository root in PowerShell. The checked-in [`global.json`](../global.json) selects the SDK:

```powershell
dotnet --version
```

Expected: `10.0.400`. Repository projects create and consume project-adjacent NuGet locks through `RestorePackagesWithLockFile`.

`SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` is still available only through the approved ignored local feed. Until the user publishes those exact bytes to an approved public source, use the ignored configuration for local validation:

```powershell
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --locked-mode
```

Neither the configuration, feed bytes, nor package cache may be tracked or copied into a container. Public/CI restore remains blocked until the user completes that publication gate. After the exact package is public, prove the ordinary configured public-source path with no local-feed arguments:

```powershell
dotnet restore Cmsify.slnx --locked-mode
```

Do not report the public or hosted gate as passed until that command succeeds from a clean environment and the package identity is verified. If a stable replacement is selected instead, update the central pin and regenerate every affected lock before running the same public locked restore.

### Safe lock regeneration

Change package versions centrally, preserve unrelated work, and regenerate through the approved source configuration. `--force-evaluate` is intentional only for a dependency change; it is not a normal build command.

```powershell
git status --short
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --force-evaluate
git diff -- ':(glob)**/packages.lock.json'
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --locked-mode
node --test tests/release-contract/quality-policy.test.mjs
```

Review the exact changed lock graphs, package versions, and content hashes. Keep one lock beside each of the twelve `Cmsify.slnx` projects, restore unrelated line-ending churn, and never hand-edit a lock or add a tracked `NuGet.Config` for `artifacts/local-nuget`.

## Build and test gates

The Release build is forced and non-incremental so cached output cannot hide a warning. Repository policy promotes emitted first-party compiler, analyzer, and nullable warnings to errors; Sass quieting applies only to Bootstrap dependency diagnostics.

```powershell
dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental --verbosity minimal
dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal
```

For the final local serial check, use the VSTest run-setting form documented by the implementation plan:

```powershell
dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -- RunConfiguration.MaxCpuCount=1
```

The API, Infrastructure, and Admin suites require Docker for PostgreSQL, MinIO, and Redis Testcontainers. Run the command and report an unavailable Docker environment rather than skipping those tests.

## Blocking capacity invariants

After the Release build, run all three deterministic capacity filters. Pull-request automation runs these without `CMSIFY_CAPACITY_TIMING` or a report directory, so an invariant failure blocks the job.

```powershell
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-build --filter Category=Capacity
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-build --filter Category=Capacity
dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj --configuration Release --no-build --filter Category=Capacity
```

The blocking contract is:

- Resolved-content requests execute exactly two content SQL commands—one count and one joined page projection—for both small and maximum pages. The page SQL contains database-side outer `LIMIT` and `OFFSET`; the command count stays constant as eligible/page counts grow, and template or content-item N+1 lookups are counted.
- Webhook outbox and delivery claims return no more than the configured batch, keep the command count independent of eligible-row count, and produce no duplicate row or lease across concurrent claimers. Supported batch bounds remain 1–500 with default 100.
- A configured media limit rejects one byte over the maximum with 413 and creates no database state and no storage state: no `MediaAsset`, deletion intent, or committed blob.
- Upload, server response, and client download use incremental streaming with bounded requests and explicit stream ownership/disposal. Callers retain ownership of caller-provided upload and destination streams; the server disposes its stored-object stream after response completion.

## Coverage trends

Collect open Cobertura reports for all five .NET test projects and aggregate them:

```powershell
dotnet test Cmsify.slnx --configuration Release --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/coverage --verbosity minimal
node scripts/quality/summarize-coverage.mjs --input artifacts/coverage --json artifacts/coverage/summary.json --markdown artifacts/coverage/summary.md
node --test tests/release-contract/coverage-summary.test.mjs
```

Raw reports and summaries remain ignored under `artifacts/coverage`. The JSON schema is `cmsify.coverage.v1` with top-level `schema`, full lowercase `sourceSha`, and ordinal `assemblies`. Each assembly entry contains `assembly`, plus `lines` and `branches`, each with integer `valid`, integer `covered`, and `percentage`. Coverage percentages are trend data: the schema has no threshold or pass/fail field, and no release-critical behavior is waived by a percentage.

## Scheduled capacity trends

After a Release build, the opt-in runner executes the same API, Infrastructure, and client capacity categories with timing enabled, validates three fragments, and writes the merged report:

```powershell
node scripts/quality/run-capacity.mjs
node --test tests/release-contract/capacity-report.test.mjs
```

The scheduled/manual workflow is [`.github/workflows/capacity-trends.yml`](../.github/workflows/capacity-trends.yml). It uploads `artifacts/capacity/capacity-report.json`. A missed latency budget produces a visible warning and `passed: false`, but exits successfully only when every blocking invariant remains true.

The `cmsify.capacity.v1` top level contains:

- `schema`, full lowercase `sourceSha`, exact `sdkVersion`, PostgreSQL `databaseVersion`, and canonical `generatedAtUtc`;
- `datasets.mediaStreaming`, `datasets.resolvedContent`, and `datasets.webhookClaim`;
- `measurements.mediaStreaming`, `measurements.resolvedContent`, and `measurements.webhookClaim`, including sample/query/command counts and latency distributions;
- `diagnosticBudgets.mediaStreamingTimeToFirstByte`, `resolvedContentP95`, `resolvedContentP99`, and `webhookClaimP95`, each with `actualMilliseconds`, `thresholdMilliseconds`, and `passed`;
- top-level `blockingInvariantsPassed`, which must be `true` or report generation fails.

Initial diagnostic budgets are:

| Measurement | Budget |
| --- | ---: |
| Resolved-content list | p95 <= 250 ms; p99 <= 500 ms |
| Webhook claim of 100 ready rows | p95 <= 250 ms |
| Local 50 MiB media streaming | TTFB <= 500 ms |

Latency budgets are diagnostic trends until a stable production-like baseline exists. They must not be silently loosened, and they do not turn shared-runner timing into a pull-request gate.

## Representative datasets and index decision

The checked capacity fixture has 520 content items and 2,600 published versions: 500 live owners, 20 deleted owners, five templates, two locales, tags, translation groups, overlapping bounded/unbounded candidates, and exact effective-range boundaries. The webhook fixture has 251 eligible rows for a batch of 100, exercising two disjoint sequential batches and overlapping concurrent workers. The media timing fixture streams 52,428,800 bytes without allocating one payload-sized byte array.

Before the checked capacity fixture existed, the resolved-content page query was inspected with PostgreSQL 17 using the design-minimum 500 content items and 2,500 published versions. `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` for a 100-row page reported 33.405 ms execution time. The outer active set and all 2,500 correlated winner probes used existing content-version indexes; sequential scans were limited to the five-row template tables and 500-row owner table. This EXPLAIN is diagnostic rather than a timing gate. Index decision: **existing indexes retained**; no new index or EF migration was justified.
