# Cmsify v1 release-readiness audit

**Audit date:** 2026-08-24  
**Audited revision:** `616773d` (`main`)  
**Decision:** Not ready to label `1.0.0`; the runtime foundation is healthy, but the public contract, SDK, release, upgrade, and multi-instance reliability guarantees are not yet v1-grade.

This report is a release gate, not a public delivery commitment. It covers the HTTP API, the handwritten .NET contracts and clients, `@cmsify/client`, published containers, production operation, and reuse of sibling `SyntaxCircus.*` packages.

## Locked v1 decisions

- **Promise:** v1 covers a SemVer-stable HTTP API, both first-party SDKs, production containers, documented operation, and upgrades.
- **Licensing:** server/repository artifacts are AGPL-3.0-or-later; `Cmsify.Contracts`, both .NET client packages, and `@cmsify/client` are MIT.
- **.NET support:** the SDK supports .NET 10 only for v1.
- **TypeScript support:** Node 20+ and compatible server/edge fetch runtimes are supported. Browser bundles are explicitly unsupported because Cmsify bearer credentials are server secrets.
- **Promotion:** one reviewed `vX.Y.Z` tag promotes every artifact from the same commit.
- **Upgrade:** v1 must upgrade the latest published `0.1.x`; older prereleases first upgrade to that baseline.
- **Authentication:** local sessions, API clients, API OIDC/JWT, and interactive Admin OIDC are supported v1 paths.
- **Shared packages:** coordinated sibling-package enhancements are allowed when the abstraction is reusable outside Cmsify and a released package is consumed rather than source-copied.

## Executive assessment

Cmsify has a credible pre-release foundation: all current automated tests pass, the API has meaningful integration coverage against PostgreSQL, mutable resources generally use ETags, errors usually use ProblemDetails, migrations use a PostgreSQL advisory lock, webhook retries use database leases, storage supports local and S3-compatible providers, and the production Compose example includes persistent data and health checks.

The largest v1 risk is not basic feature completeness. It is that several public surfaces can drift or publish independently:

- API controllers duplicate wire types that also exist in `Cmsify.Contracts`.
- the TypeScript generation check validates generated code against a pinned snapshot, but never proves that snapshot matches the running API;
- `@cmsify/client` claims behavior its high-level client does not safely provide;
- NuGet, npm, Docker, Git tags, and package licenses do not share one promotion/version policy;
- no test proves that the latest `0.1.x` deployment upgrades to v1 without losing data or media;
- documented Admin OIDC sign-in is not implemented; and
- initial webhook events and scheduled publication are not safe across crashes or multiple API replicas.

### Readiness ratings

| Dimension | Rating | Summary |
| --- | --- | --- |
| Correctness | 3/5 | Good domain and integration coverage; duplicated contracts and inconsistent error/pagination shapes remain. |
| Security | 3/5 | Strong baseline controls; OIDC is incomplete and webhook DNS validation does not pin the validated destination. |
| Performance | 2/5 | Useful indexes and bounded response pages exist, but resolved-content listing materializes all matching versions and performs per-item template lookups; no load budgets prove production limits. |
| Maintainability | 3/5 | Layering is clear and several shared packages are already used; SDK and controller duplication is costly. |
| Operational readiness | 2/5 | Health, Compose, backup guidance, and migration locking exist; upgrade, outbox, media consistency, and multi-replica guarantees do not. |
| SDK readiness | 2/5 | The .NET SDK is broad, but contract drift is unchecked; the TypeScript facade has release-blocking correctness and coverage gaps. |
| Release engineering | 1/5 | Stable artifacts can be published from different triggers with different versions and licenses. |

## Evidence collected

The following checks were run from a clean tracked worktree. `sdk/typescript/dist/` was created only as local build output.

| Check | Result |
| --- | --- |
| `dotnet build Cmsify.slnx --configuration Release --no-restore -p:DisableGitVersionTask=true` | Passed; 58 warnings, primarily nullable-flow warnings in Admin plus Sass/Bootstrap deprecations. |
| `dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal -p:DisableGitVersionTask=true` | Passed: Core 52, Infrastructure 37, API integration 34, Admin integration 18, .NET SDK 29; 170 total. |
| `npm run generate:check` | Passed against the pinned snapshot; this command is not currently a valid API-drift check, as described in F-01. |
| `npm run typecheck` | Passed. |
| `npm test` | Passed: 14 tests. |
| `npm run build` | Passed: ESM, CommonJS, declarations, and source maps generated. |
| `dotnet list Cmsify.slnx package --vulnerable --include-transitive` | No known NuGet vulnerabilities reported by configured sources. |
| `npm audit --omit=dev` | No known runtime dependency vulnerabilities reported. |

