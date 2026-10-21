# Content save CI race — 2026-10-21

Reported job:
https://github.com/Syntax-Circus/cmsify/actions/runs/39681888056/job/119506165887

The push-triggered coverage run at15a3e3970602660da06186f9416d951b73a780d7
failed the SQLite Approved/expandChildren=true save parity case: expected200,
received409. This was separate from the tag-triggered0.8.20 certification/promotion
run39683139086, which succeeded on the same source and published the release.
The failure is a real test-isolation defect with a timing-dependent manifestation,
not evidence of a broken package publication or a random runner/download error.

## Root cause and scope

`ContentVersionSaveFactory` attempted to remove Cmsify hosted workers by their
`ImplementationType` namespace. Infrastructure registers those workers using
`ImplementationFactory`, so that property is null and all six workers survived.
Registering a separate provider configuration with Workers=None did not remove the
original API registrations.

The fixture seeds Approved content with publication/lease timestamps relative to
a fixed October5 clock. The surviving scheduled-publishing factory uses its real
system clock, making those dates due. A scheduler tick between reset and HTTP save
can publish the seed; the save correctly rejects Published content with409.
Coverage timing can expose the window. The successful non-coverage release run
does not establish that the fixture was isolated.

Only the parity test host now removes IHostedService registrations by service
type, matching other isolated API fixtures. Production worker registration,
scheduling, authorization and content handlers are unchanged. The released
v0.8.20 tag is not moved or reused.

## RED/GREEN validation

SDK10.0.400, Release, local Windows, real SQLite/PostgreSQL:

1. New provider theory failed2/2 before the fix, enumerating all six leaked
   workers, including ScheduledPublishingService.
2. A deterministic due-publication tick regression failed before the fix:
   the Approved seed became Published. This reproduces the causal state change
   without arbitrary sleeps or hoping coverage makes the original test fail.
3. After service-type removal, the entire affected save parity class passes
   **88/88,0 failures/0 skips**, including both regressions, under coverage.

```powershell
rtk proxy dotnet test --project tests/Cmsify.Api.Integration.Tests -c Release --no-restore --filter-class '*ContentVersionSaveSqliteApiTests' --coverage --coverage-output-format cobertura --results-directory artifacts/coverage-save-fixture -p:CustomAfterMicrosoftCommonTargets=scripts/qualification/locked-assets.targets
```

The passing run measured41.468s module/42.000s runner and produced Cobertura XML.
This focused run is not a claim of a new complete solution or Linux coverage pass;
the follow-up CI validates the Linux workflow. No production runtime changed and
no new release is required for this fixture-only fix.
