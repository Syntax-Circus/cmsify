# Task 2 Report: Standardize every test project on xUnit v3

## Implementation

- Replaced the v2 `xunit` central package entry with the existing exact `xunit.v3` `3.2.2` entry. The exact central versions are `Microsoft.NET.Test.Sdk` `18.9.0`, `xunit.runner.visualstudio` `3.1.5`, and `xunit.v3` `3.2.2`.
- Standardized Core, Infrastructure, API integration, Admin integration, and .NET client test hosts on `xunit.v3`, retained `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`, and set `OutputType` to `Exe` in all five projects. Existing runner metadata in the four projects that had it was retained unchanged.
- Converted the listed `IAsyncLifetime` implementations from `Task` to xUnit v3 `ValueTask` signatures. Container startup is wrapped with `new(container.StartAsync())`; no-op setup returns `ValueTask.CompletedTask`; disposal ordering and existing cancellation tokens are unchanged.
- Extended the release policy test so it rejects any mixed xUnit v2/v3 stack and asserts all five host project declarations plus the exact central package versions.
- Regenerated only the semantically affected test lock files. The client test already used `xunit.v3`, so its lock graph did not change when `OutputType` was added. The force-evaluate restore also touched unrelated lock-file metadata; those seven unrelated production locks were restored. The four changed test lock files were mechanically normalized to the repository LF convention.

## Files changed

- `Directory.Packages.props`
- `tests/release-contract/quality-policy.test.mjs`
- Five test project files, including `sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj`
- Five Infrastructure fixture files, five Admin fixture files, and nine API fixture files specified by the task brief
- `tests/Cmsify.Core.Tests/packages.lock.json`
- `tests/Cmsify.Infrastructure.Tests/packages.lock.json`
- `tests/Cmsify.Api.Integration.Tests/packages.lock.json`
- `tests/Cmsify.Admin.Integration.Tests/packages.lock.json`

## RED/GREEN policy evidence

### RED

Command:

```powershell
node --test tests/release-contract/quality-policy.test.mjs
```

Result: 3 passing, 1 failing. The new `standardizes every test project on the xUnit v3 host` assertion failed at the central `xunit` v2 entry (`true !== false`), before the migration was made.

### GREEN

Command:

```powershell
node --test tests/release-contract/quality-policy.test.mjs
```

Result: 4 passing, 0 failed, 0 skipped. The final fresh run completed in 105.9474 ms.

## Commands and results

```powershell
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --force-evaluate
```

Result: exit code 0; all twelve projects restored with the approved ignored local feed.

```powershell
dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --locked-mode
```

Result: exit code 0; all twelve lock files were accepted in locked mode. `SyntaxCircus.Http.Resilience` remains resolved at exactly `0.2.0-cmsify.1`.

```powershell
dotnet test tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj --configuration Release --no-restore
```

Result: exit code 0; 66 passed, 0 failed, 0 skipped.

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore
```

Result: the project compiled under xUnit v3 and tests ran; 229 passed and 63 failed because Docker's `npipe://./pipe/docker_engine` endpoint denied access. No tests were skipped.

```powershell
dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-restore
```

Result: the project compiled under xUnit v3 and tests ran; 10 passed and 61 failed because Docker's `npipe://./pipe/docker_engine` endpoint denied access. No tests were skipped. The known baseline `MediaApiTests.Delete_WithStaleEtag_ReturnsPreconditionFailedWithoutTombstone` 404-versus-412 failure was neither changed nor observable while Testcontainers could not start.

```powershell
dotnet test tests/Cmsify.Admin.Integration.Tests/Cmsify.Admin.Integration.Tests.csproj --configuration Release --no-restore
```

Result: 29 passed, 2 failed, 0 skipped. Both failures are `OidcDistributedTokenCacheTests` and are caused by unavailable Docker/Redis Testcontainers.

```powershell
dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj --configuration Release --no-restore
```

Result: exit code 0; 67 passed, 0 failed, 0 skipped.

```powershell
git diff --check
```

Result: no output after LF-normalizing the generated changed locks.

## Baseline comparison

- Core remained 66/66.
- .NET client remained 67/67.
- Infrastructure, API, and Admin all compiled and discovered/reran their complete test inventories, but Docker access prevented comparison with their stated 292/292, 70 pass plus the known API failure, and 31/31 baselines. The environment failure was not hidden or skipped.

## Self-review

- The policy test verifies every required host project, central versions, `OutputType=Exe`, runner/SDK references, and absence of v2 `xunit` references.
- A final lifecycle scan found no `IAsyncLifetime` implementation with `Task InitializeAsync` or `Task DisposeAsync` left in `tests` or the .NET SDK tests.
- The final lock-file diff contains only the four test projects whose dependency graph changed; unrelated force-evaluated production locks were restored.
- Locked restore passed and inspected entries retain `SyntaxCircus.Http.Resilience` at `0.2.0-cmsify.1`.
- Existing runner `PrivateAssets` and `IncludeAssets` metadata was preserved. No API behavior or existing test assertions were changed.

## Concerns

- Docker was unavailable to this environment (`npipe://./pipe/docker_engine` reports access denied), so the Docker-backed success baselines could not be re-established. The commands were run once and no suites were skipped.
- xUnit v3 enables existing `xUnit1051` analyzer warnings about cancellation-token use across the test projects. This task preserved every existing token as required and does not alter the test bodies; the warnings are non-fatal and should be addressed as a separate, scoped test-quality change if desired.

## Fix round 1: isolate the global stale-upload boundary test

### Finding and RED evidence

The covering test is `MediaReconciliationRepositoryTests.FailStaleUploads_UsesInclusiveBoundaryAndCreatesImmediateCleanup`. Under xUnit v3, the full Infrastructure suite reproducibly reported 291/292: that test expected `FailStaleUploadsAsync` to affect one row but observed two. The same test passed in isolation. Docker was verified available outside the sandbox; the failure was a shared-fixture database-state dependency, not a product behavior change.

RED command:

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore
```

Result before the fix: 291 passed, 1 failed (292 total). The named boundary test received `2` for its exact `count.ShouldBe(1)` assertion.

### Implementation

At the start of the covering test's setup, a set-based EF Core update changes only pre-existing rows matching the production operation's global predicate (`PendingUpload` with `BlobStateChangedAt <= cutoff`) to `Available`. The test then seeds exactly its boundary candidate and its immediately newer control row.

This leaves the production repository method and its exact `count.ShouldBe(1)` assertion unchanged. It makes the candidate set explicit and independent of test execution order: rows left by earlier collection tests cannot satisfy this test's global operation, while this test's stale-at-cutoff row remains the sole eligible candidate.

### GREEN verification

Focused command:

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~MediaReconciliationRepositoryTests.FailStaleUploads_UsesInclusiveBoundaryAndCreatesImmediateCleanup'
```

Result: 1 passed, 0 failed, 0 skipped; duration 2 s.

Full-suite command (outside sandbox with Docker access):

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --verbosity minimal
```

Result: 292 passed, 0 failed, 0 skipped; duration 1 m 11 s.

### Fix-round self-review

- The cleanup uses the exact global predicate under test and does not suppress xUnit analyzers.
- It does not delete historical rows or alter production code; it only prevents shared fixture data from joining this test's candidate set.
- The full Docker-backed suite establishes that the isolation works across the actual collection execution order.