Passing current checks does not imply v1 readiness because several required gates do not exist yet. In particular, no current check compares live OpenAPI to the snapshot, installs the packed SDKs into clean consumers, starts the published containers, or upgrades a released database/media fixture.

## API and SDK surface matrix

“Broad” means the handwritten .NET client has a service group for the concern; it does not mean parity is enforced. TypeScript generated schema types are exported, but the generated `createCmsifyFetchClient` factory is not exported from the package root, so “schema only” is not an operable generated client surface for npm consumers.

| API concern | .NET client | TypeScript high-level facade | v1 action |
| --- | --- | --- | --- |
| Authentication/session | Broad | Login and current token info only | Decide and document local-session/OIDC lifecycle; add refresh, logout, password, and typed empty-response behavior where supported. |
| Workspaces | Broad CRUD | List plus client-side slug lookup | Require workspace ID for scoped calls or resolve slug to ID; add typed get/create/update/delete if the facade promises management. |
| Templates and versions | Broad | List/get only | Add the version, section, field, publish, and concurrency operations or explicitly position the facade as delivery-only. |
| Components | Broad | None | Expose typed generated access at minimum; add facade operations if management parity is the v1 promise. |
| Choice sets/picklists | Broad | None | Same decision as components; preserve immutable revision behavior in types and examples. |
| Content lifecycle/versions | Broad | Read/list/slug/translations only | Add or remove claims for create/update/review/approve/publish/schedule/archive/rollback/link operations. Test ETag behavior per resource URL. |
| Media | Broad | List/get/download only | Add upload/update/delete or document read-only scope; verify streaming and empty responses. |
| Tags | Broad | None | Provide generated typed access or add a facade group. |
| Webhooks/deliveries | Broad | None | Provide generated typed access; high-level helpers are optional if the raw generated client is public and documented. |
| Audit | Broad | None | Provide generated typed access and preserve paging/filter types. |
| Users and workspace grants | Broad | None | Provide generated typed access; keep this out of delivery-oriented examples. |
| API clients | Broad | None | Provide generated typed access and one-time-secret response types. |
| Account/storage settings | Broad | None | Provide generated typed access; do not expose server secrets in response types. |
| Model packages | Broad | None | Provide generated typed access including multipart import and binary export. |
| Health | Broad | Live/ready | Keep, with documented unauthenticated behavior and no retries by default. |

Before v1, choose one explicit TypeScript product shape: a complete management facade, or a smaller delivery facade backed by a public generated client for all remaining operations. The recommended option is the smaller curated facade plus a fully exported generated client; duplicating every OpenAPI operation by hand would recreate the drift problem.

## Prioritized findings

Priority uses `(Impact + Risk) × (6 - Effort)`, where each input is 1–5. Higher scores should be addressed first, but every Blocker and High finding remains a v1 release gate regardless of score.

### Blockers

#### F-01 — The API and SDK contract sources can drift undetected

- **Score:** Impact 5, Risk 5, Effort 2 → **40**
- **Evidence:** `PagedResponse<T>`, `ContentListQuery`, and `AuditQueryRequest` are independently declared in both the API and contracts (`src/Cmsify.Api/Controllers/PagedResponse.cs:3`, `ContentController.cs:1230`, `AuditController.cs:140`, and `src/Cmsify.Contracts/WireContracts.cs:4,182,188`). The TypeScript generator writes from `openapi.snapshot.json` directly into the tracked schema (`sdk/typescript/scripts/generate.mjs:7-14`). In check mode it compares only the small generated client string and still writes that file (`generate.mjs:20-24`).
- **Risk:** A controller change can compile, pass `generate:check`, and ship while the .NET contracts and TypeScript snapshot describe a different wire shape.
- **Required outcome:** Use shared `Cmsify.Contracts` types at the API boundary where practical; otherwise add explicit mappings and contract tests. Export OpenAPI from the built API in CI, compare it non-mutatingly with the checked-in snapshot, then regenerate TypeScript from that verified document. Add a breaking-change diff gate for `/api/v1`.

