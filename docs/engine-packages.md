# Engine NuGet packages and qualification

`SyntaxCircus.Cmsify.Core` and `SyntaxCircus.Cmsify.Infrastructure` package the existing .NET 10 engine assemblies. Assembly names and `Cmsify.Core.*` / `Cmsify.Infrastructure.*` namespaces are unchanged. Infrastructure depends on the matching Core package version; both follow the existing tag-derived release version and remain non-packable for ordinary local builds.

Both engine packages retain **AGPL-3.0-or-later** and include the repository's `LICENSE`, their README, source repository URL and release source commit. The separate SDK/contracts/components packages retain their existing MIT license. Creating these engine packages does not change the source license or establish that either new ID has been published.

## Host composition

The existing `AddCmsifyInfrastructure(configuration)` retains PostgreSQL and all Cmsify workers as defaults. It requires `ConnectionStrings:Cmsify`; storage and secret protection also require suitable host configuration. See [operations](operations.md) for operational settings and [provider portability](provider-portability.md) for existing registration options.

Hosts can register a scoped `ICurrentActor` and opt into its audit attribution with `UseHostCurrentActorForAudit = true`. Select workers explicitly with `CmsifyWorkers`; `None` disables all six. Audit identity grants no authorization. The existing workspace handlers continue to enforce actor capabilities and revision checks.

Resolving services does not migrate or seed a database. The host owns that lifecycle. `CmsifyDbContext.Database.MigrateAsync` applies the packaged PostgreSQL migrations; `ICmsifyDatabaseMigrator` also runs the existing seeder. The package qualification uses migrations directly to prove direct host workflows without creating Cmsify-local credentials.

### Host workspace visibility

The additive `Cmsify.Core.Workspaces.IWorkspaceVisibilityScopeProvider` supplies
workspace query visibility independently of `IWorkspaceAuthorizationService`.
Register the host's scoped provider before infrastructure composition:

```csharp
services.AddScoped<IWorkspaceVisibilityScopeProvider, HostWorkspaceVisibility>();
services.AddCmsifyInfrastructure(configuration,
    new() { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None });
// Alternatively, explicitly replace the default after composition:
services.Replace(ServiceDescriptor.Scoped<IWorkspaceVisibilityScopeProvider, HostWorkspaceVisibility>());
```

The host implementation resolves trusted current account/session/grants in
`ResolveAsync(CancellationToken cancellationToken = default)`. Return
`WorkspaceVisibilityScope.ForWorkspaces(allowedWorkspaceIds)` for initialized,
authenticated operations and `WorkspaceVisibilityScope.None` for denial or
missing initialization. IDs are copied, deduplicated and immutable; `Guid.Empty`
is rejected. A restricted decision remains restricted even for a SuperAdmin or
an actor with `WorkspaceId`. Anonymous actors cannot use restricted IDs. Null,
unknown, throwing and cancelled decisions fail without a wider-access fallback.

Each workspace get/list/legacy update/soft-delete/revision mutation resolves
visibility afresh. SQL applies the decision before lookup/count/paging; one list
uses a single decision for both queries. Handler authentication, role, capability
checks and creation rules still apply: visibility supplies no write permission.
An already-authorized in-flight write may finish; later operations resolve current
grants. No distributed transaction with a host grant store is promised.

Standalone registration uses a scoped, host-overridable `TryAdd` provider returning
`CmsManaged`, preserving the existing CMS membership subquery, API-client and
SuperAdmin behavior. Existing repository constructor signatures remain available
and use this default; DI selects their new provider-aware overloads.

This contract covers workspace repositories only. Content/template/media/tag/
webhook queries retain their existing boundaries. Native SQLite compatibility
covers database-side workspace queries, legacy mutations and sequential revision
update/stale/delete/audit/outbox paths; it does not establish concurrent SQLite
writer parity, complete embedding or production deployment support.

## Repeatable package gate

The release workflow packs eight NuGet packages, including Core, Infrastructure
and optional `SyntaxCircus.Cmsify.Infrastructure.Sqlite`. See the
[embedded SQLite guide](integrations/embedded-sqlite.md) for the separate SQLite
registration, schema-only migration and operational contract. Before the candidate
artifacts can reach the gated `promote` job, it runs:

```powershell
node scripts/release/verify-engine-packages.mjs --packages artifacts/nuget --version X.Y.Z --source-sha <40-character-source-sha>
```

Both consumers are also a pull-request/main CI gate, using local-only
`0.0.0-package-test`. The verifier copies the three candidate packages to a
temporary feed and stages `tests/package-consumer` and `tests/package-consumer-sqlite`
separately outside the checkout, each with empty package, HTTP and scratch caches.
All three engine IDs map exclusively to the candidate feed; there is no public
engine fallback. Public NuGet supplies other dependencies. Both graphs require
exact candidate package versions and reject source project references. PostgreSQL
additionally rejects EF SQLite, Microsoft.Data.Sqlite and SQLitePCLRaw dependencies.

The executable verifies the packed assembly files, repository/commit metadata, README metadata, AGPL license expression and included license bytes. It explicitly requires Infrastructure's Core dependency at the candidate version so the direct Core reference cannot hide a broken transitive dependency. It rejects a leaked EF Design dependency. Against its own disposable PostgreSQL 17 container, it verifies real DI registration, host audit mode, worker opt-out, all packaged migrations, workspace create/get/list/update/delete handlers, stale-revision rejection, persisted soft deletion, update outbox and two scoped host audit subjects without HTTP or Cmsify-local user/session/API-client records.

