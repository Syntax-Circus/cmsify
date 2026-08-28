# Reproducible Quality and Capacity Design

## Status

Approved in conversation on 2026-08-28. This design is the binding specification for Task 11 of `docs/superpowers/plans/2026-08-24-v1-remediation.md` and closes F-11, F-16, and F-17 from `docs/v1-release-readiness.md`.

## Objective

Make Cmsify builds reproducible, first-party Release output warning-free, resolved-content listing bounded at the database boundary, and capacity expectations observable without replacing behavioral tests with coverage percentages or unreliable wall-clock gates.

Task 11 must produce:

- an exact .NET SDK policy and deterministic NuGet restore;
- one xUnit v3 test stack across the solution;
- zero emitted first-party compiler, analyzer, nullable, and first-party Sass deprecation warnings in a clean Release build, with enforcement against regressions;
- supported first-party Sass module usage while isolating unavoidable Bootstrap implementation warnings;
- database-side resolved-content filtering, effective-version selection, sorting, counting, and paging with no per-row template lookup;
- deterministic query-count and bounded-batch tests plus non-blocking coverage and capacity trends;
- documented operational budgets and the commands that produce their evidence.

## Scope boundaries

Task 11 does not publish the local `SyntaxCircus.Http.Resilience` prerelease, change API wire contracts, introduce cursor pagination, redesign content timing, or certify release artifacts. Stable resilience publication remains the user-owned Task 10 release gate. Supply-chain signing, SBOMs, action pinning across all existing workflows, accessibility trigger expansion, production-like artifact smoke tests, governance documents, and final release certification remain Task 12.

No tracked NuGet configuration may reference the ignored local feed. Lock files may record the exact `0.2.0-cmsify.1` package identity already consumed locally, but CI/public restore remains gated until those exact bytes are available from an approved public source or the stable replacement is pinned and the locks are regenerated.

## Selected approach

Use deterministic correctness gates on every pull request and scheduled trend measurement for environment-sensitive performance data.

Rejected alternatives:

1. Making latency and throughput thresholds blocking on shared GitHub runners would turn host contention into release failures and encourage relaxed, meaningless budgets.
2. Adding only SDK pins, warning flags, and documentation would leave the F-17 unbounded query and N+1 behavior intact.
3. Replacing behavioral tests with a repository-wide coverage percentage would reward execution without proving the release-critical scenarios.

## Toolchain and restore contract

Add a root `global.json` with SDK `10.0.400`, `rollForward` set to `latestPatch`, and prerelease SDK selection disabled. Local and CI commands must honor that file. Workflows use the root SDK policy instead of an independent `10.0.x` range.

Enable `RestorePackagesWithLockFile` for repository projects and check in a `packages.lock.json` beside every project in `Cmsify.slnx`. All normal dependency changes regenerate and review those lock files. CI and release validation use `dotnet restore Cmsify.slnx --locked-mode`; build and test continue with `--no-restore`. Docker restore stages copy the relevant lock files and use locked mode. A clean locked restore must fail if central versions, transitive graphs, package content identity, or project references drift from the checked-in graph.

The solution retains central package management and transitive pinning. No floating package version, wildcard SDK, or unreviewed restore fallback is permitted.

## Test stack consistency

All five test projects use `xunit.v3` rather than a mix of `xunit` v2 and v3. The central v2 `xunit` version is removed. The already-pinned `xunit.v3` `3.2.2`, `xunit.runner.visualstudio` `3.1.5`, and `Microsoft.NET.Test.Sdk` `18.9.0` remain the initial exact stack unless implementation discovers a demonstrated incompatibility; any correction must update the central pin and every lock atomically.

Existing assertions, collection fixtures, Testcontainers lifetimes, cancellation tokens, filters, and pass counts remain behaviorally equivalent. Migration is not permission to skip or weaken Docker-backed tests.

## Warning policy and Admin nullability

Repository build policy treats emitted first-party C# compiler and analyzer warnings as errors. The enforcement applies to clean Release builds locally and in CI. Existing project-scoped `CS1591` suppressions for projects that deliberately do not generate a complete public XML reference are not broadened; Task 11 must not add nullable or general-purpose warning suppressions.