#### F-02 — `@cmsify/client` is not a truthful or safe v1 surface

- **Score:** Impact 5, Risk 5, Effort 3 → **30**
- **Evidence:** The package is hardcoded to `1.0.0` before public npm release (`sdk/typescript/package.json:3`). Documentation accepts a workspace “ID or slug” (`sdk/typescript/README.md:31`), while the client inserts the value into `{workspaceId:guid}` routes without resolving a slug (`sdk/typescript/src/client.ts:123-138`). The retry loop retries all methods on `429`/`5xx`, including POST requests (`client.ts:141-150`), supports only numeric `Retry-After`, and does not retry transport exceptions. Successful `204` responses fall through to `response.json()` (`client.ts:120`). Documentation says mutations are available (`README.md:68`), but the high-level facade exposes only login and read groups; the generated fetch client is not exported from `src/index.ts`.
- **Risk:** Documented configurations can 404; non-idempotent operations can be replayed; no-content operations can throw after succeeding; and consumers cannot rely on advertised typed coverage.
- **Required outcome:** Keep the package prerelease until the v1 train. Support workspace IDs explicitly or resolve slugs once to IDs. Retry only idempotent requests unless an idempotency key is present, support delta and HTTP-date `Retry-After`, surface transport/timeout behavior, handle empty success bodies, export the generated client, and either implement every advertised high-level operation or narrow the documentation. Add tests for each behavior.

#### F-03 — Artifact versioning, promotion, and licensing are inconsistent

- **Score:** Impact 5, Risk 5, Effort 3 → **30**
- **Evidence:** GitVersion publishes stable patches from `main` beginning at `0.1.x`, while TypeScript declares `1.0.0`. The NuGet/Docker workflow publishes on successful pushes to `main` and creates a tag afterward (`.github/workflows/publish-cmsify.yml:22-86`). npm publishes from a separate manual/GitHub Release workflow (`npm-publish-cmsify-client.yml:4-6`). The repository states AGPL-3.0-or-later, the .NET packages declare AGPL-3.0-only (`Cmsify.Contracts.csproj:15` and both client project files at line 15), and npm declares MIT (`package.json:5`).
- **Risk:** Consumers can receive mismatched SDK/server versions and incompatible license representations, and a release tag does not identify the inputs that triggered all publications.
- **Required outcome:** A reviewed `vX.Y.Z` tag must build and promote the API image, Admin image, all three NuGet packages, and npm package from one immutable commit. Server artifacts use AGPL-3.0-or-later; `Cmsify.Contracts`, both .NET clients, and `@cmsify/client` use MIT. Generate a GitHub Release and changelog entry from the same version and attach provenance/SBOM metadata.

#### F-04 — The latest prerelease-to-v1 upgrade path is unproved

- **Score:** Impact 5, Risk 5, Effort 3 → **30**
- **Implementation:** The moving-baseline implementation is defined by the [design](superpowers/specs/2026-08-27-moving-baseline-upgrade-rollback-design.md) and [implementation plan](superpowers/plans/2026-08-27-moving-baseline-upgrade-rollback.md). The checked-in [fixture manifest](../tests/upgrade/fixtures/v0.1.3/manifest.json) and [checksums](../tests/upgrade/fixtures/v0.1.3/SHA256SUMS), [operator runbook](../tests/upgrade/README.md), and [dedicated workflow](../.github/workflows/upgrade-rollback.yml) provide the fixture, exact-image rehearsal, moving-baseline gate, diagnostics, and rollback contract. Exact candidate evidence remains revision-specific and must be recorded only after the complete validation run passes.
- **Risk:** The first stable release could corrupt or strand the only data held by early adopters.
- **Required outcome:** Create a sanitized fixture from the latest `0.1.x` schema with representative workspaces, permissions, templates, components, choice revisions, content versions, schedules, media, webhooks, audit rows, and package provenance. Restore it, run v1 migrations, validate invariants and media reads, then exercise rollback instructions. Older prereleases must first upgrade to the documented latest `0.1.x` baseline.

#### F-05 — Admin OIDC is advertised but has no sign-in handler

