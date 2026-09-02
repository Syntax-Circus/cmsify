# Cmsify v1 Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task. Every behavioral change follows strict red-green-refactor TDD and every task receives an independent spec-and-quality review.

**Goal:** Close every finding and go/no-go gate in `docs/v1-release-readiness.md`, producing a certifiable Cmsify v1 release from immutable artifacts.

**Architecture:** Remediate contract truth first, then operational reliability and security, then quality and certification. Public wire contracts live in `Cmsify.Contracts`; generated clients derive from exported live OpenAPI; durable database state coordinates multi-replica work; released `SyntaxCircus.*` packages own only reusable mechanics.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core/PostgreSQL, Blazor, TypeScript/Node 20+, OpenAPI, Docker/OCI, GitHub Actions.

**Spec:** `docs/v1-release-readiness.md`

## Global Constraints

- Server/repository artifacts are AGPL-3.0-or-later; `Cmsify.Contracts`, both .NET clients, and `@syntaxcircus/cmsify-client` are MIT.
- The .NET SDK supports .NET 10 only for v1.
- The TypeScript SDK supports Node 20+ and compatible server/edge fetch runtimes; browser bundles are unsupported.
- One reviewed `vX.Y.Z` tag promotes every public artifact from one immutable commit; branch builds never publish or tag.
- Every non-success HTTP response is RFC 7807 `application/problem+json` with stable type, `traceId`, `correlationId`, and `X-Correlation-Id`.
- Public pagination uses `page`/`pageSize` and `{items,totalCount,page,pageSize,totalPages}`; invalid bounds return 400.
- Mutable resources preserve ETag/If-Match optimistic concurrency.
- Webhooks are at-least-once with stable event IDs; scheduled publication and webhook processing are safe across crashes and replicas.
- The latest published `0.1.x` database/media fixture must upgrade to v1; the baseline may move and each new `0.1.x` release must refresh it.
- Generated TypeScript files are never hand-edited.
- Sibling package changes must be independently reusable, released, and consumed by version; never source-copy sibling code.
- No Blocker, High, or Medium audit finding is deferred unless the audit records an owner, rationale, target milestone, and documented v1 limitation.

---

### Task 1: Freeze the public HTTP contract

**Files:** Modify `src/Cmsify.Contracts/WireContracts.cs`, API controllers/error plumbing, .NET/Admin consumers, and focused API/SDK tests.

**Produces:** A single public `PagedResponse<T>` and shared query contracts; all `/api/v1` list endpoints use page/pageSize; all failures use the required ProblemDetails contract.

- [ ] Write failing contract and integration tests for pagination envelopes, invalid bounds, shared serialization, ProblemDetails fields/content type, and correlation echo.
- [ ] Run focused tests and record the expected failures.
- [ ] Remove API-local wire duplicates, map internal offset paging at controller boundaries, and standardize failure generation.
- [ ] Update .NET/Admin consumers and nearest integration documentation.
- [ ] Run focused tests, full affected builds, and record clean passing evidence.
- [ ] Commit the task and self-review the diff.

### Task 2: Make live OpenAPI the generated-client authority

**Files:** Modify API Swagger registration, repository tool manifest/scripts, TypeScript generator/snapshot/generated output, and CI contract workflow.

**Consumes:** Task 1 shared HTTP contract.

**Produces:** Deterministic live OpenAPI export, non-mutating drift checks, generated-output verification, and an `oasdiff 1.28.0` breaking-change gate.

- [ ] Write failing exporter/generator behavior tests proving live drift and tracked-file mutation are detected.
- [ ] Run them and record expected failures.
- [ ] Add a local Swashbuckle CLI matching runtime `10.2.3`, reusable Swagger registration, cross-platform export/update/check commands, and temp-directory generation.
- [ ] Add target-branch breaking diff enforcement with protected `api-breaking-change-approved` override evidence.
- [ ] Regenerate from live OpenAPI and update integration documentation.
- [ ] Run exporter, generate check, typecheck, SDK tests/build, API integration tests, and commit.

### Task 3: Repair the TypeScript delivery SDK

**Files:** Modify `sdk/typescript/src`, tests, package exports, README, and server-side examples.

**Consumes:** Task 2 verified generated client/types.

