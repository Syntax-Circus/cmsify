# Cmsify v1 release-readiness audit

**Original audit date:** 2026-08-24

**Preliminary local refresh:** 2026-08-30

**Accepted implementation revision:** `a3454f3bcdfafd9688858c2cc3a2d0f569d3b48e` (`feature/readiness-audit`)

**Decision:** Not ready to label `1.0.0`. The repository remediation is implemented at the local source/policy level, but the public dependency, definitive candidate, hosted, approval, signing, promotion, soak, tag, and final-release evidence is still absent.

This report is a release gate, not a public delivery commitment. It covers the HTTP API, the handwritten .NET contracts and clients, `@cmsify/client`, published containers, production operation, and reuse of sibling `SyntaxCircus.*` packages.

## Task 12 local evidence ledger

The [Task 12 local evidence manifest](evidence/task-12-local-verification.json) is bound to accepted implementation SHA `a3454f3bcdfafd9688858c2cc3a2d0f569d3b48e`. It is a **preliminary local source/policy tuple**, not release certification: current checks are release contracts 504/504, release-smoke source tests 91/91, upgrade unit tests 173/173, and the standalone semantic verifier. The prior product sweep at `4d9da511303e646c5f4147f51108bf3d87c4bba0` passed the Release build with zero warnings/errors, the full .NET solution 599/599 with zero skips, and the TypeScript/OpenAPI generation, typecheck, 40/40 tests, and build. Subsequent accepted changes affect release policy, smoke evidence, workflows, and their tests rather than compiled product code.

The preserved API OCI archive also completed reviewed **offline-loader live certification** at loader source `4d9da511303e646c5f4147f51108bf3d87c4bba0`: exact manifest digest, Docker image/config identity, platform, labels, ordered DiffIDs, and cleanup were verified. That archive was built from older source `a8e2218c530b4323e8e44ca0cf25b3d22e2aea4d`, covers only the API image, and is explicitly non-promotable. It is not the definitive same-source package/API/Admin tuple.

Every public, definitive-candidate, final-consumer/accessibility/upgrade/smoke, hosted-accessibility, protected-approval, attestation, registry-signing, immutable-promotion, soak, stable-tag, and final-release gate remains false and unperformed in the manifest, with an owner and next command. The overall v1 decision remains **not ready**.

**Remediation update (2026-08-30):** Tasks 1–11 remain implemented and validated locally; F-11, F-16, and F-17 are remediated at the local source/test level through their recorded Task 11 implementation `bdaa0ff4a8f6d5e9b6692575f57a524e925a9ca4`. Task 12 repository implementation and the pre-publication review fixes are now present through `a3454f3bcdfafd9688858c2cc3a2d0f569d3b48e`: exact-candidate workflows and verifiers, clean-consumer and production-like smoke harnesses, accessibility policy, API compatibility/deprecation enforcement, governance/runbooks, supply-chain controls, offline OCI transport, complete attestation-subject verification, and certified-manifest smoke identity binding. Consequently F-01 through F-19 are remediated at the local source/policy level; their remaining clauses below distinguish external release certification from an active repository defect.

The tested `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` bytes still exist only in the ignored local feed and were built from sibling feature-branch source. They are preliminary local evidence and must not be published. A fresh official flat-container listing on 2026-08-30 contained only versions 0.1.1 through 0.1.6; this availability observation is not public-restore proof. The [post-merge release handoff](superpowers/plans/2026-08-30-post-merge-release-handoff.md) requires both repository changes to merge first, then packs and approves the resilience candidate from the sibling default branch, reconciles any identity/lock changes into Cmsify, and publishes the exact `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` candidate only through the trusted default-branch/release workflow. The release operator then runs the hardened isolated public gate, builds the definitive tuple, and executes every final hosted gate. No publication, push, merge, tag, signature, attestation, promotion, or release is claimed.

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

This section preserves the 2026-08-24 audit baseline and rationale. It is not the current defect ledger; the 2026-08-30 remediation update and each finding's **Status** line govern current source/policy state.

At the original audit, Cmsify had a credible pre-release foundation: all then-current automated tests passed, the API had meaningful integration coverage against PostgreSQL, mutable resources generally used ETags, errors usually used ProblemDetails, migrations used a PostgreSQL advisory lock, webhook retries used database leases, storage supported local and S3-compatible providers, and the production Compose example included persistent data and health checks.

The largest v1 risks identified by that original audit were not basic feature completeness, but public surfaces that could drift or publish independently:

- API controllers duplicate wire types that also exist in `Cmsify.Contracts`.
- the TypeScript generation check validates generated code against a pinned snapshot, but never proves that snapshot matches the running API;
- `@cmsify/client` claims behavior its high-level client does not safely provide;
- NuGet, npm, Docker, Git tags, and package licenses do not share one promotion/version policy;
- no test proves that the latest `0.1.x` deployment upgrades to v1 without losing data or media;
- documented Admin OIDC sign-in is not implemented; and
- initial webhook events and scheduled publication are not safe across crashes or multiple API replicas.