- **Score:** Impact 4, Risk 4, Effort 3 → **24**
- **Evidence:** The login page links to `/signin-oidc` whenever `Auth:Oidc:Enabled` is true (`src/Cmsify.Admin/Components/Pages/Auth/Login.razor:31-41`). Admin registers only cookie authentication (`src/Cmsify.Admin/Program.cs:26-27`); no `AddOpenIdConnect` call exists. The API JWT path is separate and does not complete interactive Admin sign-in.
- **Risk:** Enabling a documented production authentication mode produces a broken login path.
- **Required outcome:** Register and configure OIDC sign-in, callback, sign-out, saved tokens, refresh, and API token forwarding. Preserve local Cmsify login as a parallel option. Cover successful login, mapped roles/workspace, expired/refresh-failed tokens, logout, invalid issuer/audience, and multi-instance token caching.

### High findings

#### F-06 — Initial webhook delivery is not durable

- **Score:** Impact 5, Risk 5, Effort 4 → **20**
- **Evidence:** Domain operations enqueue to a bounded in-memory channel (`InProcessWebhookQueue.cs:15-24`). A database delivery row is created only after the outbound POST finishes (`WebhookDeliveryProcessor.cs:19-42`). A crash after the domain commit but before or during channel processing loses the event and leaves no retry record.
- **Required outcome:** Persist an outbox event in the same database transaction as the domain change. Workers must claim rows with leases/`SKIP LOCKED`, create idempotent delivery records, retry safely, and retain dead-letter diagnostics. Document at-least-once delivery and stable event IDs so consumers can deduplicate.

#### F-07 — Scheduled publishing can duplicate work across replicas

- **Score:** Impact 5, Risk 5, Effort 4 → **20**
- **Evidence:** Each hosted service selects the same approved rows and publishes them independently (`InProcessScheduledPublishingDispatcher.cs:29-44`); there is no claim, lease, row lock, or concurrency predicate before snapshots and webhooks are produced.
- **Required outcome:** Atomically claim due content with a lease or `FOR UPDATE SKIP LOCKED`, make publication transition/version creation idempotent, and test two dispatchers racing against the same database.

#### F-08 — Webhook SSRF protection is vulnerable to DNS rebinding

- **Score:** Impact 4, Risk 5, Effort 4 → **18**
- **Evidence:** Cmsify resolves and validates DNS addresses (`WebhookDestinationValidator.cs:33`) and later asks `HttpClient` to resolve the hostname again (`WebhookDeliveryProcessor.cs:79`). Redirects are correctly disabled, but the validated address is not pinned to the connection.
- **Required outcome:** Pin outbound connections to a validated public address while preserving the original host for TLS/SNI, or route webhooks through an egress proxy that enforces public destinations. Revalidate on retries and test DNS rebinding, mixed public/private results, IPv4/IPv6 special ranges, redirects, and proxy behavior.

#### F-09 — Media database and blob state are not transactional or reconciled

- **Score:** Impact 4, Risk 4, Effort 3 → **24**
- **Status:** Remediated on `feature/readiness-audit`; final release-package publication/candidate certification remains approval-gated.
- **Evidence:** Upload now commits `PendingUpload` with the final deterministic key before storage and exposes only `Available` rows. Durable 30-day deletion intents use fenced, reclaimable leases and capped retry; stale uploads, missing/reappearing blobs, and old managed-prefix orphans are reconciled in bounded batches. Final local evidence: shared storage 91/91; Core 66/66; Infrastructure 292/292 with PostgreSQL/MinIO; API 69/69; Admin 29/29; .NET client 38/38; TypeScript generation/typecheck/40 tests/build; no EF/OpenAPI drift; independent review clean. Operator configuration, alerts, upgrade semantics, and pre-purge recovery are documented in `docs/operations.md`.
- **Required outcome:** Define retention semantics. Either delete blobs after a durable tombstone with retry, or explicitly retain them for a documented recovery window. Add orphan reconciliation, failed-upload cleanup, metrics, and local/S3 integration tests.

#### F-10 — API pagination and error contracts are inconsistent before freeze

