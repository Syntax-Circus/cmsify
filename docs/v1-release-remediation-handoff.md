# V1 release remediation handoff after Task 10

This is a point-in-time resume document for moving the remediation work to another computer. Resume from the commit containing this file; do not redo Tasks 1–10.

## Resume point

- Branch: `feature/readiness-audit`
- Exact Task 10 Cmsify implementation source tested: `b68172d6f7d8bd0a1ab53c4b3ef083f5c83e7c42` (`Snapshot Cmsify retry configuration`); full implementation range `29ba5a8^..b68172d`.
- Exact sibling source tested: `af04bef1566b175a3a859d399b68f5bd6c840a1f` (`Harden request resilience guarantees`) on `feature/cmsify-resilience`; full change range `5216a18..af04bef`.
- State: Task 10/F-13 is implemented and validated locally. Release consumption is still blocked on the user-owned stable publication/replacement gate described below. Tasks 11–12 remain untouched.
- The final Task 10 documentation commit follows the tested implementation commit and changes no runtime, tests, generated output, or package bytes.
- No push, merge, tag, package publication, image publication, or release was performed in Task 10.

On the new computer, first make sure this branch and the commit containing this document have been transferred or pushed by the user. Then run:

```powershell
git switch feature/readiness-audit
git status --short
git log -5 --oneline
```

Expected: a clean worktree with the Task 10 documentation commit at `HEAD` and tested implementation source `b68172d6f7d8bd0a1ab53c4b3ef083f5c83e7c42` in its direct history. Read `AGENTS.md` before changing anything.

## Authoritative plans and audit

- Master remediation plan: [`docs/superpowers/plans/2026-08-24-v1-remediation.md`](superpowers/plans/2026-08-24-v1-remediation.md)
- Release-readiness audit: [`docs/v1-release-readiness.md`](v1-release-readiness.md)
- Task 10 design: [`docs/superpowers/specs/2026-08-27-http-resilience-consolidation-design.md`](superpowers/specs/2026-08-27-http-resilience-consolidation-design.md)
- Task 10 implementation plan: [`docs/superpowers/plans/2026-08-27-http-resilience-consolidation.md`](superpowers/plans/2026-08-27-http-resilience-consolidation.md)
- Task 9 design: [`docs/superpowers/specs/2026-08-27-moving-baseline-upgrade-rollback-design.md`](superpowers/specs/2026-08-27-moving-baseline-upgrade-rollback-design.md)
- Task 9 implementation plan: [`docs/superpowers/plans/2026-08-27-moving-baseline-upgrade-rollback.md`](superpowers/plans/2026-08-27-moving-baseline-upgrade-rollback.md)
- Task 7 design: [`docs/superpowers/specs/2026-08-26-webhook-egress-secret-rotation-design.md`](superpowers/specs/2026-08-26-webhook-egress-secret-rotation-design.md)
- Task 7 egress plan: [`docs/superpowers/plans/2026-08-26-webhook-egress-hardening.md`](superpowers/plans/2026-08-26-webhook-egress-hardening.md)
- Task 7 key-rotation plan: [`docs/superpowers/plans/2026-08-26-webhook-secret-key-rotation.md`](superpowers/plans/2026-08-26-webhook-secret-key-rotation.md)

The master plan checkboxes are not the execution ledger. Use this handoff, Git history, and the current source/tests as the completed-work record.

## Completed remediation

Tasks 1–10 are complete through local implementation and validation. Key Cmsify milestones are:

| Area | Commit | Result |
| --- | --- | --- |
| Task 1 | `9e02aa5` | Ownership/review fix completed. |
| Task 2 | `ff2acbf` | OpenAPI output authority/hard-link protection completed. |
| Task 3 | `0a8d45e` | TypeScript TDD evidence recovery completed. |
| Task 4 | `db42d68` | Release-integrity gaps closed. |
| Task 5 | `b3e2f15` | Cmsify OIDC host superadmin mapping completed. |
| Task 6 | `9423085` | Durable webhook outbox/delivery and scheduled publishing completed. |
| Task 7 design | `f33747b`, `7fd8343` | Approved egress/key-rotation design and plans. |
| Task 7 egress | through `4618a92` | F-08 remediation complete, including NAT64 correction. |
| Task 7 key rotation | through `643776c` | Versioned keys, online rotation, migration, metrics, config, and runbook complete. |
| Task 7 final | `64ee4fb` | Reader-first inventory, runtime decrypt telemetry, and actionable diagnostics; combined GO. |
| Task 8 | `415a6a7` | Durable media reconciliation implemented and independently approved. |
| Task 9 | through `26bd204` plus the evidence-only documentation commit | Moving-baseline fixture, final-review compatibility/safety fixes, exact-image upgrade/rollback, CI/release gates, runbook, and fresh validation evidence complete. |
| Task 10 | `29ba5a8^..b68172d` plus the documentation commit containing this handoff; sibling `5216a18..af04bef` | Shared request-factory resilience, direct/DI/Admin parity, construction-time configuration, private logical breaker, hard non-cooperative deadline, retry ownership, cancellation/observer exclusion, and final local package validation complete; stable publication/replacement remains gated. |

Task 5 also changed sibling packages outside this repository:

- Authentication: `6732d5a` (`v0.1.4`)
- Blazor.Auth: `982013d` (`v0.1.6`)

Public publication remains approval-gated and was not performed. In particular, do not treat the local `SyntaxCircus.Http.Resilience` prerelease as publicly consumable.

## Task 7 final evidence

- F-08: PASS.
- F-18: PASS.
- Combined Task 7 go/no-go: GO.
- API Release build: 0 warnings, 0 errors.
- Infrastructure: 257/257 at final head.
- API integration: 61/61 at final head.
- Earlier unchanged Task 7 gate: Core 52/52, Admin integration 29/29, .NET client 38/38, TypeScript 40/40 plus generation/typecheck/build.
- EF reports no pending model changes; the outbox and secret-ciphertext expansion migrations each appear once.
- No OpenAPI/generated drift; production Compose rendered successfully.

The final combined review found no remaining Task 7 blocker.

## Carry to Task 12 final review

These are explicitly non-blocking and must remain visible for final release adjudication:

- Wire-level original webhook `Host` assertion and explicit override-policy coverage.
- Real socket connector fallback and connect-phase cancellation/disposal coverage.
- Remaining-gauge test precision: numeric zeros across two refreshes and seeded snapshot retention after count failure.
- Optional exact 1,062-character ciphertext assertion in the migration-boundary test.
- Task 6's historical strict-TDD evidence exception.
- Task 2's external GitHub environment evidence.
- Task 4's Syft relationship-only `DESCRIBES` handling and smoke cleanup trap requirement.
- Task 5 package publication and all external release evidence remain explicit-approval gates.
- Task 10's `SyntaxCircus.Http.Resilience` stable publication/replacement and clean public restore remain explicit user-owned gates.
- The default multi-project full-test command historically exposed the existing Media API fixture/initial reconciliation race twice; the isolated failing test passed, and the final strict-serial `-m:1` full solution passed 527/527 after the final resilience regressions were added. Keep this visible for Task 12 rather than attributing it to Task 10.

## Historical Task 8 start point

The following section is retained as historical transfer context; Task 8 has since completed. Its required outcome was:

- Define durable blob retention/tombstone semantics.
- Clean up failed uploads and reconcile database/blob orphans.
- Retry failed deletes safely across restart and replicas.
- Cover local and S3/MinIO providers, authorization, metadata reads, and disposal.
- Add bounded metrics and operator documentation.
- Enhance/release `SyntaxCircus.Storage` where reusable mechanics belong, while Cmsify owns key naming, retention, and reconciliation policy.

Before implementation on the new computer:

1. Inspect `git status --short` and preserve all existing work.
2. Re-read Task 8, F-09, `docs/project plan/09_media_api.md`, current media controller/repository/storage code, migrations, and local/S3 tests.
3. Locate the `SyntaxCircus.Storage` sibling repository and confirm its branch/worktree state. Do not publish a package without explicit user approval.
4. Use the required brainstorming skill for the retention/reconciliation design, then write an implementation plan before editing.
5. Keep Testcontainers and other heavy checks serial; this computer experienced substantial lag from orphaned .NET build/test helpers.

## Environment and workflow notes