### Historical readiness ratings (superseded 2026-08-30)

These ratings are the original 2026-08-24 baseline, retained to explain prioritization. They are superseded by the [preliminary Task 12 evidence ledger](evidence/task-12-local-verification.json) and the explicit F-01 through F-19 status dispositions below.

| Dimension | Rating | Summary |
| --- | --- | --- |
| Correctness | 3/5 | Original audit: duplicated contracts and inconsistent error/pagination shapes required remediation. |
| Security | 3/5 | Original audit: OIDC and webhook destination pinning required remediation. |
| Performance | 2/5 | Original audit: resolved-content execution and production-capacity evidence required remediation. |
| Maintainability | 3/5 | Original audit: SDK and controller duplication required remediation. |
| Operational readiness | 2/5 | Original audit: upgrade, outbox, media consistency, and multi-replica guarantees required remediation. |
| SDK readiness | 2/5 | Original audit: contract drift and TypeScript correctness/coverage required remediation. |
| Release engineering | 1/5 | Original audit: artifact triggers, versions, licensing, and certification required remediation. |

Current disposition: those repository concerns are remediated at the local source/policy level. These historical scores must not be used as current readiness ratings; only the unperformed public, definitive-candidate, hosted, approval, signing, promotion, soak, tag, and final-release gates keep the decision at **not ready**.

## Historical evidence collected (superseded 2026-08-30)

The following checks were the original 2026-08-24 baseline from a clean tracked worktree. They are retained for provenance and are superseded by the current preliminary tuple above. `sdk/typescript/dist/` was created only as local build output.

| Check | Result |
| --- | --- |
| `dotnet build Cmsify.slnx --configuration Release --no-restore -p:DisableGitVersionTask=true` | Passed; 58 warnings, primarily nullable-flow warnings in Admin plus Sass/Bootstrap deprecations. |
| `dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal -p:DisableGitVersionTask=true` | Passed: Core 52, Infrastructure 37, API integration 34, Admin integration 18, .NET SDK 29; 170 total. |
| `npm run generate:check` | Original audit result: passed against the pinned snapshot, which was not then a valid live API-drift check. |
| `npm run typecheck` | Passed. |
| `npm test` | Passed: 14 tests. |
| `npm run build` | Passed: ESM, CommonJS, declarations, and source maps generated. |
| `dotnet list Cmsify.slnx package --vulnerable --include-transitive` | No known NuGet vulnerabilities reported by configured sources. |
| `npm audit --omit=dev` | No known runtime dependency vulnerabilities reported. |

At the original audit, these checks did not cover live OpenAPI comparison, packed clean consumers, exact candidate containers, or a released database/media upgrade. Those repository gates and harnesses are now implemented and source/policy-tested. Their definitive public and hosted executions remain unperformed.

### Task 11 quality and capacity evidence

Task 11's committed implementation range is `a482873..bdaa0ff`; exact commands and interpretation are in [Quality and capacity operations](performance.md). The final policy-only commit `bdaa0ff4a8f6d5e9b6692575f57a524e925a9ca4` passed the semantic quality policy 17/17, the complete release-contract set 232/232, the approved ignored-feed locked restore for all twelve projects, a forced non-incremental Release build with 0 warnings and 0 errors, and both Dockerfile BuildKit static checks. No runtime, package, or lock graph changed in that last fix round.

The [checked Task 11 evidence manifest](evidence/task-11-local-verification.json) is the machine-readable authority for the local verification tuple and report contracts. Evidence tuple: source SHA `e72b4681158cf687f0462bb2aa29f9ed47771e49`; SDK `10.0.400`; Core 66, .NET client 71, Admin 35, Infrastructure 303, API 112; full 587; coverage 587; coverage reports 5; coverage schema `cmsify.coverage.v1`.

Fresh documentation-review validation ran from committed source `e72b4681158cf687f0462bb2aa29f9ed47771e49`, whose direct history contains the tested Task 11 implementation. Fresh committed-tree full solution: 587/587 passed (Core 66 + .NET client 71 + Admin 35 + Infrastructure 303 + API 112 = 587). A separate XPlat collection passed the same 587/587 tests, emitted exactly five fresh Cobertura reports, and the coverage summarizer produced `cmsify.coverage.v1` with `sourceSha` `e72b4681158cf687f0462bb2aa29f9ed47771e49`. The direct capacity filters remain API 10/10, Infrastructure 6/6, and client 4/4 at workflow implementation `ce9629417b794c9828a0e686dca6e1f846609877`. Coverage percentages remain trend-only. The committed capacity runner at `05984108b84d708f3fae90260d971a387af71960` passed its 15/15 schema/runner contracts and emitted validated `cmsify.capacity.v1` output with all blocking invariants true; a resolved-content p95 miss was represented as a warning-only diagnostic, proving timing does not replace correctness.