- **Score:** Impact 4, Risk 4, Effort 2 → **32**
- **Evidence:** APIs mix `PagedResponse` (`page`, `pageSize`, `totalPages`) and `PagedResult` (`offset`, `limit`); templates accept page parameters but return the offset shape. Several authenticated-only branches return JSON strings from `BadRequest(...)` rather than the documented RFC 7807 body (`AuthController.cs:80,118`; `SettingsController.cs:33,46`).
- **Required outcome:** Choose one v1 pagination envelope and query convention, migrate all endpoints and SDKs before freeze, and require `application/problem+json` with stable error type, `traceId`, and correlation ID for every non-success response.

#### F-11 — Release builds are noisy and do not enforce first-party warning quality

- **Score:** Impact 3, Risk 3, Effort 2 → **24**
- **Evidence:** The release build emits 58 warnings, including nullable dereferences in Admin. Sass compilation reports deprecated `@import`, `if()`, and color APIs plus hundreds of collapsed Bootstrap warnings. No `TreatWarningsAsErrors` or equivalent repository policy exists.
- **Required outcome:** Resolve first-party nullable warnings, migrate Sass to supported module APIs/tooling, isolate unavoidable third-party warnings, and fail CI on new first-party compiler/analyzer warnings.

#### F-12 — CI does not certify shipped artifacts

- **Score:** Impact 4, Risk 4, Effort 3 → **24**
- **Evidence:** Accessibility runs only through `workflow_dispatch` (`admin-accessibility.yml:4`). Docker images are built and pushed but not started in a production-like smoke test. Packed NuGet/npm artifacts are not installed into clean consumer projects. No job checks package contents or the live OpenAPI snapshot.
- **Required outcome:** Pull or load the exact candidate images, start PostgreSQL/API/Admin, verify static assets, health, login, representative CRUD, media, and graceful restart. Install each packed SDK into clean .NET 10 and Node 20/22 consumers. Run accessibility on relevant Admin changes.

#### F-13 — .NET SDK resilience is incomplete and duplicated

- **Score:** Impact 4, Risk 3, Effort 3 → **21**
- **Evidence:** The client manually retries idempotent responses but not transport failures/timeouts; direct construction and DI use the same one-off loop. `SyntaxCircus.Http.Resilience` is centrally versioned but not referenced by any project.
- **Required outcome:** Extend the shared resilience package with a reusable handler/pipeline suitable for typed and directly constructed clients, then preserve `CmsifyApiException`, ProblemDetails extensions, correlation IDs, ETags, streaming, timeout budgets, and idempotency rules while removing duplicate policy code.

### Medium findings and enhancements

| ID | Finding | Required outcome |
| --- | --- | --- |
| F-14 | No API compatibility/deprecation policy beyond the `/api/v1` route and Swagger document. | Document additive-change rules, enum evolution, deprecation headers/windows, and the threshold for `/api/v2`; enforce with OpenAPI diffing. |
| F-15 | Release supply-chain controls are incomplete. | Pin third-party Actions by commit SHA, use npm trusted publishing/provenance where available, generate SBOMs, sign/attest images, and publish immutable digests. |
| F-16 | .NET toolchain restore is not fully reproducible. | Add `global.json`, intentional SDK roll-forward, locked NuGet restore, and scheduled dependency updates; standardize xUnit v3 across test projects. |
| F-17 | Resolved-content paging is implemented after full materialization, and no coverage, performance, or capacity budgets are enforced. `ListResolvedAsync` loads all matching published versions before applying filters and `Skip`/`Take` (`ContentController.cs:863-934`), then performs a template-name query for every returned item (`ContentController.cs:935-938,976-982`). | Push filtering, effective-version selection, sorting, and paging into bounded database queries; remove the per-item lookup; add query-count/load scenarios for hot reads, upload/stream limits, webhook throughput, coverage trend reporting, and documented SLO-oriented budgets. Do not use a coverage percentage as a substitute for behavioral tests. |
| F-18 | Secret-key validation and rotation are under-specified. | Validate production encryption-key entropy/format at startup and design versioned key rotation for encrypted webhook secrets. |
| F-19 | Project governance files are sparse. | Add `SECURITY.md`, a compatibility/support policy, vulnerability reporting instructions, release ownership, and a release runbook. |

## SyntaxCircus package disposition

Package reuse should remove mechanical infrastructure without forcing Cmsify’s domain rules into generic packages. Sibling changes are acceptable when the resulting abstraction is independently useful and released before Cmsify consumes it.

