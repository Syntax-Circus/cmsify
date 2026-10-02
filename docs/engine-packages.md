# Engine NuGet packages and qualification

`SyntaxCircus.Cmsify.Core` and `SyntaxCircus.Cmsify.Infrastructure` package the existing .NET 10 engine assemblies. Assembly names and `Cmsify.Core.*` / `Cmsify.Infrastructure.*` namespaces are unchanged. Infrastructure depends on the matching Core package version; both follow the existing tag-derived release version and remain non-packable for ordinary local builds.

Both engine packages retain **AGPL-3.0-or-later** and include the repository's `LICENSE`, their README, source repository URL and release source commit. The separate SDK/contracts/components packages retain their existing MIT license. Creating these engine packages does not change the source license or establish that either new ID has been published.

## Host composition

The existing `AddCmsifyInfrastructure(configuration)` retains PostgreSQL and all Cmsify workers as defaults. It requires `ConnectionStrings:Cmsify`; storage and secret protection also require suitable host configuration. See [operations](operations.md) for operational settings and [provider portability](provider-portability.md) for existing registration options.

Hosts can register a scoped `ICurrentActor` and opt into its audit attribution with `UseHostCurrentActorForAudit = true`. Select workers explicitly with `CmsifyWorkers`; `None` disables all six. Audit identity grants no authorization. The existing workspace handlers continue to enforce actor capabilities and revision checks.

Resolving services does not migrate or seed a database. The host owns that lifecycle. `CmsifyDbContext.Database.MigrateAsync` applies the packaged PostgreSQL migrations; `ICmsifyDatabaseMigrator` also runs the existing seeder. The package qualification uses migrations directly to prove direct host workflows without creating Cmsify-local credentials.

## Repeatable package gate

The release workflow packs seven NuGet packages, including these two engine packages. Before the candidate artifacts can reach the gated `promote` job, it runs:

```powershell
node scripts/release/verify-engine-packages.mjs --packages artifacts/nuget --version X.Y.Z --source-sha <40-character-source-sha>
```

The same consumer is also a pull-request/main CI gate, using the local-only `0.0.0-package-test` version. It copies only the two candidate `.nupkg` files to a temporary feed and builds `tests/package-consumer` outside the checkout. It uses a new NuGet package/cache directory, clears inherited package sources and fallback folders, and maps the two engine IDs exclusively to that feed; public NuGet supplies the other dependencies. The consumer has only two `PackageReference` entries, no source/project references, and checks the resolved asset graph for the exact candidate versions.

The executable verifies the packed assembly files, repository/commit metadata, README metadata, AGPL license expression and included license bytes. It explicitly requires Infrastructure's Core dependency at the candidate version so the direct Core reference cannot hide a broken transitive dependency. It rejects a leaked EF Design dependency. Against its own disposable PostgreSQL 17 container, it verifies real DI registration, host audit mode, worker opt-out, all packaged migrations, workspace create/get/list/update/delete handlers, stale-revision rejection, persisted soft deletion, update outbox and two scoped host audit subjects without HTTP or Cmsify-local user/session/API-client records.

Docker and public NuGet access are required. The runner removes only its uniquely named container and its own temporary directory. Package bytes, local feeds and caches remain ignored and are not committed. `--restore-only` is a diagnostic option for reproducing absent-package restore failures; CI runs the full executable.

For a local candidate after locked restore, choose a fresh ignored output directory (incremental pack may retain an earlier package's metadata):

```powershell
dotnet build src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj --configuration Release --no-restore -p:CmsifyReleaseBuild=true -p:Version=0.0.0-package-test
dotnet pack src/Cmsify.Core/Cmsify.Core.csproj --configuration Release --no-build --output artifacts/engine-candidate -p:CmsifyReleaseBuild=true -p:PackageVersion=0.0.0-package-test -p:RepositoryCommit=<source-sha> -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
dotnet pack src/Cmsify.Infrastructure/Cmsify.Infrastructure.csproj --configuration Release --no-build --output artifacts/engine-candidate -p:CmsifyReleaseBuild=true -p:PackageVersion=0.0.0-package-test -p:RepositoryCommit=<source-sha> -p:IncludeSymbols=false -p:WarningsNotAsErrors=NU5104
node scripts/release/verify-engine-packages.mjs --packages artifacts/engine-candidate --version 0.0.0-package-test --source-sha <source-sha>
```

## Remaining qualification

This gate qualifies packaging and the bounded PostgreSQL workspace/host-audit path. It does not qualify complete application embedding, every content/template/media/authentication workflow, background-worker behavior in a consumer host, upgrade/rollback, restart durability, contention or capacity. Existing source tests and experiments remain evidence for their own scope; they are not substitutes for package-consumer qualification.

SQLite remains unqualified as a deployment provider: provider registration/migrations, broader repository/query translation, search, bulk concurrency-token behavior, worker parity and restart/load coverage remain incomplete. This change neither adds SQLite registration nor replaces PostgreSQL migrations with `EnsureCreated`.

See [measured qualification evidence](evidence/2026-10-02-engine-packages.md) for the implementation checkpoint. Before first publication, a maintainer must verify NuGet ownership/reservation and trusted-publisher scope permit **both** new package IDs. That registry prerequisite is not satisfied by a local package test and this change does not modify registry settings.