These are local source/test results, not hosted or public certification. Runtime-image digest pins, the repository-wide action-pin audit, SBOM/provenance/signing contracts, accessibility triggers, production-like artifact smoke, and governance are implemented. What remains open is the exact public locked restore followed by definitive-candidate execution, clean consumers/accessibility/upgrade/smoke, hosted evidence and approvals, actual attestations/signatures, immutable promotion, soak, stable tag, and final release.

## Historical API and SDK surface matrix (superseded 2026-08-30)

This matrix records the original 2026-08-24 surface and proposed actions. It is historical, not a list of current missing work. The remediation exported generated TypeScript access, aligned the documented curated facade, enforced the live OpenAPI contract, and added clean-consumer certification policy.

| API concern | Original .NET client | Original TypeScript facade | Original action (completed locally) |
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

Current disposition: the repository adopted the curated facade plus public generated-client shape and mutation-tests the API/OpenAPI/SDK boundary. The remaining TypeScript work is not source design; it is installing the definitive packed artifact in the final clean Node consumers and retaining the hosted compatibility evidence.

## Prioritized findings

Priority uses `(Impact + Risk) × (6 - Effort)`, where each input is 1–5. Higher scores should be addressed first, but every Blocker and High finding remains a v1 release gate regardless of score.

Finding titles, scores, original evidence, risks, and required outcomes below preserve the audit trail. They do not override the explicit **Status** line (or status text in the medium-findings table), which records current remediation and separates completed repository work from unperformed release certification.

### Blockers

#### F-01 — The API and SDK contract sources can drift undetected

- **Score:** Impact 5, Risk 5, Effort 2 → **40**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`; the live OpenAPI comparison and protected breaking-change path still require definitive hosted execution.
- **Evidence:** `PagedResponse<T>`, `ContentListQuery`, and `AuditQueryRequest` are independently declared in both the API and contracts (`src/Cmsify.Api/Controllers/PagedResponse.cs:3`, `ContentController.cs:1230`, `AuditController.cs:140`, and `src/Cmsify.Contracts/WireContracts.cs:4,182,188`). The TypeScript generator writes from `openapi.snapshot.json` directly into the tracked schema (`sdk/typescript/scripts/generate.mjs:7-14`). In check mode it compares only the small generated client string and still writes that file (`generate.mjs:20-24`).
- **Risk:** A controller change can compile, pass `generate:check`, and ship while the .NET contracts and TypeScript snapshot describe a different wire shape.
- **Required outcome:** Use shared `Cmsify.Contracts` types at the API boundary where practical; otherwise add explicit mappings and contract tests. Export OpenAPI from the built API in CI, compare it non-mutatingly with the checked-in snapshot, then regenerate TypeScript from that verified document. Add a breaking-change diff gate for `/api/v1`.

#### F-02 — `@cmsify/client` is not a truthful or safe v1 surface

- **Score:** Impact 5, Risk 5, Effort 3 → **30**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`; clean packed-candidate consumers remain an unperformed final gate.
- **Evidence:** The package is hardcoded to `1.0.0` before public npm release (`sdk/typescript/package.json:3`). Documentation accepts a workspace “ID or slug” (`sdk/typescript/README.md:31`), while the client inserts the value into `{workspaceId:guid}` routes without resolving a slug (`sdk/typescript/src/client.ts:123-138`). The retry loop retries all methods on `429`/`5xx`, including POST requests (`client.ts:141-150`), supports only numeric `Retry-After`, and does not retry transport exceptions. Successful `204` responses fall through to `response.json()` (`client.ts:120`). Documentation says mutations are available (`README.md:68`), but the high-level facade exposes only login and read groups; the generated fetch client is not exported from `src/index.ts`.
- **Risk:** Documented configurations can 404; non-idempotent operations can be replayed; no-content operations can throw after succeeding; and consumers cannot rely on advertised typed coverage.
- **Required outcome:** Keep the package prerelease until the v1 train. Support workspace IDs explicitly or resolve slugs once to IDs. Retry only idempotent requests unless an idempotency key is present, support delta and HTTP-date `Retry-After`, surface transport/timeout behavior, handle empty success bodies, export the generated client, and either implement every advertised high-level operation or narrow the documentation. Add tests for each behavior.

#### F-03 — Artifact versioning, promotion, and licensing are inconsistent