Resolve the current Admin warning inventory at its source: 40 `CS8602`, one `CS8601`, one `CS8603`, and three `CS8604`. Loading, authorization, missing-response, and failed-request states use explicit branches or stable local snapshots. Null-forgiving operators and default-object fabrication are allowed only where a checked invariant makes the value non-null; they cannot be used merely to silence the compiler. Observable loading, empty, not-found, and error behavior remains unchanged and the closest Admin integration tests cover any branch whose behavior changes.

The completion build is a forced non-incremental Release build. Zero emitted first-party warnings is required; cached outputs are not evidence.

## Sass contract

Cmsify-owned Sass uses the module system. `app.scss` loads the local variables module, loads Bootstrap through a configured Sass load path with those variables supplied through module configuration, then loads Cmsify custom styles. Cmsify-owned files contain no `@import` and use no deprecated global color or legacy `if()` APIs.

Bootstrap `5.3.8` remains a LibMan-restored dependency for this task. Its internal legacy Sass diagnostics are third-party output, not Cmsify source debt. Configure the Sass compiler so Bootstrap is resolved as a dependency and dependency diagnostics are quiet, while first-party diagnostics remain visible and fatal. Do not globally discard Sass stderr and do not commit generated CSS or restored Bootstrap source.

A clean Admin Release build must compile `wwwroot/scss/app.scss` to the ignored `wwwroot/css/app.css`, report no first-party Sass deprecation, and preserve the current Bootstrap variable overrides and Admin styling. Admin integration and accessibility coverage remain the behavioral checks; Task 12 will expand accessibility workflow triggers.

## Resolved-content query semantics

Extract the resolved-list query from controller response assembly into one focused query component that accepts the existing `ContentListQuery`, workspace ID, and resolved `asOf` instant. The HTTP route, request shape, response shape, authorization behavior, and `ETag` behavior do not change.

The database candidate predicate preserves current semantics:

- workspace matches and status is Published;
- the owning content item is not deleted;
- an effective version is either unbounded (`EffectiveStartAt` and `EffectiveEndAt` both null) or bounded and active (`EffectiveStartAt <= asOf < EffectiveEndAt`);
- `TemplateVersionId`, `TemplateId`, locale, translation group, exact slug, all normalized tags, and published-date bounds filter candidates before effective-version selection;
- a non-Published status filter returns an empty page without querying version rows;
- `Q` applies to the selected effective version's slug after effective-version selection, preserving the current behavior where an older matching version cannot displace the selected version.

For each content item, select exactly one candidate in this order:

1. active bounded range before unbounded fallback;
2. shortest bounded effective duration first;
3. newest `PublishedAt` first;
4. highest `VersionNumber` first.

`ContentItemId` is the stable final page-order tie breaker, not a new version-selection rule. Published sorting and slug sorting retain the existing ascending/descending choices; the resolved default/`createdAt` behavior remains published-time ordering because resolved snapshots expose published time for all three summary timestamps.

Selection, post-selection search, total count, stable sort, offset calculation, and bounded `Take` execute in PostgreSQL before materialization. The page projection joins `TemplateVersions` and `Templates` to obtain the template name in the page query. It returns only fields needed by `ContentItemSummaryResponse`, including the snapshotted tags. There is no template query inside a result loop.

The content portion of a valid resolved-list request executes exactly two SQL commands regardless of candidate count or page size: one count and one page projection. Workspace authorization may execute its existing separate query and is counted separately in end-to-end tests. An overflowed page offset returns an empty page after the count and does not execute an unbounded fallback query.

Use translatable EF Core LINQ when it produces the specified PostgreSQL query. A parameterized PostgreSQL query is permitted only if EF cannot translate the exact rank semantics; it must remain encapsulated in the query component, use typed parameters, return the same projection, and pass the same behavior/query-count tests. No client evaluation or pre-page `ToListAsync` is allowed.

Existing content-version indexes are the baseline. Add or change an index only with an EF migration and PostgreSQL execution-plan evidence from the representative dataset showing that it supports the selected query. No speculative wide index is required by this design.

## Deterministic capacity gates

Capacity tests are behavioral bounds, not microbenchmarks. They run against PostgreSQL Testcontainers where database behavior matters.

### Resolved-content hot read

Seed at least 500 content items and 2,500 published versions with overlapping bounded/unbounded candidates, multiple templates, tags, locales, and deleted owners. The test must prove:

- exact selection and filter semantics at effective-range boundaries;
- stable first and later pages with `PageSize=100`;
- the correct total without materializing all candidates;
- exactly two content SQL commands for both a small and maximum page;
- no SQL command count growth when candidate or page-result counts grow;
- no template lookup command per returned item.

