# Host workspace visibility candidate qualification

This increment starts from clean upstream `main`
`4ae553f7be6c42f34a2771add8a603dcde428727` on the normal branch
`feature/host-workspace-visibility`. It adds a workspace-only public visibility
scope, preserving standalone membership SQL, capability checks, public constructor
signatures, revisions, soft deletion and atomic audit/outbox persistence.

The selected method is one coding agent followed by fresh independent review of
the entire increment. Final platform/source/package identities and review findings
are recorded in the Puppies Plus execution handoff and candidate qualification
JSON. No merge, push, tag, publication or production adoption is authorized by
these candidate checks. `0.0.0-package-test` is the existing nonpublishing identifier.

## Measured source checks before packing

- Contract/registration RED: missing scope/kind/provider compile failures. Exact
  isolated Windows SDK 10.0.400 GREEN: Core 211/211 and registration 3/3, zero skips.
- PostgreSQL reads RED: host access, visible count, privileged restriction and
  provider failure tests fail against the original query path. GREEN: 9/9,
  24.898 seconds, zero failed/skipped; get uses one CMS query, list two queries and
  one resolution, with predicates before database paging.
- PostgreSQL mutations RED: 19 tests, seven failures through the legacy target
  filter. GREEN: focused workspace/authorization/workflow 35/35 in 50.595 seconds;
  full Infrastructure 656/656 in 196.764 seconds, zero failed/skipped. Host update,
  stale update/delete, concurrent writers, fresh actors, revocation, provider
  errors, save cancellation, in-flight ordering and stored audit/outbox checked.
- SQLite and default membership compatibility: 8/8 in 13.446 seconds, zero
  failed/skipped. Native get/list/legacy update/delete and sequential revision
  update/stale/delete/revocation/audit/outbox pass. CMS Reader/Editor/Admin access,
  membership `EXISTS`, API clients, SuperAdmin, anonymous and deleted rows are
  exercised on PostgreSQL and native SQLite. Concurrent SQLite writer parity is
  outside this increment's claim.
- Release solution build: zero warnings/errors, 7.33 seconds. All 17 projects
  report no vulnerable packages with public NuGet and transitive dependencies.
  The complete solution test gate and final candidate runs are recorded in the
  final cross-repository handoff; these bounded figures do not replace that gate.
- Packed negative: actual public 0.8.11 package restore succeeds, then the new
  human consumer fails compilation with two CS0246 errors solely for the absent
  visibility scope/provider. No database setup occurs.
- Node 22.23.3 package graph/ownership/output protection checks: 9/9, zero skips.
  Ownership guard RED then GREEN; protected output guard deliberately disabled
  yields two failures, restored guard passes on both successful/failing children.
- Independent review found external password-only output was not protected by
  the full connection-string guard. Synthetic scalar success/failure and
  missing-input regressions produce RED 9/16 then GREEN 16/16 on Node 22.23.3.
  External mode now requires a privately supplied nonempty plaintext scalar
  `CMSIFY_CONSUMER_POSTGRES_PROTECTED_PASSWORD`; both scalar and full connection
  are scanned before emission, and absent scalar input fails before child execution.
  Quoted Password and Pwd alias connections use the same explicit scalar input.

## Exact SDK and unchanged lock constraint

Windows originally has only SDK 10.0.401. The isolated SDK 10.0.400 archive from
Microsoft release metadata is SHA-512 verified and staged under ignored task
artifacts; no global SDK, PATH or global.json change is made. Process-local PATH
ensures child tools use the isolated executable. Official Node 22.23.3 Windows
archive SHA-256 is `2b0ff57b049cda1bbcea2240eec20467018713c1efe1f7360c2681859b90ed71`.

Unmodified exact SDK 10.0.400 locked restore encounters existing NU1004:
Admin's implicit `Microsoft.AspNetCore.App.Internal.Assets` request is 10.0.11,
while the unchanged lock requests 10.0.12. SDK bundled props lines 98-100 define
the implicit version. Qualification uses the SDK's custom import hook
`CustomAfterMicrosoftCommonTargets` (Microsoft.Common.CurrentVersion.targets:7105)
and this target, preserving compiled source/locks/package versions:

```xml
<Project>
  <Target Name="UseLockedAspNetCoreAssets" BeforeTargets="ProcessFrameworkReferences">
    <ItemGroup>
      <KnownAspNetCorePack Update="Microsoft.AspNetCore.App.Internal.Assets" AspNetCorePackVersion="10.0.12" />
    </ItemGroup>
  </Target>
</Project>
```

The initially ignored target is now tracked unchanged at
`scripts/qualification/locked-assets.targets` for release and ordinary .NET CI.
Both copies have SHA-256
`d8e96e62202728ae251a71bbf3e226f9474108f9f6666dbd6629342f5043ad4b`.
Pass `-p:CustomAfterMicrosoftCommonTargets=<absolute-target-path>` for
source locked restore/build/test on both platforms. Locked restore then passes;
this is an explicit qualification input, not a claim that default SDK 10.0.400
restore works unmodified. The vulnerability command uses `--no-restore` against
those validated assets because `dotnet list package` does not accept `-p`.
The release-readiness correction also uses the actually qualified MTP solution
command with sequential modules and minimum expected tests of one, replacing
legacy positional/VSTest CLI forms in release and ordinary source/coverage/capacity
steps. Registry/promotion/manual approval and package guards remain unchanged.

## Release handoff and limits

Proposed changelog entry: add a scoped host workspace visibility provider with
immutable restricted IDs, database filtering before workspace lookup/count/paging
and mutation lookup, preserved standalone defaults and existing constructors.
No release version is selected here.

The maintainer handoff must include final full source SHA, candidate archive
SHA-256 dictionaries and metadata, Windows/Linux exact-toolchain checks, byte
reconciliation, independent review rulings/fixes and additive API diff. Only
separate explicit release authorization permits the normal merge/tag/trusted
promotion runbook. After publication, verify immutable public package bytes and
repeat the clean public consumer gate before changing the Puppies Plus experiment
pins or resuming HTTP/Blazor/bootstrap qualification.

The new seam covers workspace repositories only. It does not extend host grants
to content/template/media/tag/webhook queries, create CMS credential/access rows,
grant write capabilities or change seeding/migrations. Existing operator/content
consumer setup stays separate from nonprivileged human scope qualification.
Visibility resolves for each operation; already-authorized writes may finish.
Hosts supply currently readable IDs. The existing list handler uses authenticated
Reader-or-higher access and reports per-result `CanWrite`, without a new per-row
`CanRead` gate; this preserves handler behavior and does not grant writes.
Count/page snapshots have no new transactional guarantee. Native SQLite checks
are compatibility evidence, not a production deployment/support claim. Raw logs,
SDK archives, feeds, caches and fixture secrets remain ignored; protected output
is scanned on success and failure before emission.
