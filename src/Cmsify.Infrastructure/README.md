# SyntaxCircus.Cmsify.Infrastructure

Cmsify's existing `Cmsify.Infrastructure` assembly packaged for .NET 10 hosts. Namespaces remain `Cmsify.Infrastructure.*`. The matching `SyntaxCircus.Cmsify.Core` package is a transitive dependency.

Includes PostgreSQL persistence and migrations, repositories, storage registration, audit interception and the existing background services. `AddCmsifyInfrastructure(configuration)` retains PostgreSQL and all Cmsify workers as its defaults. Hosts can use the existing `CmsifyInfrastructureOptions` to select workers and opt into their own scoped `ICurrentActor` for audit.

The engine is licensed **AGPL-3.0-or-later**, as specified by the included `LICENSE`. The MIT license of Cmsify's separate SDK and component packages does not apply to the engine.

Packaging does not establish complete application embedding or SQLite support. See [engine package qualification](https://github.com/Syntax-Circus/cmsify/blob/main/docs/engine-packages.md) for the measured scope, required host configuration and remaining limitations.

Webhook outbox/delivery claim selection and outbox materialization use an Infrastructure-only provider strategy. PostgreSQL retains its row-locking queries; SQLite uses mapped queries inside a non-deferred writer-reserving transaction, so competing SQLite writers wait through lease mutation or materialization commit. Lease owner/token/expiry fencing and atomic delivery intent creation remain repository responsibilities. The file-backed SQLite tests create the schema with `EnsureCreated`; this bounded repository coverage does not qualify SQLite migrations, registration, other webhook workflows, or a complete SQLite deployment.