- **Score:** Impact 5, Risk 5, Effort 3 → **30**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`; no definitive same-source tuple has been built, attested, promoted, tagged, or released.
- **Evidence:** GitVersion publishes stable patches from `main` beginning at `0.1.x`, while TypeScript declares `1.0.0`. The NuGet/Docker workflow publishes on successful pushes to `main` and creates a tag afterward (`.github/workflows/publish-cmsify.yml:22-86`). npm publishes from a separate manual/GitHub Release workflow (`npm-publish-cmsify-client.yml:4-6`). The repository states AGPL-3.0-or-later, the .NET packages declare AGPL-3.0-only (`Cmsify.Contracts.csproj:15` and both client project files at line 15), and npm declares MIT (`package.json:5`).
- **Risk:** Consumers can receive mismatched SDK/server versions and incompatible license representations, and a release tag does not identify the inputs that triggered all publications.
- **Required outcome:** A reviewed `vX.Y.Z` tag must build and promote the API image, Admin image, all three NuGet packages, and npm package from one immutable commit. Server artifacts use AGPL-3.0-or-later; `Cmsify.Contracts`, both .NET clients, and `@cmsify/client` use MIT. Generate a GitHub Release and changelog entry from the same version and attach provenance/SBOM metadata.

#### F-04 — Latest prerelease-to-v1 upgrade and rollback

- **Score:** Impact 5, Risk 5, Effort 3 → **30**
- **Status:** Remediated locally on `feature/readiness-audit` for exact tested source `26bd2047b906c9ef3c4b7776447a7a44f8ca4a7c`; protected release certification remains required for a real tag.
- **Implementation:** The moving-baseline implementation is defined by the [design](superpowers/specs/2026-08-27-moving-baseline-upgrade-rollback-design.md) and [implementation plan](superpowers/plans/2026-08-27-moving-baseline-upgrade-rollback.md). The checked-in [fixture manifest](../tests/upgrade/fixtures/v0.1.3/manifest.json) and [checksums](../tests/upgrade/fixtures/v0.1.3/SHA256SUMS), [operator runbook](../tests/upgrade/README.md), and [dedicated workflow](../.github/workflows/upgrade-rollback.yml) provide the fixture, exact-image rehearsal, moving-baseline gate, diagnostics, and rollback contract.
- **Fresh evidence:** The consolidated final review wave closed all six findings: fresh-runner manifest-derived baseline pulls, post-fence resource identity/ownership reinspection, legacy full-tick ETag compatibility with normalized emission, bounded allow-listed service/migration/assertion/readiness diagnostics, the shared active global-Admin invariant, and deterministic seed/generator provenance. The no-cache production Dockerfile/public-feed build produced exact linux/amd64 candidate image ID `sha256:5bf4175b8c81140ad57f441d7b3d27018479c831f7188b840893ee96fc8103a0`, OCI version `1.0.0-task9-final`, and revision `26bd2047b906c9ef3c4b7776447a7a44f8ca4a7c`. Runs `cmsify-upgrade-8e310004e422` and `cmsify-upgrade-6c97c20e0382` each passed all eleven phases with 27 baseline, 30 candidate, and 28 rollback assertions; their independently created matched-backup manifest digests were `240c0ae3cf8089081b4c173661cffbd942c1371ef8e1e076f62d7a7881513587` and `32fb0ae056fe2570d6385c44ab7e85f70c43f5fb2fe6c91a440c0c705beb5099`. The post-run audit found zero owned containers, volumes, and networks. Fixture verification, byte-identical regeneration, 352 fast Node/release tests, focused Infrastructure 292/292 and API 71/71 tests, TypeScript generation/typecheck/40 tests/build, the Release build, and the full 496/496 solution test passed. Full identities, commands, environment facts, and limitations are recorded in the [remediation handoff](v1-release-remediation-handoff.md#task-9-final-validation-and-evidence).
- **Risk:** The first stable release could corrupt or strand the only data held by early adopters.
- **Required outcome:** Create a sanitized fixture from the latest `0.1.x` schema with representative workspaces, permissions, templates, components, choice revisions, content versions, schedules, media, webhooks, audit rows, and package provenance. Restore it, run v1 migrations, validate invariants and media reads, then exercise rollback instructions. Older prereleases must first upgrade to the documented latest `0.1.x` baseline.

#### F-05 — Admin OIDC is advertised but has no sign-in handler

- **Score:** Impact 4, Risk 4, Effort 3 → **24**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`; final OIDC artifact-smoke evidence remains unperformed.
- **Evidence:** The login page links to `/signin-oidc` whenever `Auth:Oidc:Enabled` is true (`src/Cmsify.Admin/Components/Pages/Auth/Login.razor:31-41`). Admin registers only cookie authentication (`src/Cmsify.Admin/Program.cs:26-27`); no `AddOpenIdConnect` call exists. The API JWT path is separate and does not complete interactive Admin sign-in.
- **Risk:** Enabling a documented production authentication mode produces a broken login path.
- **Required outcome:** Register and configure OIDC sign-in, callback, sign-out, saved tokens, refresh, and API token forwarding. Preserve local Cmsify login as a parallel option. Cover successful login, mapped roles/workspace, expired/refresh-failed tokens, logout, invalid issuer/audience, and multi-instance token caching.

### High findings

#### F-06 — Initial webhook delivery is not durable

