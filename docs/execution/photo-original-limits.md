# Configurable photo originals — local qualification

This change lets an embedding host explicitly accept photo originals above20 MiB,
up to64 MiB, through normal authorized upload handlers. Default consumers and the
existing Core constructor keep20 MiB. Generated variants,24 MP/8192 edge,
processing budgets and host decoder acceptance remain separate.

## Source and ownership

- Branch: `feature/configurable-photo-original-limits-0818`.
- Base: `814e183b7af1f1f39680bbc7e22c7cced5f9cb41`, the source of installed0.8.18.
- The initially inspected clean main wasc9d313. Its Common0.2 dependency conflicted
  with the paired host's pins. All edits were preserved on the compatible source
  base; this PR does not adopt unrelated dependency updates.
- Core owns the limits contract/handler validation. Infrastructure owns immutable
  registration, PostgreSQL checks, leases, storage acknowledgement and private
  readback. The embedding host owns transport and decoder policy.
- The migration has an immutable target model. Its Down refuses larger retained
  originals/intents; no truncation, deletion or weakened retention protection.
- Stored lengths bound pending writes/readback after an intake reduction. Resume
  through existing intent/outcome handlers. New allocations obey the new limit.

## Measured validation

Validation used SDK10.0.400 from this repository root and real local PostgreSQL/
MinIO Testcontainers. The exact focused commands are:

```powershell
rtk proxy dotnet test --project tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj -c Release --no-restore --verbosity minimal
rtk proxy dotnet test --project tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj -c Release --no-restore --filter-class '*EmbeddedPhoto*' --verbosity minimal
```

Core274 and embedded-photo provider94 tests passed. Tests include default rejection,
explicit64 MiB/max+1, invalid/conflicting registration, authorization, declared
length/hash checks, restart, retained readback and pending writes after limit
reduction, plus a non-destructive PostgreSQL downgrade refusal.

The complete solution build passed with0 warnings/errors. Required SDK400 supplied
web-assets10.0.11, while the pre-existing Admin lock required10.0.12. An ignored
qualification targets file aligned only that SDK pack metadata to10.0.12; tracked
package pins and locks did not change. This workaround is an environment limitation,
not a claimed ordinary fresh locked-restore pass under SDK400.

The complete solution test run passed Core, Infrastructure, Components, Admin and
client suites. It reported2043 passed/0 assertion failures but did not complete:
the API process was stopped at the bounded20-minute window (20m31.663s measured),
producing one runner error/exit-1. **Full API qualification remains incomplete.**
The positional solution/-m invocation initially ran zero tests; the actual run
used `dotnet test --solution Cmsify.slnx -c Release --no-build`.

An independent review found that the local-source opt-in could use ordinary
bin/obj paths. Its regression failed before the fix; both repositories now
require explicit isolated absolute artifact outputs before restore/build.
Eight negative/positive MSBuild scenarios passed in the companion qualification
harness. No Important/Critical findings remained.

## Companion host evidence and limits

The paired local host uses explicit source references and separate qualification
locks/output paths. It retains released-package20 MiB behavior unless the local
source flavor is selected. No package is packed, released or adopted by this PR.

Disposable actual-source qualification retained three distinct originals
(36,256,437;36,256,437;21,576,032 bytes) with exact SHA256 readback, unchanged
reruns and process restart. Equal lengths did not mean equal hashes. All three
correctly reject derivatives under unchanged host policy: two28.1 MP8-bit PNGs
reject dimensions; a4.46 MP16-bit PNG rejects color. Windows supervised and
Linux512 MiB/128 PID/30s bounded decoding agree. No raw exports/media or personal
source records are included in this repository.

The companion host recorded762 default tests,9 Windows/9 Linux migration tests,
18 coupled media provider tests, one limits invariant and87 cross-engine original
upload/recovery checks. Those are host qualification results, not Cmsify package
consumer/release claims. The host migration changes are separate and uncommitted.

## Remaining prerequisites

Complete API-suite qualification and the normal reviewed release workflow before
publishing any package. Reconcile the feature with then-current upstream during
release preparation; no merge, release, deployment or host package adoption is
authorized by opening this PR.