**Produces:** Delivery facade plus exported raw generated client; explicit GUID `workspaceId`; safe retry, timeout, cancellation, empty-body, ETag, and error behavior.

- [ ] Write failing behavioral tests for every promised retry/timeout/cancellation/204/export/workspace behavior.
- [ ] Run them and record expected failures.
- [ ] Implement the minimal typed behavior, alias generated schemas where compatible, and remove browser/management claims.
- [ ] Add Node 20/22 clean-consumer pack/install/compile coverage.
- [ ] Run generation, typecheck, tests, build, consumer checks, and commit.

### Task 4: Align licenses, versions, and immutable tag promotion

**Files:** Modify package metadata/licenses/notices/changelog, GitVersion/version plumbing, Docker metadata, and GitHub workflows.

**Produces:** Branch validation without publication; tag-only build-once candidate certification and protected promotion of NuGet, npm, OCI, GitHub Release, SBOM, checksums, and provenance.

- [ ] Add executable workflow/package verification that fails against current auto-publication, mismatched metadata, and mutable rebuild behavior.
- [ ] Run checks and record expected failures.
- [ ] Align MIT SDK and AGPL-3.0-or-later server contents; use a non-publishable source version and validated tag-derived release version.
- [ ] Build artifacts once, certify exact files/images, require protected release approval, and publish without rebuilding; pin changed actions by SHA.
- [ ] Add clean .NET 10 and Node 20/22 consumers, OCI smoke checks, SPDX SBOMs, provenance, checksums, and changelog validation.
- [ ] Run build-only workflow verification, package inspection, focused/full suites, and commit.

### Task 5: Implement and adopt complete Admin OIDC

**Files:** Enhance/release `SyntaxCircus.AspNetCore.Authentication` and `SyntaxCircus.Blazor.Auth`, then modify Cmsify API/Admin auth configuration, token forwarding/cache, tests, and docs.

**Produces:** Parallel local and OIDC login/logout, token save/refresh/forwarding, role/workspace mapping, invalid-token handling, and multi-instance distributed caching.

- [ ] Add failing sibling-package and Cmsify end-to-end tests for all F-05 scenarios.
- [ ] Implement reusable package mechanics, release them, consume exact versions, and implement Cmsify-specific mappings.
- [ ] Run package suites, Admin/API integration and security tests, documentation checks, and commit each repository independently.

### Task 6: Make webhook and scheduled work durable

**Files:** Modify Core entities/contracts, EF mappings/migrations, webhook and scheduled dispatchers, integration tests, metrics, and operations docs.

**Produces:** Transactional webhook outbox, stable event IDs, leased SKIP LOCKED claims, idempotent delivery/publication, retries/dead letters, and two-replica/crash recovery.

- [ ] Add failing PostgreSQL integration tests for commit/crash windows, lease expiry, duplicate workers, retry, dead-letter, and two-dispatcher schedule races.
- [ ] Implement minimal transactional state and workers; preserve snapshot/pick-list invariants.
- [ ] Run Infrastructure/API integration and concurrency suites, migration tests, and commit.

### Task 7: Harden webhook egress and secret rotation

**Files:** Modify destination validation/connection handling, encryption configuration/storage, tests, metrics, and security docs.

**Produces:** DNS-rebinding-safe pinned connections with original TLS host, retry revalidation, special-range rejection, redirects disabled, production key validation, and versioned key rotation.

- [ ] Add failing tests for rebinding, mixed DNS, IPv4/IPv6 special ranges, redirects, proxy behavior, key entropy/format, and rotation.
- [ ] Implement pinned/public egress and versioned encryption with backwards reads and explicit rotation.
- [ ] Run focused security/integration suites and commit.

### Task 8: Reconcile media database and blob state

**Files:** Enhance/release `SyntaxCircus.Storage`, then modify Cmsify media persistence/workers/configuration, migrations, metrics, tests, and operations docs.

**Produces:** Durable tombstones, documented recovery retention, upload/delete retry state, orphan reconciliation, safe local/S3 behavior, metadata reads, and disposal.