SQLite's separate executable validates all three packages and assembly versions,
real migrations with owned history/no seed, foreign keys and a second host's
idempotent migration/repository CRUD, audit and stale-write checks using native SQLite.
Both executables also resolve `Cmsify.Core.ContentQueries.IListContentRequestHandler`
and execute ordinary and resolved content lists with an explicit host actor and
fixed `TimeProvider`. The shared fixture asserts item versus serving snapshots,
exact all-tag membership, wildcard differences, candidate filtering before ranking,
Q after winner selection, paging/counts, AsOf and authorization denial. HTTP
diagnostics must record zero requests and the Cmsify API assembly must remain absent;
the host is never started. Database fixture setup uses the packaged context after
real migrations; each content workflow enters through the registered handler.
Docker and public NuGet access are required by the default gate. The runner removes
only its uniquely named container and own temporary directory. For isolated Linux
orchestration, `CMSIFY_CONSUMER_POSTGRES` may supply a caller-owned disposable test
connection; that path never creates/removes Docker resources. The caller must
verify the fixture endpoint and its
private ownership marker before setting `CMSIFY_CONSUMER_POSTGRES_DISPOSABLE=1`;
without that explicit disposable ownership declaration the runner stops before
restore or setup. The default runner generates a private environment file and
random fixture password, scans child output for protected inputs before emitting
success or failure logs, and cleans only its own resources. Both consumers also
build a separate human DI composition for workspace visibility. Humans keep a
stable host GUID, no API-client/actor workspace ID and no SuperAdmin privilege;
packed get/list/paging, independent write denial, revision/audit/outbox/stale/delete
and reused-context revocation paths execute through the actual packed handlers.
These sequential SQLite checks do not claim concurrent SQLite writer parity.
Package bytes, feeds and caches remain ignored. `--restore-only` restores and validates both package
graphs without builds, metadata execution or database operations; CI runs the full gate.

For a local candidate after locked restore, choose a fresh ignored output directory (incremental pack may retain an earlier package's metadata):

```powershell
dotnet build src/Cmsify.Infrastructure.Sqlite/Cmsify.Infrastructure.Sqlite.csproj --configuration Release --no-restore -p:CmsifyReleaseBuild=true -p:Version=0.0.0-package-test -p:InformationalVersion=0.0.0-package-test+<source-sha>
dotnet pack src/Cmsify.Core/Cmsify.Core.csproj --configuration Release --no-build --output artifacts/engine-candidate -p:CmsifyReleaseBuild=true -p:PackageVersion=0.0.0-package-test -p:RepositoryCommit=<source-sha> -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
dotnet pack src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj --configuration Release --no-build --output artifacts/engine-candidate -p:CmsifyReleaseBuild=true -p:PackageVersion=0.0.0-package-test -p:RepositoryCommit=<source-sha> -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
dotnet pack src/Cmsify.Infrastructure.Sqlite/Cmsify.Infrastructure.Sqlite.csproj --configuration Release --no-build --output artifacts/engine-candidate -p:CmsifyReleaseBuild=true -p:PackageVersion=0.0.0-package-test -p:RepositoryCommit=<source-sha> -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
node scripts/release/verify-engine-packages.mjs --packages artifacts/engine-candidate --version 0.0.0-package-test --source-sha <source-sha>
```

## Remaining qualification

This gate qualifies packaging, bounded workspace/host-audit paths and direct ordinary/resolved content-query behavior on each provider. It does not qualify complete application embedding, every content/template/media/authentication workflow, background-worker behavior in a consumer host, upgrade/rollback, general restart durability, contention or capacity. Existing source tests and experiments remain evidence for their own scope; they are not substitutes for package-consumer qualification.

Optional SQLite registration and real schema-only migrations now have a separate
candidate gate. Full deployment qualification remains open: generic JSON queries,
broader repository workflows, bulk concurrency, full workers/load and production
backup recovery are separate gates. No earlier supported SQLite release exists
for a supported-version upgrade; synthetic source fixtures cover their own scope.

See [measured qualification evidence](evidence/2026-10-02-engine-packages.md) for the
original PostgreSQL checkpoint. Before optional-package publication, a maintainer
must verify NuGet ownership/reservation and trusted-publisher scope permit the
SQLite ID. Local checks do not satisfy that registry prerequisite or change settings.

## Local content-query candidate

`0.8.9-query.1` is a local verification identifier, not a release decision or
publication claim. Build and pack it from a committed source snapshot using the
commands above with that version and the actual source SHA, then run the verifier
on Windows and pinned Linux SDK 10.0.400. Candidate feeds/caches/logs stay ignored.
Released `0.8.8` cannot supply the new `Cmsify.Core.ContentQueries` public contract;
building the same consumer against those package bytes is a required compile
negative, distinct from missing/wrong candidates failing isolated restore or metadata.

The tested text envelope retains PostgreSQL en_US.utf8 ILIKE versus native SQLite
LIKE differences for accented case and Turkish I. Ordinary Q uses provider LIKE
wildcards; resolved Q escapes literals. Resolved tag membership uses actual mapped
snapshot tags, not a generic JsonElement LINQ predicate. The separate generic JSON
translation failure remains open. Mapped PostgreSQL microseconds tie literal 10/11
tick spans where SQLite preserves the difference; common 100/110 tick spans
discriminate both. Finite near-extreme dates do not qualify PostgreSQL infinity.
