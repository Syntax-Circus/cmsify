# Embedded template and content operations

The server-side Core handlers and Infrastructure repositories provide six explicit PostgreSQL operations. Register Infrastructure with `UseHostCurrentActorForAudit = true` and `Workers = CmsifyWorkers.None` for an ordinary host composition. Migration and workspace provisioning remain explicit operator tasks. Registration does not install templates, migrate databases, seed accounts, or start an embedded-content worker.

| Handler interface | Purpose |
| --- | --- |
| `IEnsureEmbeddedTemplateRequestHandler` | Install one immutable declared schema and publish its template version atomically; matching repeats return its real IDs. |
| `IGetEmbeddedTemplateRequestHandler` | Read and verify the registered schema against its actual persisted definition. |
| `ICreateEmbeddedContentRequestHandler` | Create a reserved item identity, complete first draft, and operation receipt atomically. |
| `IGetEmbeddedContentVersionRequestHandler` | Read one exact saved version with optional revision equality and no child expansion. |
| `ICreateEmbeddedContentVersionRequestHandler` | Create a new draft with complete replacement values after checking an exact source revision. |
| `IGetEmbeddedContentOperationReceiptRequestHandler` | Establish that a correlated operation committed, without returning original fields or changing state. |

Each interface exposes `HandleAsync(request, CancellationToken)` returning a Common `Result<T>`. Outputs own collections and JSON and remain usable after scope disposal. Exact selected-version reads use a coherent PostgreSQL snapshot; omission of the revision condition refreshes that version, without selecting latest.

## Explicit host authority

The default `IEmbeddedContentAuthorizationService` and `IEmbeddedTemplateSetupAuthorizationService` deny. Supply request-scoped adapters bound to an immutable host operation after fresh session/account/resource validation. Workspace authority and Editor do not replace resource authority. Template reads initially have an empty template ID; content requests initially lack a contract key. The repository repeats authorization with the actual registered template, contract key, item and selected version inside its transaction. Your adapter must permit the unresolved request identity only when the rest of the pinned operation matches, then require the actual loaded identity.

Setup authority is separate: pin workspace, contract key and canonical fingerprint, and expose it only in explicit operator composition. It requires an authenticated nonempty host GUID for audit, without TemplateAdmin or SuperAdmin promotion. Reads require Reader and workspace read; writes require Editor, workspace write and a nonempty audit subject. Content actors can retain `IsSuperAdmin = false`, null `ApiClientId` and null `WorkspaceId`.

The existing update handler accepts an optional `IContentVersionResourceGuard`, evaluated against its actual loaded snapshot before preparing fields or committing. The snapshot adds an init-only `TemplateVersionId`; the existing positional snapshot and four-argument handler constructor remain available. Standalone defaults permit the guard. An embedded host must supply a restrictive adapter requiring its registered template ID and exact workspace/item/version/update operation; an empty template identity must deny. Defaults use `TryAdd`, preserving explicitly registered host adapters.

See [`tests/package-consumer/EmbeddedContentQualification.cs`](../tests/package-consumer/EmbeddedContentQualification.cs) for a fictional scoped host using all six handlers plus the update guard. It uses public package APIs; persistence reads are independent test oracles.

## Schema and values

Contracts support closed Inline Text fields with Text values, one occurrence maximum, positive integer `maxLength` and optional `formatHint: "plaintext"`. Unknown config, duplicate JSON property names/field keys/orders, unsupported primitives, composition or reference/media/child values fail validation. Canonical SHA256 fingerprints include the declared name, slug, title key and every supported field attribute/config; IDs, timestamps and audit identity are excluded. Generated field GUIDs are returned by setup and must be used for values.

The consumer fixture `puppies-plus.dog-profile.v1` declares name/breedName/sex/birthDate/introduction with maxima 200/200/7/10/4000 and first two required. These are declared consumer fields, not engine breeder rules. Date and sex remain Text: the host validates ISO calendar dates and its enum, stores `Female`/`Male`, and represents Unknown as an absent field. Text schemas do not enforce those semantics.

Matching ensure is read-only. Drift, soft deletion, an unpublished registered schema or an unregistered slug collision fails closed and is never repaired implicitly. There is no schema-upgrade API in this increment. New operations reject non-PostgreSQL providers before writes; SQLite model/migration regressions remain supported for existing APIs.

## Correlation and uncertain completion

Reserve a nonempty item GUID and stable `OperationKey` before item creation. Reuse that same key and input after an uncertain response. Never create a new key automatically or adopt an occupied foreign item. Receipt uniqueness is workspace/write kind/key; the private input fingerprint includes the actor, identities, normalized source revision and complete fields.

New-version creation atomically stores the complete replacement fields. The source is unchanged. It does not clone and issue a separate update, and it does not publish content. A matching committed receipt is checked before the original source revision on replay. Fresh resource authorization always applies to receipts and replay, including after revocation.

Commit may precede response cancellation or projection failure. The handler never automatically retries or deletes a committed mutation. Use receipt lookup to prove commit. A receipt contains committed identity/version/revision, subject and timestamp, without original fields. If a receipt's saved version has since changed, write replay returns `embedded-refresh-required`; a fresh exact-version read can refresh it. Original responses also check projected revision against the receipt and return refresh-required for a racing later edit.

Expected failures include existing authentication/forbidden/not-found/concurrency codes and `embedded-validation`, `embedded-template-mismatch`, `embedded-operation-conflict`, `embedded-refresh-required`, `embedded-provider-unsupported`. Unexpected exceptions and cancellation propagate to the host. Existing HTTP actions, routes, DTOs, ETags/If-Match and OpenAPI/SDK contracts retain their legacy orchestration.