- **Score:** Impact 5, Risk 5, Effort 4 → **20**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`; final exact-candidate webhook smoke remains unperformed.
- **Evidence:** Domain operations enqueue to a bounded in-memory channel (`InProcessWebhookQueue.cs:15-24`). A database delivery row is created only after the outbound POST finishes (`WebhookDeliveryProcessor.cs:19-42`). A crash after the domain commit but before or during channel processing loses the event and leaves no retry record.
- **Required outcome:** Persist an outbox event in the same database transaction as the domain change. Workers must claim rows with leases/`SKIP LOCKED`, create idempotent delivery records, retry safely, and retain dead-letter diagnostics. Document at-least-once delivery and stable event IDs so consumers can deduplicate.

#### F-07 — Scheduled publishing can duplicate work across replicas

- **Score:** Impact 5, Risk 5, Effort 4 → **20**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`; final exact-candidate scheduled-publication smoke remains unperformed.
- **Evidence:** Each hosted service selects the same approved rows and publishes them independently (`InProcessScheduledPublishingDispatcher.cs:29-44`); there is no claim, lease, row lock, or concurrency predicate before snapshots and webhooks are produced.
- **Required outcome:** Atomically claim due content with a lease or `FOR UPDATE SKIP LOCKED`, make publication transition/version creation idempotent, and test two dispatchers racing against the same database.

#### F-08 — Webhook SSRF protection is vulnerable to DNS rebinding

- **Score:** Impact 4, Risk 5, Effort 4 → **18**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`; the retained real-socket fallback caveat remains visible for final adjudication.
- **Evidence:** Cmsify resolves and validates DNS addresses (`WebhookDestinationValidator.cs:33`) and later asks `HttpClient` to resolve the hostname again (`WebhookDeliveryProcessor.cs:79`). Redirects are correctly disabled, but the validated address is not pinned to the connection.
- **Required outcome:** Pin outbound connections to a validated public address while preserving the original host for TLS/SNI, or route webhooks through an egress proxy that enforces public destinations. Revalidate on retries and test DNS rebinding, mixed public/private results, IPv4/IPv6 special ranges, redirects, and proxy behavior.

#### F-09 — Media database and blob state are not transactional or reconciled

- **Score:** Impact 4, Risk 4, Effort 3 → **24**
- **Status:** Remediated locally on `feature/readiness-audit`; final release-package publication/candidate certification remains approval-gated.
- **Evidence:** Upload now commits `PendingUpload` with the final deterministic key before storage and exposes only `Available` rows. Durable 30-day deletion intents use fenced, reclaimable leases and capped retry; stale uploads, missing/reappearing blobs, and old managed-prefix orphans are reconciled in bounded batches. Final local evidence: shared storage 91/91; Core 66/66; Infrastructure 292/292 with PostgreSQL/MinIO; API 69/69; Admin 29/29; .NET client 38/38; TypeScript generation/typecheck/40 tests/build; no EF/OpenAPI drift; independent review clean. Operator configuration, alerts, upgrade semantics, and pre-purge recovery are documented in `docs/operations.md`.
- **Required outcome:** Define retention semantics. Either delete blobs after a durable tombstone with retry, or explicitly retain them for a documented recovery window. Add orphan reconciliation, failed-upload cleanup, metrics, and local/S3 integration tests.

#### F-10 — API pagination and error contracts are inconsistent before freeze

- **Score:** Impact 4, Risk 4, Effort 2 → **32**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`; definitive OpenAPI and clean-consumer certification remains unperformed.
- **Evidence:** APIs mix `PagedResponse` (`page`, `pageSize`, `totalPages`) and `PagedResult` (`offset`, `limit`); templates accept page parameters but return the offset shape. Several authenticated-only branches return JSON strings from `BadRequest(...)` rather than the documented RFC 7807 body (`AuthController.cs:80,118`; `SettingsController.cs:33,46`).
- **Required outcome:** Choose one v1 pagination envelope and query convention, migrate all endpoints and SDKs before freeze, and require `application/problem+json` with stable error type, `traceId`, and correlation ID for every non-success response.

#### F-11 — Release builds are noisy and do not enforce first-party warning quality

- **Score:** Impact 3, Risk 3, Effort 2 → **24**
- **Status:** Remediated locally at committed implementation `bdaa0ff4a8f6d5e9b6692575f57a524e925a9ca4`; final release certification remains open.
- **Evidence:** Release policy now treats emitted first-party compiler/analyzer warnings as errors. The 45 Admin nullable warnings and 513 xUnit v3 cancellation diagnostics found during implementation were fixed without broad suppression. Cmsify-owned Sass uses modules, Bootstrap remains a dependency with dependency-only quieting, and policy tests reject first-party legacy/deprecated usage or global quieting. The forced non-incremental Release build passed with 0 warnings and 0 errors; the final semantic quality policy passed 17/17 and complete release contracts passed 232/232. The ordinary public restore was not used because the exact resilience package remains unpublished.
- **Required outcome:** Resolve first-party nullable warnings, migrate Sass to supported module APIs/tooling, isolate unavoidable third-party warnings, and fail CI on new first-party compiler/analyzer warnings.

#### F-12 — CI does not certify shipped artifacts