This disposition was checked against `_template/docs/syntaxcircus/PACKAGE_CATALOG.md` and the repository's central package pins. Cmsify currently consumes `AspNetCore.Common` 0.1.9, `AspNetCore.Serilog` 0.1.3, `Blazor.Components` 0.1.1, `DotEnv` 0.1.2, and `EntityFrameworkCore.Postgres` 0.1.3. `Http.Resilience` 0.1.6 is pinned but unused. Record the exact released versions of new or enhanced packages in `Directory.Packages.props` when each implementation change is approved; do not consume sibling source directly.

| Package | Decision | Cmsify action |
| --- | --- | --- |
| `SyntaxCircus.AspNetCore.Common` | **Keep** | Continue using correlation IDs, ProblemDetails, security headers, trusted proxies, rate-limit helpers, and health endpoints. Add contract tests for Cmsify-specific error mapping and middleware order. |
| `SyntaxCircus.AspNetCore.Serilog` | **Keep** | Retain standard host logging; add redaction and release telemetry guidance rather than creating local bootstrap code. |
| `SyntaxCircus.DotEnv` | **Keep** | Retain development-only dotenv loading and its precedence tests. |
| `SyntaxCircus.EntityFrameworkCore.Postgres` | **Keep** | Retain naming and advisory-lock migration helpers. Domain mappings and migration compatibility stay in Cmsify. |
| `SyntaxCircus.Blazor.Components` | **Keep** | Retain shared error/reconnect/not-found UI where already used; do not couple Cmsify domain UI into the package. |
| `SyntaxCircus.AspNetCore.Authentication` | **Enhance, then adopt** | Use shared JWT registration and standard ASP.NET Core schemes. Add a safe bearer credential extractor/composite selector so Cmsify can keep database-backed `cmsify_` clients, opaque local sessions, OIDC JWTs, and workspace/role claims without an `X-Api-Key` wire change. |
| `SyntaxCircus.Blazor.Auth` | **Enhance, then adopt** | Use it for Admin OIDC token forwarding, refresh, session-expiry state, and optional distributed token cache. Local Cmsify sessions remain a separate supported path. |
| `SyntaxCircus.Http.Resilience` | **Enhance, then adopt** | Expose a reusable pipeline/handler that honors `Retry-After`, transport faults, jitter, circuit telemetry, caller timeout budgets, and method idempotency. Integrate it into Admin and the .NET SDK without double retries. Remove the currently unused central pin until adoption or reference it in the implementing change. |
| `SyntaxCircus.Storage` | **Enhance, then adopt** | Add a separate S3-compatible provider package so local consumers do not inherit AWS dependencies. Support read metadata and safe disposal. Migrate Cmsify providers behind adapters while retaining Cmsify key generation, media authorization, and reconciliation policy. |
| `SyntaxCircus.Common` | **Selective** | Consider `PeriodicBackgroundService` after it supports the required time/test seams. Keep Cmsify’s actor/workspace authorization model and v1 pagination types; the shared current-user and pagination semantics are not equivalent. |
| `SyntaxCircus.AspNetCore.Common.MassTransit` | **Not applicable** | Cmsify has no MassTransit boundary. A durable PostgreSQL outbox does not justify adding a message bus for v1. |
| `SyntaxCircus.Credentials` | **Not applicable** | It owns desktop/local credential storage, not server secret management. |
| `SyntaxCircus.Email` | **Not applicable** | Cmsify currently sends no transactional email. |
| `SyntaxCircus.AI.Providers` | **Not applicable** | Cmsify has no AI provider integration. |
| `SyntaxCircus.Blazor.Seo`, `.Tracking`, `FancyBlazor` | **Not applicable** | The Admin is an authenticated operational application, not a public SEO/analytics/decorative site. |
| `SyntaxCircus.Maui.TokenStorage` | **Not applicable** | Cmsify ships no MAUI client. |
| `SyntaxCircus.RevenueCat`, `.RevenueCat.Maui` | **Not applicable** | Cmsify has no purchase or entitlement boundary. |

## Phased remediation backlog

### Phase 0 — Freeze a truthful public contract

