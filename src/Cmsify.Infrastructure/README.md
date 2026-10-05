# SyntaxCircus.Cmsify.Infrastructure

Cmsify's existing `Cmsify.Infrastructure` assembly packaged for .NET 10 hosts. Namespaces remain `Cmsify.Infrastructure.*`. The matching `SyntaxCircus.Cmsify.Core` package is a transitive dependency.

Includes PostgreSQL persistence and migrations, repositories, storage registration, audit interception and the existing background services. `AddCmsifyInfrastructure(configuration)` retains PostgreSQL and all Cmsify workers as its defaults. Hosts can use the existing `CmsifyInfrastructureOptions` to select workers and opt into their own scoped `ICurrentActor` for audit.

The engine is licensed **AGPL-3.0-or-later**, as specified by the included `LICENSE`. The MIT license of Cmsify's separate SDK and component packages does not apply to the engine.

Packaging does not establish complete application embedding or SQLite support. See [engine package qualification](https://github.com/Syntax-Circus/cmsify/blob/main/docs/engine-packages.md) for the measured scope, required host configuration and remaining limitations.

Optional SQLite registration and schema-only migrations are supplied separately by
`SyntaxCircus.Cmsify.Infrastructure.Sqlite`; see the [embedded SQLite guide](https://github.com/Syntax-Circus/cmsify/blob/main/docs/integrations/embedded-sqlite.md).
Infrastructure-only consumers retain their PostgreSQL package graph.

Registration also supplies the scoped `IUpdateContentVersionRequestHandler` from
`Cmsify.Core.ContentWrites`. A host can edit an existing Draft, Review or Approved
version directly, without loading the API assembly:

```csharp
// Inject IUpdateContentVersionRequestHandler into the host's named use-case handler.
var result = await updateVersion.HandleAsync(new UpdateContentVersionRequest(
    workspaceId, itemId, versionNumber, new ContentVersionRevisionCondition(revision),
    effectiveStartAt, effectiveEndAt,
    [new ContentVersionFieldInput(fieldId, 0, ValueKind.Text, "Updated title",
        null, null, null, null, null)], ExpandChildren: false), cancellationToken);
if (result.IsSuccess)
    revision = result.Value.Revision;
```

Supply a scoped host `ICurrentActor` before registration; the handler checks
authentication, Editor-or-higher permission and workspace write authorization.
Actor identity is never accepted in the save request. Obtain the numeric revision
from a prior detail timestamp (`UpdatedAt.UtcTicks / 10`) or successful save output.
The save fully replaces fields and atomically persists version, parent search and
the existing single outbox event. It does not dispatch that event.

Each operation constructs and disposes its own context from the existing scoped
`DbContextOptions<CmsifyDbContext>`, retaining host options and audit interceptors.
It neither reuses nor clears a caller's tracked changes. Keep the handler and its
actor/options inside the request scope; do not capture them in a singleton.
The returned detail is detached and materialized before disposal. Projection or
cancellation after commit can still fail after the write persisted; there is no
automatic retry or exactly-once acknowledgement. This bounded save extraction
does not extract the remaining content lifecycle workflows or establish complete
provider qualification.

Registration supplies `IListContentRequestHandler` and PostgreSQL content-list
queries for ordinary item metadata and resolved serving snapshots. Register a
scoped host actor and optionally `TimeProvider` before composition. Local candidate
`0.8.9-query.1` consumer checks cover both modes, tags, Q, paging and denial without
an API host; candidate evidence does not claim publication or generic JSON parity.

Webhook outbox/delivery claim selection and outbox materialization use an Infrastructure-only provider strategy. PostgreSQL retains its row-locking queries; SQLite uses mapped queries inside a non-deferred writer-reserving transaction, so competing SQLite writers wait through lease mutation or materialization commit. Lease owner/token/expiry fencing and atomic delivery intent creation remain repository responsibilities. The file-backed SQLite tests create the schema with `EnsureCreated`; this bounded repository coverage does not qualify SQLite migrations, registration, other webhook workflows, or a complete SQLite deployment.