- **Score:** Impact 4, Risk 4, Effort 3 → **24**
- **Status:** Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`. Exact-candidate smoke, clean consumers, accessibility, upgrade/rollback, backup/restart, and evidence verification are implemented and mutation-tested; their definitive hosted executions remain false.
- **Evidence:** Accessibility runs only through `workflow_dispatch` (`admin-accessibility.yml:4`). Docker images are built and pushed but not started in a production-like smoke test. Packed NuGet/npm artifacts are not installed into clean consumer projects. No job checks package contents or the live OpenAPI snapshot.
- **Required outcome:** Pull or load the exact candidate images, start PostgreSQL/API/Admin, verify static assets, health, login, representative CRUD, media, and graceful restart. Install each packed SDK into clean .NET 10 and Node 20/22 consumers. Run accessibility on relevant Admin changes.

#### F-13 — .NET SDK resilience is incomplete and duplicated

- **Score:** Impact 4, Risk 3, Effort 3 → **21**
- **Status:** Remediated locally on `feature/readiness-audit`; default-branch package reconciliation/publication or a separately approved replacement remains a user-owned release gate.
- **Evidence:** Cmsify implementation range `29ba5a8^..b68172d` replaces the manual retry loop with one `HttpRequestResiliencePipeline` for direct, DI, and Admin paths, forwards safe timeout telemetry, and freezes replay enablement with the other construction-time settings. The sibling `feature/cmsify-resilience` range `5216a18..e5a7c57` adds the reusable request factory, executed keyed policies, configurable frozen classification, and bounded telemetry/ownership rules. Its final audit intentionally replaces Polly's circuit strategy and scaled clock adapter with a private thread-safe logical-request breaker driven directly by the caller's monotonic `TimeProvider`: caller cancellation and observer-failure tunnels contribute no throughput/state, half-open admits one probe, and extreme/zero-origin durations remain exact. Breaker completion is the atomic terminal linearization point: cancellation observed through its locked completion check, including during completion timestamp acquisition, wins with the exact token and zero breaker effect; cancellation first initiated after commit by the outside-lock circuit callback is post-terminal and cannot replace the committed outcome. A chunked hard deadline races both synchronous blocking prefixes and asynchronous work from non-cooperative factories, senders, and observers and records timeout/circuit failure once. Scheduled sender/observer delegates capture stable non-null request/response snapshots before ownership transfer; every late observer invocation is itself fully queued before user code can run, so terminal cancellation/timeout returns promptly while once-only observation, late-exception handling, and safe deferred disposal continue detached. The final correction adds a second explicit scheduling boundary after an incomplete late sender completes, ensuring that the sender-completion thread only queues observer work and never executes its synchronous prefix. The final local unsigned `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1` package is 44,277 bytes with provenance SHA-256 `17843D8C0A3422FCE37A3CEAC38029C638B099F01F044B09F30AD237D1786A1C` and NuGet content hash `/wzJoTLh3ebeAzOdaT0yUXXznF4C/26eWS6js5dDzzgDKsxNpeOL+s0ZJTwaxZYj6wG5cr9I4rUYOzpXOWoW+w==`; all five Cmsify asset graphs resolve that content hash as type `package` from the ignored feed with no sibling project path. Fresh validation passed 183 shared-package tests, including the 2/2 Fix 4 cases—the timeout case proves suspended-late-sender re-entry isolation and the cancellation case is an exact-token/ownership/zero-telemetry control—the unchanged 4/4 scheduling/ownership regressions, and the unchanged 9/9 atomicity regressions; a final exact-package clean consumer covering retry, custom classification, `NotReplayable`, timeout re-entry isolation, and exact-token cancellation; 67 .NET client tests; 31 Admin tests; and the single-MSBuild-node 527-test full solution. The `-m:1` switch limited project orchestration, not xUnit test-case scheduling. The unsuppressed XML-documentation pack emitted 80 warnings (`CS1591` ×75, `CS1573` ×5); a non-incremental Release build retained 45 existing Admin nullable warnings assigned to Task 11.
- **Required outcome:** Extend the shared resilience package with a reusable handler/pipeline suitable for typed and directly constructed clients, then preserve `CmsifyApiException`, ProblemDetails extensions, correlation IDs, ETags, streaming, timeout budgets, and idempotency rules while removing duplicate policy code.
- **Remaining release action:** Merge the sibling resilience work first; do not publish the current feature-branch-built package. Pack `0.2.0-cmsify.1` from the sibling default branch, record its source/raw SHA/content hash, and reconcile any changed evidence or lock identity into Cmsify through a reviewed post-merge PR before publication. Publish only those approved default-branch bytes through the trusted publishing workflow. A replacement is only an alternative after separate explicit approval and identity/pin review; its package identity, version, content hash, central pin, and lock entries must all be reviewed before use. The release operator then removes the local-feed dependency, runs a clean public locked restore, and reruns the focused/full gates. This task did not push, tag, publish, or release anything.

### Medium findings and enhancements

| ID | Finding | Required outcome |
| --- | --- | --- |
| F-14 | **Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`.** The compatibility/deprecation policy, additive-change rules, enum rules, deprecation window, `/api/v2` threshold, exact-PR-head comparison, scoped immutable diff tool, and protected breaking-change flow are implemented. | Execute the hosted contract and approval gates against the definitive release source. |
| F-15 | **Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`.** Third-party Actions and runtime images are immutable, exact candidate SBOM/provenance/signing/attestation/promotion contracts are enforced, attestation subjects must be the complete canonical set, and manifest identity remains distinct from Docker image identity. | Configure and verify hosted identities/permissions, then attest, sign, and promote the definitive tuple without rebuilding it. |
| F-16 | **Remediated locally at `bdaa0ff4a8f6d5e9b6692575f57a524e925a9ca4`.** SDK `10.0.400` is pinned with latest-patch roll-forward and prerelease disabled; all twelve solution projects have checked locks; all five tests use the exact xUnit v3 stack; workflows and Docker build stages use locked restore; weekly ecosystem-specific Dependabot updates group only minor/patch changes and cannot auto-merge. Final local evidence: 17/17 quality policy, 232/232 release contracts, all-twelve ignored-feed locked restore, and 0-warning/0-error forced Release build. Public/CI restore remains gated by the unpublished resilience package. | Retain lock review/regeneration and exact SDK enforcement; complete the user-owned public-package/public-restore gate before hosted certification. |
| F-17 | **Remediated locally through `bdaa0ff4a8f6d5e9b6692575f57a524e925a9ca4`.** Resolved selection, count, sort, and paging execute in PostgreSQL, with exactly two content commands and database-side `LIMIT`/`OFFSET`; the page projection joins template data. The checked dataset is 520 items/2,600 versions, webhook claims use 251 eligible rows for a batch of 100, media rejects max+1 without state, and guarded streams prove incremental transfer/ownership. Direct capacity filters passed API 10/10, Infrastructure 6/6, and client 4/4 at `ce9629417b794c9828a0e686dca6e1f846609877`. Coverage (`cmsify.coverage.v1`) and latency budgets (`cmsify.capacity.v1`) are trends only; behavioral/query/batch/streaming invariants block. PostgreSQL 17 EXPLAIN reported 33.405 ms for the representative page and supported the existing-index/no-new-index decision. | Keep deterministic invariants blocking, timing/coverage diagnostic, and collect production-like trend history before considering a latency gate. |
| F-18 | **Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`.** Production key validation, versioned encrypted webhook secrets, online rotation, migration, metrics, configuration, and operating guidance are implemented. | Preserve rotation/readability checks in final exact-candidate smoke and hosted monitoring. |
| F-19 | **Remediated locally at the source/policy level through `da3a428be6f12b9cdfbdde5a17daefab025615e0`.** `SECURITY.md`, `SUPPORT.md`, API compatibility policy, vulnerability reporting, release ownership policy, release runbook, and rollback runbook are present and mutation-tested. | Supply a repository-verified CODEOWNER and verify hosted environment protections/ownership before release. |

