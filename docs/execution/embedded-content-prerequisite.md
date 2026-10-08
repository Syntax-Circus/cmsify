# Embedded content prerequisite qualification

Approved cross-repository design: [spec](../../../../puppies-plus/docs/superpowers/specs/2026-10-07-cmsify-breeder-content-prerequisite-design.md). Approved execution: [plan](../../../../puppies-plus/docs/superpowers/plans/2026-10-08-cmsify-embedded-content-prerequisite.md). Baseline `bbacd76fea5156e4c810eeb1d132123e491b7165`; branch `feature/embedded-content-prerequisite`, normal checkout. No public release/version selection or product adoption is claimed.

The increment exposes six Core request handlers, PostgreSQL transaction-owning Infrastructure implementations, default-deny embedded authorization and a compatible optional loaded update guard. The existing four-parameter update constructor and positional snapshot constructor remain available. HTTP controllers, shared wire contracts and SDK generated files are unchanged against baseline.

## Measured implementation checks

- SDK: 10.0.401, explicitly selected by this increment's plan; upstream `global.json` remains unchanged. Migrations generated/reconciled with isolated EF tool 10.0.11, matching EF runtime 10.0.11, for PostgreSQL and SQLite models. New embedded operations remain PostgreSQL-only.
- Core initial behavioral failures covered defaults, optional guard, canonical contract validation, exact setup authority, role/workspace/resource boundaries and anonymous reads/writes; latest focused Core run passed 233/233 with zero skips.
- Real PostgreSQL tests cover repeated/concurrent setup, schema drift/collision, exact selected reads, stale revisions, foreign scope, coherent concurrent edits, detached outputs, atomic writes, actor/field conflicts, concurrent same-key receipts and version allocation, rollback before commit, cancellation after commit, original response races, receipt revocation/nonmutation and host GUID audit.
- Additional measured RED→GREEN checks rejected overlong embedded text, undefined persisted content status, a drifted current registered schema pointer, and direct-port mutable input changes during authorization.
- First full Infrastructure run: 697/698, zero skipped; the sole failure was the existing native SQLite recovery assertion assuming one migration. Its expected history now derives from the actual migration assembly. The complete SQLite recovery class then passed 8/8. This interim run is not a final qualification pass.
- Existing package-graph guard suite: 16/16 passed, zero skipped. The PostgreSQL external fixture now exercises the new operations and restrictive host adapters; SQLite continues existing provider regressions. `--evidence <fresh-directory>` optionally retains locked graph, dependency archive/nuspec hashes and license metadata, vulnerability output and sanitized child logs; private fixture environment files/storage are not retained.

## Remaining acceptance gates

Final isolated locked Windows restore/build/full solution tests, frozen package provenance, Windows/Linux amd64 source and package-only consumer execution, dependency audit and one independent whole-increment review are recorded by the qualification orchestrator after this source checkpoint. Source/packed qualification cannot be inferred from compile-only validation. Public release, shared push/merge and Puppies Plus adoption remain separate explicit gates.