- `AGENTS.md` requires `rtk` command prefixes. On the old computer RTK failed with `Cannot determine home directory. Is $HOME set?`; native commands were used without repurposing `HOME`. Re-test RTK normally on the new computer rather than assuming it is broken there.
- The tag workflow remains: branch/main builds validate only; an explicit reviewed `vX.Y.Z` tag starts certification; the protected `release` approval promotes the exact artifacts. Post-PR builds do not auto-tag.
- Run one heavy process at a time. Before killing a suspected stale process, verify its command line and that its parent has exited; do not touch IDE, Docker, or unrelated session processes.
- Do not push, merge, tag, publish, or release without explicit user approval.

The historical resume instruction at this point was to continue with Task 8. The current resume point is the Task 10 stable-publication gate followed by Task 11, as recorded below.

## Task 8 continuation result

Task 8 / F-09 is implemented and independently approved in Cmsify commit `415a6a7` (`Complete durable media reconciliation`). The reusable shared-storage work is committed in the sibling `SyntaxCircus.Storage` repository through `4df2ac3` on `feature/media-reconciliation`.

Final local evidence at the Task 8 commit:

- `SyntaxCircus.Storage`: Release build 0 warnings/errors, 91/91 tests, pack succeeded.
- Cmsify: Core 66/66; Infrastructure 292/292 with PostgreSQL and MinIO; API integration 69/69; Admin integration 29/29; .NET client 38/38.
- TypeScript: OpenAPI generation check, typecheck, 40/40 tests, and build passed with no generated drift.
- EF reported no pending model changes; development and production Compose configuration rendered successfully.
- Full solution test passed. The clean Release build succeeded with 45 existing Admin nullability warnings assigned to Task 11.
- Independent Task 8 review finished with no Critical, Important, or Minor findings.

The Task 8 continuation originally paused on the local prerelease package. The user later confirmed that stable `SyntaxCircus.Storage` `0.2.0` had been published; Task 9.6 replaced the prerelease with that stable public-feed dependency and proved the production Dockerfile could restore it normally. This records the user-confirmed publication state; it does not claim this Cmsify task published the package. No push, merge, tag, publication, or release was performed by the Task 9 work.

This historical continuation next entered Task 9; Task 9 has now completed with the evidence below.

## Task 9 moving-baseline implementation

Task 9's upgrade/rollback contract is described by the [approved design](superpowers/specs/2026-08-27-moving-baseline-upgrade-rollback-design.md) and [implementation plan](superpowers/plans/2026-08-27-moving-baseline-upgrade-rollback.md). Its operator and evidence surfaces are:

- fixture provenance and exact baseline identities: [`tests/upgrade/fixtures/v0.1.3/manifest.json`](../tests/upgrade/fixtures/v0.1.3/manifest.json);
- canonical payload inventory: [`tests/upgrade/fixtures/v0.1.3/SHA256SUMS`](../tests/upgrade/fixtures/v0.1.3/SHA256SUMS);
- local fixture, rehearsal, diagnostics, cleanup, and refresh commands: [`tests/upgrade/README.md`](../tests/upgrade/README.md);
- production deployment/rollback sequence: [`docs/operations.md`](operations.md#v01x-to-v1-upgrade-and-rollback);
- dedicated branch/PR/manual rehearsal: [`.github/workflows/upgrade-rollback.yml`](../.github/workflows/upgrade-rollback.yml);
- tag certification and protected promotion dependency: [`.github/workflows/publish-cmsify.yml`](../.github/workflows/publish-cmsify.yml).

## Task 9 final validation and evidence

Task 9/F-04 passed the complete local validation from a clean committed source state after the consolidated six-finding final review fix. This proves only the exact local candidate below; the release workflow must repeat certification for the exact OCI artifact associated with any proposed tag before protected promotion.

### Exact candidate and baseline identities

- Tested source SHA: `26bd2047b906c9ef3c4b7776447a7a44f8ca4a7c`.
- Candidate reference: `syntaxcircus/cmsify-api:1.0.0-task9-final` (a mutable local alias used only to select the image once).
- Immutable local image ID: `sha256:5bf4175b8c81140ad57f441d7b3d27018479c831f7188b840893ee96fc8103a0`.
- Candidate platform: `linux/amd64`.
- OCI version/revision: `1.0.0-task9-final` / `26bd2047b906c9ef3c4b7776447a7a44f8ca4a7c`.
- Build informational version: `1.0.0-task9-final+26bd2047b906c9ef3c4b7776447a7a44f8ca4a7c`.
- The image was built without cache from [`src/Cmsify.Api/Dockerfile`](../src/Cmsify.Api/Dockerfile) with its normal public NuGet restore and stable `SyntaxCircus.Storage` `0.2.0`. It has no registry `RepoDigest` because Task 9 did not push or publish it.

The checked-in baseline remained:

| Artifact | Exact identity |
| --- | --- |
| API | `docker.io/syntaxcircus/cmsify-api:0.1.3@sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931`, linux/amd64, source `bc652aec1acad7ef440576b5019a0fe7c72004b3` |
| PostgreSQL | `docker.io/library/postgres:17-alpine@sha256:7456ef82e5f5bc43d997f4781bbd7c0d6389bff397564649a356e206ba473aee`, linux/amd64 |
| MinIO | `docker.io/minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:a1a8bd4ac40ad7881a245bab97323e18f971e4d4cba2c2007ec1bedd21cbaba2`, linux/amd64 |

`verify-fixture` passed. The strict generation contract records generator schema/version `1`/`1.0.0` and checked-in seed `tests/upgrade/seed/v0.1.3.sql` at SHA-256 `d4f985494d9da184ee5a3473b40b8fd1b366c28fb288b0331eb2aa1a1f0408de`; `expected.json` provenance is identical. Docker-backed deterministic regeneration was byte-identical with aggregate `81c9291f324691dce421292c14901c4811b82321295a645b2ed46f2a4dcfbcb6`. Both rehearsal reports bound the SHA-256 of the canonical verified `SHA256SUMS` inventory as fixture digest `ecdc40680f97a74535ab08a22532fa8d2656598f9606b8b0cb071f31377f725d`.

### Two clean rehearsal passes

The opt-in integration command passed 1/1 after running two complete rehearsals:

| Run | Result and assertion counts | Matched-backup manifest | Canary |
| --- | --- | --- | --- |
| `cmsify-upgrade-8e310004e422` | All eleven phases passed; baseline 27, candidate 30, rollback 28 | `240c0ae3cf8089081b4c173661cffbd942c1371ef8e1e076f62d7a7881513587`; 2 media objects | `01a045f6-6d90-73b9-9c0b-c6e592025527`, absent after rollback |
| `cmsify-upgrade-6c97c20e0382` | All eleven phases passed; baseline 27, candidate 30, rollback 28 | `32fb0ae056fe2570d6385c44ab7e85f70c43f5fb2fe6c91a440c0c705beb5099`; 2 media objects | `01a045f8-71b0-74cf-ac67-c84aa12f260b`, absent after rollback |

Each run re-verified its original backup, re-discovered and re-inspected exact data container/volume identity and both ownership labels after the final fence before any removal, restored database and media into fresh storage, and started exact `0.1.3` only after restore. Both reports include the shared `global-admin-restricted-read` assertion in baseline/candidate/rollback alongside the unchanged reader 404 invariant. The retained diagnostics for both runs contained only allow-listed PostgreSQL/MinIO markers and the exact 11 safe rollback migration IDs. The immediate post-run and final ownership-label audits each found zero containers, zero volumes, and zero networks.

### Commands and pass counts

- Complete upgrade/release-contract Node set: 352/352 passed, 0 failed/skipped.
- `verify-fixture`: passed for `0.1.3`.
- `generate-fixture --check`: passed byte-identically.
- `verify-release-contract.mjs`: passed.
- Fresh production Dockerfile candidate build/inspect: passed with the exact identity above.
- Opt-in `tests/upgrade/integration/rehearsal.test.mjs`: 1/1 passed, containing the two runs above.
- Focused Infrastructure PostgreSQL/MinIO Testcontainers: 292/292 passed.
- Focused API PostgreSQL Testcontainers: 71/71 passed, including matching legacy full-tick ETag acceptance, normalized emission, and stale-validator rejection.
- TypeScript: `npm ci`, live OpenAPI/generation check, typecheck, 40/40 tests, and ESM/CommonJS/declaration build passed.
- Release solution build: passed with 0 errors and 45 existing Admin nullable warnings assigned to Task 11.
- Release solution test: 496/496 passed: Core 66, Infrastructure 292, API 71, Admin 29, and .NET client 38.
- Final fixture diff, `git diff --check`, and the requirement-by-requirement design audit passed; final clean-worktree evidence is recorded in the Task 9.8 execution report after the documentation-only commit.

The exact command forms are in the [harness runbook](../tests/upgrade/README.md). The dedicated [upgrade workflow](../.github/workflows/upgrade-rollback.yml) and [release workflow](../.github/workflows/publish-cmsify.yml) run the same public CLI contract; the structural release-contract tests passed in this run.

The final run used these command contracts. On local PowerShell, the two Node globs were expanded with the following exact passing `Get-ChildItem`/file-array invocation; Node commands used the Node 24.19.0 executable described below:

```powershell
$nodeTests = @(
  (Get-ChildItem 'tests/upgrade/unit/*.test.mjs'),
  (Get-ChildItem 'tests/release-contract/*.test.mjs')
) | ForEach-Object { $_.FullName }
node --test $nodeTests
node eng/upgrade-tests/cli.mjs verify-fixture --fixture tests/upgrade/fixtures/v0.1.3
node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3 --check
node scripts/release/verify-release-contract.mjs

$sourceSha = '26bd2047b906c9ef3c4b7776447a7a44f8ca4a7c'
docker build --platform linux/amd64 --pull --no-cache --provenance=false --build-arg BUILD_VERSION=1.0.0-task9-final --build-arg "BUILD_INFORMATIONAL_VERSION=1.0.0-task9-final+$sourceSha" --build-arg "BUILD_SOURCE_REVISION=$sourceSha" --tag syntaxcircus/cmsify-api:1.0.0-task9-final --file src/Cmsify.Api/Dockerfile .
$env:CMSIFY_UPGRADE_TEST = '1'
$env:CMSIFY_UPGRADE_CANDIDATE_IMAGE = 'syntaxcircus/cmsify-api:1.0.0-task9-final'
$env:CMSIFY_UPGRADE_CANDIDATE_VERSION = '1.0.0-task9-final'
$env:CMSIFY_UPGRADE_CANDIDATE_SOURCE_SHA = $sourceSha
node --test tests/upgrade/integration/rehearsal.test.mjs

dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --verbosity minimal
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore --verbosity minimal
Set-Location sdk/typescript
npm ci
npm run generate:check
npm run typecheck
npm test
npm run build
Set-Location ../..
dotnet build Cmsify.slnx --configuration Release --no-restore
dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal
```

## Task 10 final package, validation, and publication gate

### Exact source and package identity

- Sibling repository: `feature/cmsify-resilience`, baseline `5216a18` (`v0.1.6`), Task 10 range `5216a18..af04bef`, final HEAD `af04bef1566b175a3a859d399b68f5bd6c840a1f`.
- Cmsify: `feature/readiness-audit`, implementation range `29ba5a8^..b68172d`, tested implementation HEAD `b68172d6f7d8bd0a1ab53c4b3ef083f5c83e7c42`.
- Package: `SyntaxCircus.Http.Resilience` `0.2.0-cmsify.1`.
- Final authoritative `.nupkg` SHA-256: `2E3CC9F084E26FC6EDE44B422C9852D326711F0EBC532A6CEF4E25E8FACBDF74` (42,300 bytes).
- NuGet content hash: `+h1IcaouivPC3qfKww+qikAdVnaK0YV0tuHjHk5izMOtyZaF2Num+e1wbEJX3Ylbe38qHg8UCMdl2Ofzoya3GA==`.
- The nuspec records MIT, `README.md`, repository branch/commit, and net10.0 dependencies on `Microsoft.Extensions.Http.Resilience` 9.9.0, `Microsoft.Extensions.DependencyInjection` 10.0.0, and `Microsoft.Extensions.Http` 10.0.0. The main package contains only NuGet metadata, `README.md`, `lib/net10.0/SyntaxCircus.Http.Resilience.dll`, and its XML documentation; the audit found no source, project, configuration, PDB, solution, credential, secret, token, `bin`, or `obj` asset.

The final audit intentionally follows the binding design/ledger over the earlier plan's Polly-circuit detail. Polly retry primitives remain, but the Polly circuit strategy and `CircuitTimeProvider` were removed. A private locked logical-request breaker now uses the caller's monotonic `TimeProvider` directly, preserves exact rolling-window/break durations (including timestamp zero and extreme accepted values), admits one half-open probe, and excludes caller cancellation and observer failures entirely. A monotonic deadline with chunked timers supports every positive finite timeout plus infinity, races non-cooperative factories/senders/observers, and safely observes/disposes late work without duplicate telemetry or circuit mutation. This is an intentional architecture correction, not a public API/default change; the legacy helper and its AI-mode `429` exclusion remain source-compatible.

The final sibling commands were:

```powershell
dotnet build SyntaxCircus.Http.Resilience.slnx --configuration Release -p:DisableGitVersionTask=true --verbosity minimal
dotnet test tests/SyntaxCircus.Http.Resilience.Tests/SyntaxCircus.Http.Resilience.Tests.csproj --configuration Release --no-restore --verbosity minimal -p:DisableGitVersionTask=true
dotnet pack src/SyntaxCircus.Http.Resilience/SyntaxCircus.Http.Resilience.csproj --configuration Release --no-restore -p:DisableGitVersionTask=true -p:Version=0.2.0-cmsify.1 -p:GenerateDocumentationFile=true -p:TreatWarningsAsErrors=false -p:WarningsAsErrors= --output artifacts/local-nuget/http-resilience --verbosity minimal
Get-FileHash artifacts/local-nuget/http-resilience/SyntaxCircus.Http.Resilience.0.2.0-cmsify.1.nupkg -Algorithm SHA256
```

`DisableGitVersionTask=true` is required in this linked Windows worktree because LibGit2Sharp rejects its ownership mapping before compilation. XML documentation remained pack-only, but no warning was suppressed: the final clean pack succeeded with 80 documentation diagnostics (`CS1591` ×75 and `CS1573` ×5), retained as warnings by `TreatWarningsAsErrors=false` and an empty `WarningsAsErrors`.

### Clean consumer and local restore

A unique .NET 10 console consumer was created at `C:\Users\jon\AppData\Local\Temp\cmsify-http-resilience-consumer-d7880a7314be45ef802e8da4934a25a9`. Its only direct package reference was exact `SyntaxCircus.Http.Resilience` `[0.2.0-cmsify.1]`; its private feed contained only the final nupkg and NuGet.org supplied public transitive dependencies. The executed commands were:

```powershell
dotnet new console --framework net10.0 --no-restore --output $tempConsumer
dotnet restore --configfile NuGet.Config --packages packages --force --no-cache --verbosity minimal
dotnet build --configuration Release --no-restore --verbosity minimal
dotnet run --configuration Release --no-build --no-restore
```

Result: `CLEAN_CONSUMER_PASS direct keyed custom-classification cancellation-circuit hard-late-timeout timeout-telemetry not-replayable`. This proves direct `503 -> 200`, two distinct executed keyed configurations (two and three attempts), custom-included `501 -> 200`, caller-token identity and cancellation exclusion from circuit throughput, a prompt hard timeout against a non-cooperative sender, exactly one timeout event, late once-only response observation with safe deferred ownership cleanup, and the `NotReplayable` fence. The consumer's restored `.nupkg` SHA-256 matched the final hash, its target/library asset types were `package`, and no sibling project path appeared. After verification, only that resolved exact OS-temp directory was recursively removed and its absence was confirmed.

The final package was copied into Cmsify's ignored `artifacts/local-nuget/http-resilience` feed. Only `artifacts/local-nuget/packages/syntaxcircus.http.resilience/0.2.0-cmsify.1` was cleared before this forced restore:

```powershell
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --force --no-cache --verbosity minimal
```

Five `project.assets.json` graphs—client, distributed-cache add-on, client tests, Admin, and Admin tests—resolve `SyntaxCircus.Http.Resilience/0.2.0-cmsify.1` as type `package`, with the one content hash above and no sibling source path. Cache metadata names the ignored local feed, the cached `.nupkg` SHA-256 matches, `git ls-files -- artifacts/local-nuget` returns nothing, and `.gitignore` excludes the config, feed bytes, and package cache.

### Fresh validation

- Sibling Release build: passed, 0 warnings/errors.
- Sibling tests: 168/168 passed, 0 failed/skipped.
- Clean consumer: all required behaviors passed (direct, both keyed configurations, custom classifier, cancellation/circuit accounting, hard late timeout, exact timeout telemetry, deferred observer/ownership cleanup, and `NotReplayable`).
- Focused .NET client: 67/67 passed, 0 failed/skipped.
- Focused Admin: 31/31 passed with Docker-backed Redis coverage, 0 failed/skipped.
- Full Release build: the forced non-incremental command passed with 45 existing Task 11 Admin nullable warnings and 0 errors: 40 `CS8602`, one `CS8601`, one `CS8603`, and three `CS8604`. Nothing was suppressed.
- Strict-serial full solution (`dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -m:1`): 527/527 passed—client 67, Admin 31, API 71, Core 66, Infrastructure 292—with PostgreSQL, MinIO, and Redis Testcontainers.

Two preceding full-solution attempts without `-m:1` each produced one Media API failure while every other test passed. The failing test changed between runs, and the originally failing test passed 1/1 in isolation. Investigation found the default `MediaApiTests` factory leaves `MediaReconciliationService` running; its initial cycle can mark a deliberately missing or newly seeded blob `Missing` before the endpoint assertion. Task 10 did not change this source/test path, and the required serial run passed. Preserve this concern for final adjudication rather than hiding it.

### User-owned stable-publication gate

Task 10 is complete locally, but the release dependency is not. `0.2.0-cmsify.1` has not been published and exists only in ignored local artifacts. Public CI restore therefore remains blocked. The user must publish or otherwise provide the approved exact stable `SyntaxCircus.Http.Resilience` package; then Cmsify must replace the prerelease pin with that exact stable version, remove reliance on the local feed, force a clean public restore, verify every asset graph and package hash, and rerun the sibling/consumer/client/Admin/full Release gates. Do not claim this gate is complete until that evidence exists. No command in Task 10 pushed, merged, tagged, published, or released anything.

### Environment facts and limitations

- Windows `10.0.26200` x64, PowerShell `7.6.5`, .NET SDK `10.0.400`.
- Docker client/server `29.7.2`; Docker Compose `v5.4.0` through the required `docker compose` interface.
- The documented and CI prerequisite remains Node 22. The local nvm installation exposed only Node 18.16.0, so the complete Node and TypeScript checks used the available newer Codex runtime Node 24.19.0 with npm 11.11.0. No Node check was skipped.
- `npm ci` reported 8 dependency advisories (1 low, 1 moderate, 5 high, 1 critical); dependency remediation is outside Task 9.8 and remains visible for final release adjudication.
- Stable `SyntaxCircus.Storage` `0.2.0` is pinned and resolved in Infrastructure/API assets. Its publication was user-confirmed; Task 9 did not publish it.
- `SyntaxCircus.Http.Resilience` remains pinned to local-only `0.2.0-cmsify.1`; unlike the storage package, no stable publication has been confirmed. Public/CI restore is blocked until the user completes the replacement gate above.
- The candidate is a local content-addressed image, not a published release artifact. No external GitHub Actions run ID exists for this local evidence, and no release is certified solely by this handoff.

## Next task: user publication gate, then Task 11

Task 10 implementation is complete locally. First complete the explicit user-owned stable `SyntaxCircus.Http.Resilience` publication/replacement and public-restore validation above; publication itself is not authorized by this handoff. Then resume Task 11 from [`docs/superpowers/plans/2026-08-24-v1-remediation.md`](superpowers/plans/2026-08-24-v1-remediation.md), preserving tested Cmsify source `b68172d6f7d8bd0a1ab53c4b3ef083f5c83e7c42`, sibling source `af04bef1566b175a3a859d399b68f5bd6c840a1f`, and all completed Tasks 1–10. Tasks 11–12 have not begun. Do not push, merge, tag, publish, or release without explicit approval.
