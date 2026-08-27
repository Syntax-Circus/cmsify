# V1 release remediation handoff after Task 7

This is a point-in-time resume document for moving the remediation work to another computer. Resume from the commit containing this file; do not redo Tasks 1–7.

## Resume point

- Branch: `feature/readiness-audit`
- Task 7 source head: `64ee4fb Complete webhook secret rotation preflight`
- State: intentionally paused after Task 7; Task 8 has not started.
- Worktree was clean before this handoff was added.
- No push, merge, tag, package publication, or release was performed in this session.

On the new computer, first make sure this branch and the commit containing this document have been transferred or pushed by the user. Then run:

```powershell
git switch feature/readiness-audit
git status --short
git log -5 --oneline
```

Expected: a clean worktree, this handoff commit at `HEAD`, and `64ee4fb` immediately before the handoff commit. Read `AGENTS.md` before changing anything.

## Authoritative plans and audit

- Master remediation plan: [`docs/superpowers/plans/2026-08-24-v1-remediation.md`](superpowers/plans/2026-08-24-v1-remediation.md)
- Release-readiness audit: [`docs/v1-release-readiness.md`](v1-release-readiness.md)
- Task 7 design: [`docs/superpowers/specs/2026-08-26-webhook-egress-secret-rotation-design.md`](superpowers/specs/2026-08-26-webhook-egress-secret-rotation-design.md)
- Task 7 egress plan: [`docs/superpowers/plans/2026-08-26-webhook-egress-hardening.md`](superpowers/plans/2026-08-26-webhook-egress-hardening.md)
- Task 7 key-rotation plan: [`docs/superpowers/plans/2026-08-26-webhook-secret-key-rotation.md`](superpowers/plans/2026-08-26-webhook-secret-key-rotation.md)

The master plan checkboxes are not the execution ledger. Use this handoff, Git history, and the current source/tests as the completed-work record.

## Completed remediation

Tasks 1–7 are complete and reviewed. Key Cmsify milestones are:

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

Task 5 also changed sibling packages outside this repository:

- Authentication: `6732d5a` (`v0.1.4`)
- Blazor.Auth: `982013d` (`v0.1.6`)

Public publication remains approval-gated and was not performed.

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

## Next task: Task 8 / F-09

Start with Task 8, “Reconcile media database and blob state,” in the master remediation plan and F-09 in the audit. Required outcome:

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

When resuming, tell Codex: “Read `AGENTS.md` and `docs/v1-release-remediation-handoff.md`, then continue with Task 8. Do not redo Tasks 1–7.”

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

Resume with Task 9 in `docs/superpowers/plans/2026-08-24-v1-remediation.md`; do not redo Tasks 1–8.

## Task 9 moving-baseline implementation

Task 9's upgrade/rollback contract is described by the [approved design](superpowers/specs/2026-08-27-moving-baseline-upgrade-rollback-design.md) and [implementation plan](superpowers/plans/2026-08-27-moving-baseline-upgrade-rollback.md). Its operator and evidence surfaces are:

- fixture provenance and exact baseline identities: [`tests/upgrade/fixtures/v0.1.3/manifest.json`](../tests/upgrade/fixtures/v0.1.3/manifest.json);
- canonical payload inventory: [`tests/upgrade/fixtures/v0.1.3/SHA256SUMS`](../tests/upgrade/fixtures/v0.1.3/SHA256SUMS);
- local fixture, rehearsal, diagnostics, cleanup, and refresh commands: [`tests/upgrade/README.md`](../tests/upgrade/README.md);
- production deployment/rollback sequence: [`docs/operations.md`](operations.md#v01x-to-v1-upgrade-and-rollback);
- dedicated branch/PR/manual rehearsal: [`.github/workflows/upgrade-rollback.yml`](../.github/workflows/upgrade-rollback.yml);
- tag certification and protected promotion dependency: [`.github/workflows/publish-cmsify.yml`](../.github/workflows/publish-cmsify.yml).

The exact final candidate identity, two clean run IDs, pass counts, cleanup audit, and complete validation results are appended only after Task 9.8's fresh run passes. Until then, the implementation itself is not a certification claim.
