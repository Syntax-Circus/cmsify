# Engine NuGet packages and qualification

`SyntaxCircus.Cmsify.Core` and `SyntaxCircus.Cmsify.Infrastructure` package the existing .NET 10 engine assemblies. Assembly names and `Cmsify.Core.*` / `Cmsify.Infrastructure.*` namespaces are unchanged. Infrastructure depends on the matching Core package version; both follow the existing tag-derived release version and remain non-packable for ordinary local builds.

Both engine packages retain **AGPL-3.0-or-later** and include the repository's `LICENSE`, their README, source repository URL and release source commit. The separate SDK/contracts/components packages retain their existing MIT license. Creating these engine packages does not change the source license or establish that either new ID has been published.

## Host composition

The existing `AddCmsifyInfrastructure(configuration)` retains PostgreSQL and all Cmsify workers as defaults. It requires `ConnectionStrings:Cmsify`; storage and secret protection also require suitable host configuration. See [operations](operations.md) for operational settings and [provider portability](provider-portability.md) for existing registration options.

Hosts can register a scoped `ICurrentActor` and opt into its audit attribution with `UseHostCurrentActorForAudit = true`. Select workers explicitly with `CmsifyWorkers`; `None` disables all six. Audit identity grants no authorization. The existing workspace handlers continue to enforce actor capabilities and revision checks.

Resolving services does not migrate or seed a database. The host owns that lifecycle. `CmsifyDbContext.Database.MigrateAsync` applies the packaged PostgreSQL migrations; `ICmsifyDatabaseMigrator` also runs the existing seeder. The package qualification uses migrations directly to prove direct host workflows without creating Cmsify-local credentials.

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
Docker and public NuGet access are required by the default gate. The runner removes
only its uniquely named container and own temporary directory. For isolated Linux
orchestration, `CMSIFY_CONSUMER_POSTGRES` may supply a caller-owned disposable test
connection; that path never creates/removes Docker resources. Package bytes, feeds
and caches remain ignored. `--restore-only` restores and validates both package
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

This gate qualifies packaging and the bounded PostgreSQL workspace/host-audit path. It does not qualify complete application embedding, every content/template/media/authentication workflow, background-worker behavior in a consumer host, upgrade/rollback, restart durability, contention or capacity. Existing source tests and experiments remain evidence for their own scope; they are not substitutes for package-consumer qualification.

Optional SQLite registration and real schema-only migrations now have a separate
candidate gate. Full deployment qualification remains open: JSON queries, search,
broader repository workflows, bulk concurrency, full workers/load and production
backup recovery are separate gates. No earlier supported SQLite release exists
for a supported-version upgrade; synthetic source fixtures cover their own scope.

See [measured qualification evidence](evidence/2026-10-02-engine-packages.md) for the
original PostgreSQL checkpoint. Before optional-package publication, a maintainer
must verify NuGet ownership/reservation and trusted-publisher scope permit the
SQLite ID. Local checks do not satisfy that registry prerequisite or change settings.