- [ ] Add failing local/S3/Testcontainers tests for failed database writes, failed deletes, retention expiry, reconciliation, restart, and authorization.
- [ ] Implement/release reusable storage mechanics and Cmsify-owned key/retention/reconciliation policy.
- [ ] Run sibling and Cmsify storage/integration suites and commit each repository independently.

### Task 9: Prove moving-baseline upgrades and rollback

**Files:** Add sanitized versioned PostgreSQL/media fixtures, manifests/checksums, upgrade/rollback harness, CI job, and runbook evidence.

**Produces:** Latest published `0.1.x` fixture containing every required domain concern, v1 migration/invariant/media validation, and rehearsed rollback.

- [ ] Build the current latest fixture from the published release and write failing upgrade/invariant tests.
- [ ] Implement deterministic restore/migrate/validate/rollback automation and enforce fixture refresh before any later `0.1.x` tag.
- [ ] Run the full upgrade and rollback job against exact images and commit.

### Task 10: Consolidate HTTP resilience

**Files:** Enhance/release `SyntaxCircus.Http.Resilience`, then modify .NET SDK/Admin HTTP construction and tests.

**Produces:** One reusable pipeline for DI/direct clients with Retry-After, transport faults, jitter, circuit telemetry, timeout budgets, and method idempotency while preserving Cmsify exceptions, correlations, ETags, and streaming.

- [ ] Add failing sibling and Cmsify tests for direct/DI parity and every preserved behavior.
- [ ] Implement/release the reusable pipeline, consume it once per client, and remove duplicate retry loops.
- [ ] Run sibling, SDK, Admin integration, streaming, and clean-consumer suites and commit each repository independently.

### Task 11: Enforce reproducible warning-free quality and capacity

**Files:** Modify toolchain/package locks, build policy, Admin Sass/nullability, resolved-content queries, dependency automation, coverage/capacity tests, and performance docs.

**Produces:** Pinned .NET SDK/locked restore, xUnit v3 consistency, zero first-party warnings with enforcement, supported Sass modules, bounded resolved-content SQL without N+1, and documented SLO-oriented budgets.

- [ ] Add failing query-count/load and warning/toolchain policy checks.
- [ ] Push resolved filtering/effective selection/sorting/paging into bounded queries and remove per-item lookups.
- [ ] Fix first-party warnings/Sass deprecations, isolate third-party warnings, enable enforcement, locks, updates, and coverage trends.
- [ ] Run Release build, full suites, query/load checks, and commit.

### Task 12: Certify shipped artifacts and governance

**Files:** Modify CI/release workflows, production-like smoke harness, accessibility triggers, security/support/compatibility/release/upgrade documentation, and the readiness audit evidence ledger.

**Produces:** Exact-candidate container/package/accessibility/backup/restart certification, pinned supply-chain controls and immutable digests, API compatibility/deprecation policy, governance files, and checked go/no-go evidence.

Certification evidence is tracked in [`docs/evidence/task-12-local-verification.json`](../../evidence/task-12-local-verification.json). Cmsify `v0.2.1` is certified and released from source `26c064a81411c1ec303fa1dc07813841760d44ea`; all eight external gates passed. The Task 9 rollback diagnostic omission and both historical Task 8 media races were adjudicated closed as release blockers.

- [x] Add failing artifact, accessibility-trigger, policy-presence, and compatibility/deprecation checks.
- [x] Implement production-like PostgreSQL/API/Admin smoke, CRUD/media/OIDC/webhook/schedule/restart/backup coverage and clean consumers.
- [x] Add `SECURITY.md`, support/compatibility/deprecation policies, ownership, vulnerability reporting, and release/rollback runbooks.
- [x] Run every audit gate, update immutable evidence links/digests, perform whole-branch review, and commit.

## Completion Gate

- [x] Re-read every F-01 through F-19 required outcome and every go/no-go checkbox against current evidence.
- [x] Run warning-free Release build; full .NET, TypeScript, accessibility, OpenAPI, upgrade, container, clean-consumer, security, concurrency, storage, and capacity suites.
- [x] Verify exact SemVer/source SHA/license/SBOM/provenance/digest agreement across all candidate artifacts.
- [x] Obtain clean per-task reviews and one clean whole-branch review.
- [x] Complete the final handoff under explicit user authorization; publication and release occurred only through the reviewed protected workflow.