1. Consolidate API boundary contracts and select one pagination/error convention.
2. Export live OpenAPI in CI, add non-mutating drift and breaking-change checks, and regenerate TypeScript only from the verified document.
3. Repair the TypeScript SDK behaviors in F-02, expose generated access, complete or narrow its high-level surface, and add Node 20/22 consumer tests.
4. Align MIT SDK and AGPL-3.0-or-later server metadata, package readmes, notices, changelog, and documentation.
5. Replace automatic stable publication from `main` with one tag-driven release candidate/promotion workflow.

**Exit criteria:** API, OpenAPI, .NET contracts, and both SDKs have one enforced contract; all package metadata agrees; clean consumers install candidate artifacts; no v1 version is published yet.

### Phase 1 — Close security and data-reliability blockers

1. Complete Admin OIDC with shared authentication/token-forwarding packages and end-to-end tests.
2. Implement a transactional webhook outbox and idempotent leased dispatch.
3. Add atomic claiming/idempotency for scheduled publishing.
4. Pin validated webhook destinations or enforce them through a hardened egress proxy.
5. Define media retention and add durable cleanup/reconciliation for local and S3 storage.
6. Build and verify the latest `0.1.x` upgrade fixture.

**Exit criteria:** authentication modes work end to end; a process crash or second replica cannot silently lose/duplicate scheduled work; upgrade and storage invariants pass.

### Phase 2 — Consolidate shared infrastructure and quality gates

1. Release and consume the required `SyntaxCircus.AspNetCore.Authentication`, `Blazor.Auth`, `Http.Resilience`, and `Storage` enhancements.
2. Eliminate first-party compiler/nullability warnings and obsolete Sass usage; enable warning enforcement.
3. Add deterministic SDK/toolchain locks, dependency automation, code coverage reporting, and focused capacity tests.
4. Add container, package-install, accessibility, and release-artifact smoke jobs.
5. Add security/support policies, release runbook, compatibility matrix, and upgrade/rollback runbook evidence.

**Exit criteria:** the release workflow certifies what it publishes, shared packages own only reusable mechanics, and CI is reproducible from a clean environment.

### Phase 3 — v1 release candidate certification

1. Cut a prerelease tag such as `v1.0.0-rc.1` through the unified workflow.
2. Deploy exact candidate digests to a production-like environment and perform upgrade, backup/restore, login/OIDC, CRUD, media, webhook, schedule, and restart smoke tests.
3. Run a soak period with release telemetry and no unresolved Blocker/High findings.
4. Freeze OpenAPI and SDK signatures, finalize `CHANGELOG.md`, create the stable tag, and promote the already-certified artifacts without rebuilding them.

**Exit criteria:** all gates below are checked with links to their immutable CI evidence and artifact digests.

## v1 go/no-go gates

- [ ] No unresolved Blocker or High finding in this report.
- [ ] First-party Release build has zero compiler/analyzer/nullability warnings; unavoidable third-party tool warnings are isolated and documented.
- [ ] Full .NET, TypeScript, accessibility, contract, upgrade, container, and clean-consumer suites pass.
- [ ] Live OpenAPI matches the checked-in contract and reports no unapproved breaking `/api/v1` change.
- [ ] API, Admin, NuGet, npm, GitHub Release, and Docker artifacts share one SemVer version and source commit.
- [ ] Server artifacts declare AGPL-3.0-or-later; .NET and TypeScript SDK artifacts declare MIT.
- [ ] .NET 10 and Node 20+ server-fetch support matrices are tested and documented.
- [ ] Latest `0.1.x` database/media state upgrades to v1 and passes invariant checks; rollback has been rehearsed.
- [ ] Local sessions, workspace-scoped API clients, and Admin/API OIDC pass end-to-end security tests.
- [ ] Webhooks and scheduled publication pass crash-recovery and two-replica race tests.
- [ ] Candidate containers pass production-like health, static asset, persistence, backup/restore, and restart tests.
- [ ] NuGet, npm, container, and GitHub artifacts include provenance/SBOM evidence and immutable digests.
- [ ] Documentation describes compatibility, deprecation, support, vulnerability reporting, configuration, upgrades, and rollback without prerelease contradictions.

Medium findings may be deferred only with an owner, rationale, target milestone, and documented v1 limitation. Passing tests alone cannot waive a release gate whose scenario is not covered by those tests.