## Current SyntaxCircus package disposition

Package reuse should remove mechanical infrastructure without forcing Cmsify’s domain rules into generic packages. Sibling changes are acceptable when the resulting abstraction is independently useful and released before Cmsify consumes it.

This disposition was refreshed against the repository's central package pins and project references. Cmsify consumes `AspNetCore.Common` 0.1.9, `AspNetCore.Serilog` 0.1.3, `AspNetCore.Authentication` 0.1.4, `Blazor.Auth` 0.1.6, `Blazor.Components` 0.1.1, `DotEnv` 0.1.2, `EntityFrameworkCore.Postgres` 0.1.3, `Storage` 0.2.0, and the exact ignored-feed `Http.Resilience` 0.2.0-cmsify.1. The resilience package is integrated and validated locally, but its exact public publication/restore remains the user-owned release prerequisite. No sibling source is copied into Cmsify.

| Package | Decision | Current disposition |
| --- | --- | --- |
| `SyntaxCircus.AspNetCore.Common` | **Keep** | Retained for correlation IDs, ProblemDetails, security headers, trusted proxies, rate limiting, and health mechanics; Cmsify-specific error and middleware behavior stays covered in Cmsify. |
| `SyntaxCircus.AspNetCore.Serilog` | **Keep** | Retained for standard host logging; Cmsify owns its redaction and release-telemetry policy. |
| `SyntaxCircus.DotEnv` | **Keep** | Retained for development-only dotenv loading with Cmsify precedence rules. |
| `SyntaxCircus.EntityFrameworkCore.Postgres` | **Keep** | Retained for naming and advisory-lock migration mechanics; domain mappings and migration compatibility remain in Cmsify. |
| `SyntaxCircus.Blazor.Components` | **Keep** | Retained for shared error/reconnect/not-found UI without moving Cmsify domain UI into the package. |
| `SyntaxCircus.AspNetCore.Authentication` | **Adopted** | Released 0.1.4 is consumed for shared JWT/scheme mechanics while Cmsify retains database API-client, local-session, OIDC, workspace, and role policy. |
| `SyntaxCircus.Blazor.Auth` | **Adopted** | Released 0.1.6 is consumed for Admin OIDC token forwarding, refresh, session-expiry state, and optional distributed caching; local Cmsify sessions remain supported. |
| `SyntaxCircus.Http.Resilience` | **Adopted locally; public gate pending** | Exact 0.2.0-cmsify.1 is integrated across the .NET SDK and Admin with one request pipeline and validated package identity. Publication of those exact bytes and the isolated public restore are unperformed. |
| `SyntaxCircus.Storage` | **Adopted** | Released 0.2.0 is pinned and consumed for local and S3-compatible storage mechanics; Cmsify retains key generation, media authorization, and reconciliation policy. |
| `SyntaxCircus.Common` | **Selective, deferred** | No additional adoption is required for v1; Cmsify retains its actor/workspace authorization, pagination, and time/test seams. |
| `SyntaxCircus.AspNetCore.Common.MassTransit` | **Not applicable** | Cmsify has no MassTransit boundary. A durable PostgreSQL outbox does not justify adding a message bus for v1. |
| `SyntaxCircus.Credentials` | **Not applicable** | It owns desktop/local credential storage, not server secret management. |
| `SyntaxCircus.Email` | **Not applicable** | Cmsify currently sends no transactional email. |
| `SyntaxCircus.AI.Providers` | **Not applicable** | Cmsify has no AI provider integration. |
| `SyntaxCircus.Blazor.Seo`, `.Tracking`, `FancyBlazor` | **Not applicable** | The Admin is an authenticated operational application, not a public SEO/analytics/decorative site. |
| `SyntaxCircus.Maui.TokenStorage` | **Not applicable** | Cmsify ships no MAUI client. |
| `SyntaxCircus.RevenueCat`, `.RevenueCat.Maui` | **Not applicable** | Cmsify has no purchase or entitlement boundary. |

