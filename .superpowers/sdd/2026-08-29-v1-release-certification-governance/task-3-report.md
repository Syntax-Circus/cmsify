# Task 3: Rollback diagnostics through cleanup

## Status

Complete.

- Base: `6cb6a2be7749da04be5b42117642d88e20725392`
- Implementation commit: `a1aeb97e03b7c460a36987ebba1310dae12d9780` (`Retain rollback rehearsal diagnostics`)

## Files

- `eng/upgrade-tests/rehearsal.mjs`
- `eng/upgrade-tests/safe-files.mjs`
- `tests/upgrade/unit/rehearsal.test.mjs`
- `docs/operations.md`

## RED

Before production changes, `node --test tests/upgrade/unit/rehearsal.test.mjs` exited 1 with 32 passed and 1 failed. The new `final rollback diagnostics retain safe failure evidence after cleanup` test failed at the final report contract:

```
AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:
+ actual - expected

+ undefined
- 'cmsify.upgrade-diagnostics.v1'
```

The existing report retained per-phase state but did not expose a versioned, final first-failure/cleanup diagnostic contract.

## Implementation

- Added an atomic, safe-path JSON writer capped at 64 KiB.
- Added the `cmsify.upgrade-diagnostics.v1` report schema, source identity, first `failedStage`, allow-listed `failureEvidence`, and independent `cleanup` outcome.
- Preserved only existing sanitized readiness/assertion evidence; raw errors, headers, environment values, connection strings, and the seeded secret are excluded.
- Documented the operator-visible final report behavior in `docs/operations.md`.

## GREEN

- `node --test tests/upgrade/unit/rehearsal.test.mjs`: 33 passed, 0 failed.
- `node --test tests/upgrade/unit/*.test.mjs`: 170 passed, 0 failed, 0 skipped; exit 0.
- `git diff --check`: exit 0.

## Self-review and concerns

The regression injects a rollback assertion failure through the operation dependency, lets diagnostics capture and cleanup run, reads the run-owned final report, and proves the failure evidence remains while cleanup is separately marked passed. It also asserts that the secret, connection-string host, and request-header marker are absent. No concerns identified within this scoped change.

## Fix Round 1

### Status

Complete; implementation commit: `d8e66d0f0066032c4f958b72226832a1b2f599b8` (`Compact rollback diagnostics report`).

### RED

After correcting the fixture-only invariant text so the existing classifier recognized it, the deterministic cap regression ran:

```
node --test tests/upgrade/unit/rehearsal.test.mjs
tests 35
pass 34
fail 1
```

`compacts prior phase detail before finalizing oversized rollback diagnostics` failed with:

```
AssertionError [ERR_ASSERTION]:
null !== 'rollback'
```

Two completed phases each supplied the maximum allow-listed 16 readiness entries and 128 assertion entries. Adding the rollback failure evidence exceeded 64 KiB, the atomic write failed, and cleanup finalized the stale report without the required first failure.

### Implementation

- Before each report persistence, calculate serialized UTF-8 bytes and deterministically compact only successful-phase evidence when it exceeds 64 KiB.
- Summaries carry `truncated: true` and safe readiness/assertion counts; non-array safe evidence remains allow-listed.
- When root `failureEvidence` is retained, remove its duplicate failed-phase copy during compaction.
- Do not reattach that duplicate during terminalization of an already-failed phase.
- Leave the safe atomic writer fail-closed if the compacted report were ever still oversized; write failures are not swallowed.
- Updated `docs/operations.md` with the compaction behavior and retained required fields.

### GREEN

- `node --test tests/upgrade/unit/rehearsal.test.mjs`: 35 passed, 0 failed; exit 0.
- `node --test tests/upgrade/unit/*.test.mjs`: 172 passed, 0 failed, 0 skipped; exit 0.
- `git diff --check`: exit 0.

### Self-review and concerns

The at-cap regression retains all 16/128 allow-listed rollback entries and confirms the report is at most 64 KiB. The over-cap regression confirms the same rollback evidence, source/candidate contract, and cleanup status survive after deterministic prior-phase summaries, with no duplicate rollback phase evidence. No concerns identified within this scoped change.