The test interceptor counts commands after authorization/setup and records normalized SQL for diagnostic failures. It must not assert provider-generated alias names or other brittle SQL formatting.

### Media bounds and streaming

Keep the default 50 MiB media limit and the existing configurable lower test limit. A configured-limit test sends one byte over the limit, expects the documented ProblemDetails response, and proves no blob is committed. A guarded source/destination stream test proves upload/download paths consume data incrementally and do not require full-payload buffering. Tests assert bounded read/write requests and disposal/ownership, not process-wide heap measurements.

### Webhook batches

Seed more than twice the configured outbox and delivery batch sizes. Claim/dispatch repository tests prove each operation returns no more than the configured batch, does not duplicate leases across concurrent workers, and uses a command count independent of the number of eligible rows. The supported configuration remains 1–500 with the default 100.

## Coverage and timing trends

Collect line and branch coverage for all five .NET test projects in an open machine-readable format. CI publishes the raw report and a concise assembly trend summary. Coverage decreases are visible in job output/artifacts, but Task 11 sets no percentage pass threshold. Every release-critical behavior remains enforced by named tests.

Add an opt-in local/scheduled capacity runner that emits a versioned JSON report containing source SHA, SDK, database version, dataset size, query counts, sample counts, and latency distributions. Pull requests run deterministic capacity invariants; scheduled/manual runs collect timing trends.

Initial SLO-oriented reference budgets are:

- resolved-content list over the representative dataset: p95 at or below 250 ms and p99 at or below 500 ms, with exactly two content SQL commands;
- webhook claim of 100 ready rows: p95 at or below 250 ms, with no over-claim or duplicate lease;
- local 50 MiB media streaming: time to first response byte at or below 500 ms and sustained transfer without full-payload buffering.

Timing budgets are diagnostic until a production-like runner accumulates a stable baseline. A scheduled miss marks the report and raises a visible warning; it does not silently loosen the budget or fail an unrelated pull request. Query counts, batch sizes, upload rejection, and streaming behavior are blocking.

## Dependency automation

Add Dependabot configuration for NuGet at the repository root, npm under `sdk/typescript`, GitHub Actions, and the Dockerfiles under `src/Cmsify.Api` and `src/Cmsify.Admin` through one Docker ecosystem entry's `directories` list. Run weekly, group compatible patch/minor updates by ecosystem, and keep major updates separate. Dependabot never auto-merges. Every dependency change must regenerate the affected lock, pass locked restore, warning enforcement, focused tests, and the full applicable suite.

Task 12 will pin every workflow action to reviewed commit SHAs and certify release artifacts; Task 11 dependency automation must not weaken that later gate.

## CI and documentation

The .NET validation workflow performs, in order:

1. SDK setup from `global.json`;
2. locked solution restore;
3. forced warning-enforced Release build without restore;
4. full tests without restore;
5. coverage collection and trend publication;
6. deterministic capacity tests.

Update the nearest contributor/runbook documentation with locked-restore commands, lock-update procedure, warning policy, capacity commands, report schema, SLO interpretation, and the distinction between blocking invariants and scheduled timing trends. Update `docs/v1-release-readiness.md` and `docs/v1-release-remediation-handoff.md` with exact final evidence, while retaining the Task 10 publication gate and Task 12 carries.

## Validation and completion evidence

Task 11 is complete only when all of the following are fresh on the committed implementation:

- exact SDK selection succeeds and a deliberately changed dependency graph fails locked restore;
- clean locked restore succeeds for all solution projects using the approved source configuration;
- every test project runs on xUnit v3 with its prior behavior retained;
- clean non-incremental Release build emits zero first-party warnings and zero errors, including no Cmsify-owned Sass deprecations;
- resolved-content semantic, query-count, and representative-load tests pass with PostgreSQL;
- media and webhook deterministic capacity tests pass;
- coverage and capacity report generation succeeds and its schema/identity fields are verified;
- Core, Infrastructure, API, Admin, and .NET client focused suites pass;
- TypeScript generation check, typecheck, tests, and build pass if workflow/dependency files affect it;
- strict-serial full solution tests pass with PostgreSQL, MinIO, and Redis Testcontainers;
- `git diff --check`, lock inventory, generated-file checks, and an independent task review are clean.

No push, merge, tag, package publication, release, or public-feed mutation is part of Task 11.