## Completed repository remediation and current release remainder

### Repository phases 0–2 — completed locally

Tasks 1–12 implemented the public contract and SDK corrections; OIDC, outbox, scheduled publication, webhook egress, media reconciliation, and upgrade/rollback guarantees; shared authentication, Admin auth, storage, and resilience adoption; warning-free/locked quality policy and capacity evidence; exact candidate, clean-consumer, accessibility, smoke, compatibility, governance, supply-chain, attestation, signing, and promotion contracts; and the reviewed offline OCI loader. The explicit F-01 through F-19 dispositions above are the current source/policy ledger.

This completion is local and non-certifying. It does not assert that public package availability, a definitive candidate run, hosted protections, trusted publishing, signatures, attestations, promotion, soak, a stable tag, or a final release exists.

### Release certification remainder — all unperformed

1. Merge the sibling resilience work, pack and approve `0.2.0-cmsify.1` from its default branch, and reconcile any changed package identity/locks into Cmsify through a post-merge PR. Publish only the approved default-branch bytes through the trusted workflow; never publish the current branch-built local artifact. The release operator then runs the isolated public-package gate and preserves its five-asset identity evidence. A replacement is only an alternative after separate explicit approval and identity/pin review, including its package identity, version, content hash, central pin, and lock entries.
2. After the tag, changelog, source SHA, public restore, and hosted prerequisites are verified and explicit authorization is given, an authorized maintainer pushes the already-created reviewed `vX.Y.Z` or `vX.Y.Z-prerelease` tag. The tag-push-only workflow—not a manual dispatch—builds the definitive same-source NuGet, npm, API OCI, and Admin OCI tuple. The exact recorded push command in the evidence ledger remains unexecuted.
3. The definitive workflow must pass clean .NET/Node consumers, candidate accessibility, exact-image upgrade/rollback, production-like CRUD/media/OIDC/webhook/schedule/restart/backup smoke, live OpenAPI compatibility, and complete artifact verification without rebuilding candidates.
4. Repository administrators and approvers verify CODEOWNERS/hosted protection, trusted-publishing and registry identities, required protected approvals, complete artifact attestations, OCI signatures, and immutable digest promotion.
5. The release operator retains authenticated soak evidence, adjudicates the Task 9 rollback diagnostic omission and both historical media races, verifies the stable tag/source/release identity, and runs the final external release gate.

### Current v1 go/no-go gates

- [ ] Exact resilience bytes pass the isolated public-package restore and five-asset identity gate.
- [ ] One definitive API/Admin/NuGet/npm tuple is built from one reviewed tag and source SHA with exact versions, licenses, checksums, SBOMs, and manifest digests.
- [ ] Definitive clean consumers, accessibility, upgrade/rollback, production-like smoke, backup/restart, and live OpenAPI compatibility jobs pass with immutable evidence links.
- [ ] Hosted ownership/protection, trusted-publishing/registry identities, and protected approvals are verified.
- [ ] Every canonical artifact subject is attested, both OCI digests are signed, and promotion copies only certified immutable descriptors.
- [ ] The authenticated soak completes with no unresolved release blocker; retained caveats receive explicit final adjudication.
- [ ] A stable tag and GitHub Release resolve to the exact certified source and version, and the final external gate passes.

Passing local source tests cannot waive any unperformed public, definitive-candidate, hosted, approval, signing, promotion, soak, tag, or final-release gate.
