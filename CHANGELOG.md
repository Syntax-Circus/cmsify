# Changelog

All notable changes to Cmsify are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.7.0] - 2026-09-18

### Added

- `POST /api/v1/workspaces/{ws}/content/{id}/versions/{n}/upgrade-template-version` could never move content onto a target template version that adds a required field - reproduced in production against a "home page" template whose new version added four required fields, permanently blocking the consumer's upgrade-then-update flow. The endpoint remapped each existing value onto the target's field with the same key (dropping values whose key no longer existed), then validated before saving; a newly-added required field has no old key to carry a value over from, so validation always failed with 422 `validation-failed` (`Field '…' requires at least 1 value(s).`). Nothing else in the API could supply the missing value instead: `CreateContentVersionRequest` has no template-version parameter (new versions deliberately follow the item's latest version's template), and `UpdateContentVersionRequest` cannot change template version - so a caller had no way to complete the upgrade at all. `ContentController.UpgradeTemplateVersion` now accepts an optional request body, `UpgradeTemplateVersionRequest(IReadOnlyList<ContentFieldValueRequest>? Fields)`. No body, or a body with `Fields` null, is exactly today's behaviour, byte for byte - the key remap, unchanged - since every existing caller (including the .NET SDK's original overload) sends no body. When `Fields` is supplied, the endpoint sets the version onto the target template version and calls the existing `ApplyVersionFieldValuesAsync` (the same full-replacement helper `UpdateVersion` uses) instead of the key remap, then validates the finished result as one atomic step; a validation failure (a missing required field, or a field id not present on the target) returns 422 without calling `SaveChangesAsync`, so a failed upgrade never half-applies - the version is left exactly where it was. Field ids in `Fields` refer to the TARGET template version's fields. The `[FromBody]` parameter uses `EmptyBodyBehavior.Allow` so a genuinely empty request (no `Content-Type`, zero length - what the existing .NET SDK sends) still binds to `null` and succeeds; the generated OpenAPI now marks the body `required: false`, additive per `docs/api-compatibility.md`. `SyntaxCircus.Cmsify.Client.ContentClient.UpgradeTemplateVersionAsync` keeps its existing signature (still posting no body, unchanged for every compiled consumer) and gained a new overload taking `IReadOnlyList<ContentFieldValueRequest> fields` that posts `UpgradeTemplateVersionRequest`.

## [0.6.2] - 2026-09-16

### Fixed

- `POST /api/v1/workspaces/{ws}/content/{id}/versions` with `DuplicateFromVersionNumber` set to a version that had been moved onto a newer template version via `.../versions/{n}/upgrade-template-version` (and then published) failed with a `CmsifyApiException`/422 `validation-failed`: "Field value '…' targets a field not present on the template version." repeated for every copied field - reproduced in production against 0.6.1. `ContentController.CreateVersion` loaded the template version to validate against from the ITEM-level `ContentItem.TemplateVersionId`, and assigned that same stale id to the new version, before copying the source version's field values and validating them against it. `ContentItem.TemplateVersionId` is set once at item creation (`Create`) and was never updated afterward: `UpgradeTemplateVersion` only ever mutated the individual version's own `TemplateVersionId`, so the moment any version was upgraded, the item-level field permanently pointed at the original template version while the actual versions moved on - and duplicating from an upgraded (and now-published) version copied field values that belonged to the new template version but validated them against the old one. Every other content-mutation endpoint (`UpdateVersion`, `Publish`, `UpgradeTemplateVersion` itself, `ScheduledPublishingRepository.CompleteClaimAsync`) already validated against the specific version's own `TemplateVersionId`, not the item-level one, and was unaffected; `ResolvedContentListQuery`, `GetBySlug`'s resolution path, and `Cmsify.Components`' `ContentEditSupport` (inline child loading) all likewise key off a version's own `TemplateVersionId`, not the item's. `CreateVersion` now resolves the template version to validate against per the actual semantics of the request instead of the item-level field: when duplicating, it uses the SOURCE version's own `TemplateVersionId` (the template version its copied field values actually belong to); otherwise, it uses the item's latest existing version's `TemplateVersionId` (the template version the content is already sitting on), so a plain `CreateVersion` after an upgrade validates the caller's fields correctly too, without ever silently jumping content onto an unrelated newer template version the caller never asked to upgrade to. Separately, `UpgradeTemplateVersion` now also keeps `ContentItem.TemplateVersionId` itself in sync - set to the target template version - whenever the version just upgraded is the item's latest version (by `VersionNumber`), so the informational item-level field read by `List`'s `templateVersionId`/`templateId` filters and by `ToItemSummaryResponseAsync`/`ToItemDetailResponseAsync`'s "template name" lookup no longer drifts from reality; upgrading an older, non-latest draft deliberately leaves it untouched rather than regressing it behind a newer version that's already ahead.

## [0.6.1] - 2026-09-16

### Fixed

- `POST /api/v1/workspaces/{ws}/content/{id}/versions/{n}/publish` intermittently 500'd with a `DbUpdateException` wrapping Postgres 23505 `duplicate key value violates unique constraint "ix_content_versions_content_item_id"` - seen in production for exactly the case where an older in-flight version (Draft/Approved) was published after a newer version had already taken the default-Published slot. That index is a partial unique index on `ContentItemId` filtered to default-published rows (`status = 'Published' AND effective_start_at/effective_end_at IS NULL`), and Postgres cannot make a partial index `DEFERRABLE` - it is checked per-statement, not at commit. `ContentPublishingService.PublishAsync` marked the prior default-published version Archived and the target version Published as two tracked changes flushed by a single `SaveChangesAsync`, and `ContentController.Publish` additionally flipped the target's `Status` to Published up front via `ContentLifecycleService.TransitionAsync` before calling `PublishAsync` at all. Content version ids are UUIDv7 (time-ordered); when the version being published had a lower id than the currently-published one, EF Core's batched `UPDATE`s could reach Postgres as "target → Published" before "prior → Archived", leaving two default-published rows for an instant and violating the index. `PublishAsync` now flushes the prior version's archival with its own `SaveChangesAsync` before the target version's `Status` is set to Published, guaranteeing the archival always lands first regardless of id ordering; `ContentController.Publish` no longer pre-sets the target's `Status` via `TransitionAsync` (`PublishAsync` already sets every field that call would have) and now wraps the whole publish - archival flush, target status flush, and the `content.version_published` outbox enqueue - in one explicit database transaction, so a later failure rolls the archival back too instead of stranding the prior version as Archived with no Published successor. `ScheduledPublishingRepository.CompleteClaimAsync` (the scheduled-publish worker's path, also calling `PublishAsync`) already ran inside its own explicit transaction and needed no changes beyond the fix in `PublishAsync` itself.

- `ContentEditPanelTests.RequireSlugBlocksSavingWithBlankSlugAndIssuesNoRequest` kept failing intermittently on CI (including the `main` run for 0.6.0) despite 0.5.4's `cut.InvokeAsync` change, because the real cause was not a render race on the click. The test waited for *any* `<input>` to render, but `ContentEditForm` renders its slug/locale/tags metadata inputs before the template's fields have loaded, so `Find("input")` could return the slug input: "My Title" was typed into the slug, the RequireSlug guard legitimately passed, and the expected slug error never appeared. The test (and six sibling `ContentEditPanelTests` using the same wait/lookup) now wait for and target `.cmsify-form-fields input` explicitly, and the RequireSlug test awaits each dispatched event so any dispatch failure surfaces as its real exception instead of an unobserved task. No product code changed.

## [0.6.0] - 2026-09-16

### Changed

- Opening a content item in `ContentEditPanel` was slow, especially for templates with Inline child content: `ContentController.ToVersionDetailResponseAsync` recursively expanded every field's `ChildContentItemId` one at a time (`ResolvePublishedVersionAsync`, one sequential DB query per child, up to 8 levels deep), and re-queried the same template's name/fields once per occurrence in the tree. Child resolution is now batched per recursion layer - one query resolves every distinct child id referenced anywhere at that depth (published, effective as of `asOf`, not soft-deleted, most-specific-per-item), instead of one query per child - and template name/field lookups are memoized per `TemplateVersionId` for the lifetime of a single response build. `GetVersion`, `CreateVersion`, `UpdateVersion`, and `GetBySlug` also gained an `expandChildren` query parameter (default `true`, preserving the exact existing response shape for every current consumer) so a caller that never reads the nested `Child` response - only `ChildContentItemId` - can skip the expansion entirely. `Cmsify.Components`' `ContentEditPanel`/`ContentEditSupport` (which only ever read `ChildContentItemId`, never `Child`) now request `expandChildren=false` on every version load/create/update they issue; the corresponding `SyntaxCircus.Cmsify.Client.ContentClient.GetVersionAsync`/`CreateVersionAsync`/`UpdateVersionAsync` gained a matching optional `expandChildren` parameter (default `true`) to support this.
- `ComponentSchemaResolver.ResolveAsync`'s breadth-first component-schema resolution fetched one component at a time. It now resolves each BFS layer's distinct unresolved ids concurrently with `Task.WhenAll`, keeping the same cycle-safety (the result dictionary is still the visited set) and per-id `CmsifyApiException` swallowing as before. It also gained an optional `seed` parameter so an ancestor's already-resolved schemas can be reused instead of re-fetched by its descendants; `ContentEditSupport.LoadInlineChildInstanceAsync`/`SaveInlineFieldAsync` now thread the parent's own resolved schemas down as that seed for every Inline child of one load/save (each call still resolves into its own private dictionary, so this is safe across the concurrently-loaded siblings at any one level).
- `ContentEditSupport.LoadInlineChildInstanceAsync` no longer eagerly mints a Draft version for every Inline child that lacks one when a parent is opened for editing - it now just reads whichever version (Draft if one exists, otherwise the serving/latest version) is already current, purely for display. A Draft is minted lazily, only when that child's own edits are actually saved (`SaveInlineFieldAsync`, by duplicating the displayed version), matching the existing ETag-priming/412-refresh handling for a newly-created child. `LoadInlineChildInstanceAsync` also now accepts the caller's already-fetched `Templates.ListAsync` result so every Inline child in one load reuses it instead of refetching it per child.
- The TypeScript client's checked-in OpenAPI snapshot and generated `schema.ts` now include the new optional `expandChildren` query parameter on the content version and by-slug endpoints.

## [0.5.5] - 2026-09-15

### Fixed

- `CmsifyOpaqueBearerAuthenticationHandler.VerifyApiClientCandidatesAsync` "touched" `ApiClient.LastUsedAt` on every API-client-authenticated request (at most once per `Auth:ApiClientTouchIntervalSeconds`) by mutating a tracked entity and calling `SaveChangesAsync`, and `ApiClient` uses PostgreSQL `xmin`-based optimistic concurrency. Two requests authenticating with the same API key inside the same touch window raced: the first save bumped the row's `xmin`, the second's threw an unhandled `DbUpdateConcurrencyException`, surfacing as a 500 `internal-server-error` - seen repeatedly in production for content-delivery endpoints under ordinary concurrent traffic. The touch now goes through `ExecuteUpdateAsync`, a raw conditional update that bypasses the change tracker and the concurrency token entirely, so concurrent touches settle as a race-free "last write wins" on this purely informational timestamp.
- `ResolveUserSessionAsync`'s structurally identical session-touch logic (`LastSeenAt`/`ExpiresAt`/`IpAddress`) got the same `ExecuteUpdateAsync` treatment for consistency, even though `UserSession` has no concurrency token today and could not throw the exception above.

## [0.5.4] - 2026-09-14

### Fixed

- `ContentEditPanel.SaveAsync`'s pre-flight Inline-child validation, `InlineChildValidation`'s nested-field recursion, and `ContentEditSupport.SaveInlineFieldAsync`'s per-child-field loop all treated `CompositionMode == Inline` alone as "this field embeds a child ContentItem." A `.ctp` schema's `CompositionMode` is a required property on every field, and many schemas (including production schemas that predate Inline child-content support) set it to `Inline` uniformly as a default rather than reserving it for genuine template-reference fields. Any such schema's ordinary required Text/Markdown/component field failed to save: pre-flight validation checked its (always-empty, since it's not a composition field) `ChildInstances` count against `MinOccurrences` and rejected the save with e.g. `'Title' requires at least 1 entry` — even though the field's actual value was fully populated and correctly displayed. The request never reached the server. All three call sites now share a new `ContentEditSupport.IsInlineChildField` helper, matching the `TemplateId`/`IsOpen`/`AllowedTypes` composition check `ContentEditPanel.SaveAsync`'s main save loop already used correctly.
- `ContentEditPanelTests.RequireSlugBlocksSavingWithBlankSlugAndIssuesNoRequest` used a plain unguarded `Find().Click()` on the save button, which could race a pending render under CI's coverage-instrumentation overhead - the same symptom already fixed for two other tests in 0.4.10/0.5.2's changelogs. This one was missed and intermittently failed the `main` CI workflow (including blocking this release's own `v0.5.3` tag, which published nothing and is abandoned). Wrapped the same interaction in `cut.InvokeAsync(...)`, matching the established pattern.

## [0.5.3] - 2026-09-14

Tag pushed but `dotnet test` failed in CI before packaging (a flaky test, fixed in 0.5.4 above) - no packages were ever published under this version. Left here for the historical record; treat 0.5.4 as the direct successor to 0.5.2.

## [0.5.2] - 2026-09-14

### Fixed

- `InlineChildContentEditorTests`' removal test asserted on `EventCallback`-captured state immediately after clicking the remove button, without waiting for the render to settle first - unlike every other assertion of this shape in the suite. Under coverage instrumentation's added scheduling overhead this raced and intermittently failed the `.NET tests` workflow on `main`. It now waits for the callback to fire before asserting, matching the pattern used elsewhere.
- Two `ContentEditPanelTests` inline-child-removal tests plain `Find().Click()`'d the remove/save buttons, which can race a pending render and throw bUnit's `UnknownEventHandlerIdException` - exposed by the bunit 2.9.0 → 2.11.3 bump below. Wrapped the same interaction in `cut.InvokeAsync(...)`, matching a sibling test in the same file that already guards against this.
- A Dependabot NuGet group bump left `Microsoft.Extensions.Hosting.Abstractions` centrally pinned below the version `Microsoft.AspNetCore.Mvc.Testing` now transitively requires, leaving every `packages.lock.json` inconsistent with the project graph and locked restore failing. Bumped the pin to match and regenerated the lock files.

### Changed

- Updated dependencies via Dependabot: GitHub Actions (`actions/checkout`, `actions/setup-node`, `actions/setup-python`, `actions/deploy-pages`, `actions/download-artifact`), NuGet packages (bunit, `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.AspNetCore.Http.Abstractions`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Testing.Extensions.CodeCoverage`, several `Microsoft.Extensions.*` packages, `Microsoft.NET.Test.Sdk`, `StackExchange.Redis`, `xunit.v3`), and `@types/node` in `sdk/typescript`.

## [0.5.1] - 2026-09-14

### Fixed

- `ComponentFieldEditor` (the structured editor for `componentRef` fields added in 0.5.0) rendered each nested field's input with no `<label>` at all, unlike `ContentEditForm`'s top-level fields - a user editing e.g. a "titled-copy" component's eyebrow/title/body saw three unlabeled boxes. The field's `Label`/`HelpText` already survived `ComponentFieldAdapter`'s conversion correctly; only the markup was missing. Nested fields now render the same `cmsify-form-label`/`cmsify-form-help` markup as top-level fields.

## [0.5.0] - 2026-09-14

### Added

- Component-typed fields (`TemplateField.ComponentId`) now render as first-class, recursive structured editors in `Cmsify.Components` instead of a raw JSON textarea — nested component-in-component fields, all primitive field types (including Media/File, referenced as a GUID within the component's snapshot JSON), and pick-list bindings all render and save correctly, with add/remove of repeated instances respecting `MinOccurrences`/`MaxOccurrences`.
- `CompositionMode.Inline` template-reference fields now render a first-class, recursive child-content editor in `Cmsify.Components` instead of a "not available yet" placeholder — a user can create, edit, and remove real child `ContentItem`s directly embedded in the parent's form, at any nesting depth. Since there is no server-side cascade-delete for Inline children and no server-side cycle protection for polymorphic (`IsOpen`) composition fields, the editor enforces a client-side depth/cycle guard (8 levels) and performs save writes children-before-parents, aborting on any child failure and deleting removed children only after the parent's own save succeeds.
- Documented when to use a component field versus a template-as-child-field (`Reference` vs. `Inline` composition) in `docs/content-modeling.md`.

### Changed

- **Breaking:** `ContentFieldEditorValue.ComponentValues` is now `IReadOnlyList<ComponentInstanceValue>` (was `IReadOnlyList<string>` of raw JSON). `ContentEditForm`'s and `FieldEditor`'s `OnMediaPickRequested`/`OnFilePickRequested` callbacks are now `EventCallback<ContentFieldEditorValue>` (were `EventCallback<TemplateFieldResponse>`), and `ContentEditForm.FieldValues` is now `IDictionary<Guid, ContentFieldEditorValue>` (was `IReadOnlyDictionary<...>`). `ComponentFieldEditor`'s entire parameter surface changed to support structured editing. Consumers who render `ContentEditForm`/`FieldEditor`/`ComponentFieldEditor` directly (rather than through the SDK-backed `ContentEditPanel`) will need to update call sites; `ContentEditPanel` itself absorbs all of these changes transparently.

## [0.4.10] - 2026-09-13

### Fixed

- `ContentEditPanel.LoadContentAsync`/`LoadPickListsAsync`/`LoadReferenceOptionsAsync` fetched each media/file asset, pick-list revision, and referenced-template option list one at a time in a sequential loop, turning a single content item's load into 5-10+ back-to-back round trips against Cmsify's API - painfully slow for any template with more than a couple of such fields. All three now gather the independent lookups first and run them concurrently via `Task.WhenAll`, preserving each item's own per-item error handling (a failed media/file lookup still falls back to its raw ID; a failed pick-list revision is still silently skipped) exactly as before - only the timing changed, not the behavior. `ContentListPanel.OnParametersSetAsync`'s initial template-list and content-list fetches are similarly now run concurrently instead of sequentially.

## [0.4.9] - 2026-09-13

### Added

- `ContentEditPanel` gained an optional `RequireSlug` parameter: when set, `SaveAsync` rejects a blank slug immediately (sets `error`, issues no API request) instead of silently creating/updating content with a null slug that a consumer's own downstream logic can't use. `ContentEditPanel`/`ContentEditForm` also gained a `Busy` surface — `ContentEditPanel.BusyChanged` (`EventCallback<bool>`) fires around the save API call, and `ContentEditForm`'s own Save button now disables and reads "Saving…" while busy — so consumers no longer need to guess whether a save click actually did anything.

## [0.4.8] - 2026-09-13

### Fixed

- `ContentEditPanel.OnParametersSetAsync` had no exception handling around `LoadContentAsync`/`LoadTemplateVersionAsync`, so any failure loading the initial content or template (an expired/invalid API token, a deleted template, a network error, etc.) propagated straight out of component initialization and crashed the whole hosting circuit before the form ever rendered — the same class of bug 0.4.7 fixed for `SaveAsync`, just on the load path instead. It now sets `error` and invokes `OnError` (if bound) the same way `SaveAsync` does, so the form still renders (empty) with the failure surfaced instead of taking the circuit down.

## [0.4.7] - 2026-09-13

### Added

- `ContentEditPanel` gained an optional `OnError` parameter (`EventCallback<Exception>`), invoked whenever `SaveAsync` catches an exception (from the save itself or from a `Saved`/`Created`/`ItemChanged` consumer callback), alongside the existing inline `error` message it already renders. Lets consumers hook additional behavior — logging, telemetry, a toast — off of save failures without scraping the rendered error text. A `OnError` handler that itself throws cannot escape `SaveAsync`; it's swallowed rather than risking the circuit-crash bug below.

### Fixed

- `ContentEditPanel.SaveAsync` only caught `CmsifyApiException` around invoking its `Saved`/`Created` callbacks, so any other exception thrown by a consumer's callback (e.g. a host app's own post-save side effect) propagated straight out of the Blazor event handler and terminated the whole hosting circuit, with nothing shown to the user beyond a silent, frozen form. A `catch (Exception ex)` now sets `error` for any non-`CmsifyApiException` failure from that invocation too, so a misbehaving consumer callback surfaces a message instead of taking down the host's circuit.

## [0.4.6] - 2026-09-13

### Fixed

- Release pipeline: `Cmsify.Components` and `Cmsify.Components.Theme` shipped zero static web assets in every previously-published version (no `wwwroot`/scoped-CSS content at all, only the compiled DLL), despite both projects genuinely having real content (`Cmsify.Components`' 14 scoped `.razor.css` files, `Cmsify.Components.Theme`'s `wwwroot/cmsify-theme.css`) — a consuming app referencing `_content/SyntaxCircus.Cmsify.Components.Theme/cmsify-theme.css` or expecting `Cmsify.Components`' scoped styles to bundle into its own isolation CSS got a 404/empty response instead. `IsPackable` is gated off by default (`Directory.Build.props`) unless `CmsifyReleaseBuild=true`; the release workflow's one real `dotnet build` step never set that, so it ran with `IsPackable=false`, and the later `dotnet pack --no-build -p:CmsifyReleaseBuild=true` couldn't retroactively compute the Razor SDK's static-web-asset-to-package items — those are only wired up when `Build` itself runs with `IsPackable=true`, and `--no-build` skips re-running it. `CmsifyReleaseBuild=true` is now also passed to the `dotnet build` step.

## [0.4.5] - 2026-09-12

### Fixed

- EF Core logged a `MultipleCollectionIncludeWarning` (visible in OTel) for any query that loaded more than one collection navigation without an explicit `QuerySplittingBehavior`, e.g. `ContentController.CreateVersion`/`UpgradeTemplateVersion` loading a template version's fields together with each field's allowed types, and the equivalent patterns in `TemplateVersionRepository`, `TemplatesController`, `PackagesController`, and `ComponentsController`. `QuerySplittingBehavior.SplitQuery` is now the default for the Npgsql provider, which both silences the warning and avoids the cartesian-product row explosion `SingleQuery` produces when joining multiple sibling collections.

## [0.4.4] - 2026-09-12

### Fixed

- `POST .../content/{id}/versions/{versionNumber}/upgrade-template-version` wiped every field value on upgrade, not just genuinely removed ones - it matched a version's existing values against the target template version's fields by field *Id*, but a package re-import always mints brand-new `TemplateField` rows for every field on every template version, even one whose key never changed. In practice this meant upgrading any content onto a newer template version always emptied every field, then immediately failed its own post-upgrade validation the moment any field was required. Values are now remapped onto the target field with the same *key*; only a value whose key genuinely no longer exists in the target is dropped.
- Suppressed chatty `Microsoft`/`System` Serilog categories so `Microsoft.EntityFrameworkCore.Database.Command` stops flooding the OTel collector at Information level.

## [0.4.3] - 2026-09-11

### Fixed

- Package import: importing a package that both introduced a brand-new component and required an explicit "replace" resolution for an already-installed component (e.g. adding a field to an existing component) failed with an opaque 500 (`DbUpdateConcurrencyException`) instead of succeeding. `PackagesController.CreateComponentVersion` only attached the replacement `ComponentVersion` via its parent's `Versions` navigation collection; since `Entity.Id` is assigned client-side at construction (`Guid.CreateVersion7()`), EF Core's change detection saw a non-default key reached only through an already-tracked (unchanged/modified) parent and inferred the row already existed, issuing an `UPDATE` whose optimistic-concurrency check then never matched instead of an `INSERT`. The equivalent PickList "replace" path (`AddRevision`) already explicitly added its new revision to the `DbContext`; `CreateComponentVersion` now does the same.

## [0.4.2] - 2026-09-10

### Fixed

- Release pipeline: `quay.io/skopeo/stable`'s tag digest was being rotated and garbage-collected by quay.io on a roughly 1-3 day cadence, breaking the pinned Skopeo helper used by `artifact-smoke`, `candidate-accessibility`, and `upgrade-rollback` (this exact failure recurred six times). The helper is now pinned against `ghcr.io/syntax-circus/skopeo`, a mirror this org controls.
- `upgrade-rollback`'s assertions (`eng/upgrade-tests/assertions.mjs`) were never updated for 0.4.0's version-centric Content API breaking change, so the job carried `continue-on-error: true` to mask real, permanent failures - which itself broke the release-contract governance test suite on every PR. The assertions are fixed against the current API contract and `continue-on-error` is removed from both `publish-cmsify.yml` and `upgrade-rollback.yml`; the release gate fails closed again.
- v0.4.1's release could not complete due to the Skopeo failure above; this release carries no functional changes beyond it and the previous fix.

## [0.4.1] - 2026-09-09

### Added

- A "View" action in the Admin content list and a new read-only content viewer (`/workspaces/{workspaceId}/content/{id}/view`, optionally `/view/{versionNumber}`) for inspecting a content item's currently-published (or any specific) version without creating a Draft. Previously, opening "Edit" on a Published or Archived item always minted a new Draft via `CreateVersionAsync`, even when the user only wanted to look at the content — there was no way to see it otherwise short of the crude raw-table "Inspect" modal on the Versions page. The viewer reuses the existing `SyntaxCircus.Cmsify.Components` field editors via a new non-interactive `ReadOnly` mode (added to `FieldEditor`, `FieldEditorRenderContext`, `ContentEditForm`, `ContentEditPanel`, `ContentListView`/`ContentListPanel`, and every field-type editor) instead of a second renderer, so formatting, resolved pick-list labels, and asset names render exactly as they do when editing. Reader-role users clicking "Edit" are now redirected into this read-only view instead of landing on a form they can't successfully save.

## [0.4.0] - 2026-09-07

### Added

- Content duplication in the Admin content editor (a new "Duplicate" button) — clones an existing item's fields into a new draft under a fresh slug/translation group, keeping locale and tags. Nothing equivalent existed anywhere before (domain, API, either SDK, or Admin UI).

### Changed

- **Breaking:** `ContentVersion` is now the sole carrier of content fields and workflow lifecycle (Draft → Review → Approved → Published → Archived); `ContentItem` is now a lightweight slug/template/locale identity header. This enables independently-editable, independently-scheduled date-bounded versions of the same content item. The Content API's item-level workflow routes (`/content/{id}/submit`, `/approve`, `/reject`, `/publish`, `/archive`, `/restore`, `/rollback`, `/upgrade-version`) are replaced by version-level equivalents (`/content/{id}/versions/{versionNumber}/submit`, etc.); `GetBySlug` now resolves to a `ContentVersionDetailResponse` instead of `ContentItemDetailResponse`. The TypeScript SDK's `content.bySlug` return type changes accordingly. This is a breaking change to the Content API and its contracts — accepted as acceptable pre-release, since there are no external consumers of the API or the TypeScript SDK yet.
- **Breaking (database):** the accompanying migration (`UnifyContentVersionLifecycle`) is not a pure schema change — it carries a hand-written data backfill. It moves every `ContentItem`'s status, publish schedule and field values onto a `ContentVersion` (materialising a new version row for any item that did not already have a matching one), renames `content_versions.retired_at` to `archived_at`, rewrites the `Retired` status to `Archived`, seeds `created_at`/`updated_at` on pre-existing `content_versions` rows, and then drops the `content_field_values` table along with the moved `content_items` columns. Operators upgrading an existing deployment should back up before applying it: `Down()` restores the dropped structure but does **not** reverse the data movement.
- **Breaking (webhooks):** content webhook event types now distinguish item lifecycle from version lifecycle. Added: `content.version_created`, `content.version_updated`, `content.version_deleted`, `content.version_status_changed`, `content.version_published`, `content.version_template_upgraded`. Removed (nothing emits them any more, and subscription requests naming them are now rejected): `content.published`, `content.status_changed`, `content.archived` — existing subscriptions to these must be repointed, most often `content.published` → `content.version_published`. `content.created`/`content.updated`/`content.deleted` are unchanged and still describe item-level lifecycle. Both publish paths (the API's publish action and the scheduled-publish background worker) now emit the same `content.version_published` event; previously the scheduled worker emitted `content.published`.
- **Breaking (audit log):** audit entries for content status changes now record an `EntityType` of `ContentVersion` instead of `ContentItem`, since the status being changed belongs to the version. Audit queries filtering on `entityType=ContentItem` to find status changes need updating.

### Fixed

- `Cmsify.Admin` (the Blazor admin UI) and the separate `sdk/dotnet` .NET client SDK — both left non-functional by the version-centric Content API change above — are restored. `Cmsify.Admin`'s Content pages (list, editor, version history, publish dialog, workspace dashboard) and `sdk/dotnet`'s `ContentClient` now target the version-scoped API. Note: the Admin publish dialog no longer offers the bounded-window ("publish as bounded override") inputs it previously had — that capability didn't survive the restoration and belongs to a future dated-version-management UI, not this pass; the dialog otherwise still supports scheduling a publish date and, for Admins, overriding the workflow gate. The Content list also no longer shows a definite status/workflow actions for an item with no currently-serving version (e.g. never-published, or Archived) — it shows "—" and a link into the editor instead, since the list's summary data can't distinguish those states; the editor page still resolves and displays the item's real status correctly.
- The Admin content editor no longer mints a new, orphaned Draft version every time it's reopened for a Published or Archived item. It previously re-derived the version to edit from the item's currently-serving version on every page load, ignoring any Draft/Review/Approved version that already existed — so repeat visits (and even Rollback, immediately followed by landing back on the editor) kept spawning throwaway Drafts instead of resuming the one already there. It now reuses the latest already-editable version whenever one exists.
- The Admin content editor no longer shows a false-positive "Content changed while saving" warning on a normal, single-editor save. Saving issues two sequential writes (the version, then the item's slug/locale/tags); the version write bumps the parent item's `UpdatedAt` as a side effect, which invalidated the item write's optimistic-concurrency ETag captured at page load — so the warning fired deterministically on every save, not just real conflicts. The item's ETag is now refreshed between the two writes.
- Creating a template field with `isOpen: true` and an `allowedTypes` entry that also specifies a `primitiveType` is now rejected (`422`) at creation time. Previously this was silently accepted, then produced a confusing "expects a child content value" error the first time any content was submitted against the field.
- Dark-mode contrast across the Admin app: shared `Cmsify.Components` form labels (via `Cmsify.Components.Theme`) rendered near-black on Admin's dark background because the theme's `--cmsify-*` variables were static light-mode values, never wired to Bootstrap's `[data-bs-theme="dark"]` convention — they now track the active theme's `--bs-*` variables instead. Bootstrap's outline button variants (`.btn-outline-secondary` and, once the Content list started using more of them, `-primary/-info/-success/-warning/-danger`) bake their colors to fixed light-mode hex values at compile time with no native dark-mode handling; dark-mode overrides now cover all of them, including disabled-button state (Bootstrap's extra opacity on `:disabled` was making already-muted colors unreadable).
- The Admin sidebar's build-version footer overflowed its container showing the full informational version string; it now shows a short version (without the `+sha` suffix). The About page's Admin/API version-match indicator is unaffected and keeps showing the full version.
- The Content list's Edit button now uses real Bootstrap `btn btn-sm` classes (via a new `EditButtonClass` parameter on the shared `ContentListView`/`ContentListPanel` components) instead of hand-approximated CSS, so it's pixel-identical to its row-action siblings instead of visibly oversized. Row-action buttons are recolored from a uniform, flat `btn-outline-secondary` to distinct, semantically-appropriate variants (Edit=primary, Submit=info, Approve=success, Reject=warning, Publish=success filled, Archive=danger, Restore=secondary), and their spacing no longer silently depends on incidental HTML whitespace between buttons that Razor could trim away.

## [0.3.2] - 2026-09-06

### Added

- New publishable package `SyntaxCircus.Cmsify.Components`: headless, restylable Blazor Server components for editing and managing Cmsify content, for embedding content editing directly in a consuming site instead of sending users to Cmsify Admin. Includes a field editor for every template field type, a `FieldEditor` dispatcher with a per-field-type override hook, composed `ContentEditForm`/`ContentListView` components, SDK-backed `Client.ContentEditPanel`/`Client.ContentListPanel` drop-in panels, and a shared media/reference picker. Structural styling only; every visual property is a `--cmsify-*` CSS custom property.
- New publishable package `SyntaxCircus.Cmsify.Components.Theme`: optional default styling for `SyntaxCircus.Cmsify.Components`, matching Admin's current look. Purely static CSS; omit it to theme the components yourself.

### Changed

- The Admin app's Content Editor and Content List pages now run on the new `SyntaxCircus.Cmsify.Components` package instead of Admin's previous hand-rolled markup, and its bespoke media picker has been replaced by the shared component.

### Fixed

- The Admin sidebar's build-version display (added in 0.3.1) sometimes rendered as literal text (`v@AdminBuildInfo.Version`) instead of the actual version, due to a Razor markup-parsing quirk. It now always shows the resolved version.

## [0.3.1] - 2026-09-05

### Added

- The Admin app now shows its running build version in the sidebar and on a new `/about` page, alongside the API's reported version (read from `/health/ready`) with a match/mismatch indicator — useful for confirming a deploy landed both images in sync.
- Admins can now publish content directly from Draft or Review, skipping Submit/Approve, via a confirmation dialog in the Admin UI (`PublishContentRequest.OverrideWorkflow`, requiring both the Admin role and explicit opt-in — existing callers see no behavior change unless they opt in).
- Reject (Review → Draft) and Restore (Archived → Draft) buttons on the Admin content list — both existed in the API already but had no UI entry point.
- The Content Editor's Lifecycle card now shows the same workflow action buttons (Submit/Approve/Reject/Publish/Archive/Restore) as the content list, so reviewing content no longer requires navigating back to the list.

### Changed

- The .NET SDK's `HealthClient.LiveAsync`/`ReadyAsync` (`client.Health`) now return a typed `HealthCheckResponse` (with `Status` and `Metadata.Version`/`Metadata.GeneratedAt`) instead of an untyped payload.

### Fixed

- Release promotion now also publishes the `:latest` Docker Hub tag alongside the version-numbered tag for stable (non-prerelease) releases. Previously only the versioned tag was pushed, leaving `:latest` stuck on an old release.
- Content workflow buttons (Submit/Approve/Archive) in the Admin UI previously failed silently when clicked on an item in the wrong status. They're now disabled with a tooltip explaining why when not applicable, and any remaining server-side rejection shows an error toast instead of doing nothing.

## [0.3.0] - 2026-09-05

### Added

- Client-side `${{name}}` template rendering (`CmsifyTemplateRenderer.Render` in the .NET client, `renderCmsifyTemplate` in the TypeScript client) for substituting caller-supplied variables into Text/Markdown field values read from Cmsify content. Purely opt-in and client-side; the server has no concept of variables. See "Rendering field templates" in `docs/integrating.md`.

### Fixed

- An expired Admin session no longer redirects to a raw HTTP 400 error page. Signing back in after expiry now always reaches the login form, and any remaining antiforgery-token mismatch on the login/logout endpoints redirects to the login page with a friendly "session expired" message instead.

## [0.2.4] - 2026-09-04

### Fixed

- Release restores now pin the Admin and Admin integration projects to the repository-signed `SyntaxCircus.Http.Resilience` package hash, matching the public .NET client dependency graph.

## [0.2.3] - 2026-09-04

### Added

- Optional OpenTelemetry/SigNoz and Sentry/GlitchTip telemetry is available to the API and Admin hosts through the reusable `SyntaxCircus.Observability` package, without changing the existing configuration sections.

### Fixed

- Creating a workspace in the Admin UI now selects and displays it immediately instead of leaving the workspace state empty until a browser refresh.

## [0.2.2] - 2026-09-04

### Fixed

- `AddCmsifyClient` is now covered by a regression test proving its `AddTypedClient`-based registration (introduced in 0.2.0) avoids the constructor-ambiguity crash (`InvalidOperationException: Multiple constructors accepting all given argument types...`) that the naive `services.AddHttpClient<CmsifyClient>()` pattern still hits. This fix shipped silently in 0.2.0 as part of the HTTP resilience consolidation; this entry makes it discoverable for anyone who hit the crash on 0.1.x.

## [0.2.1] - 2026-09-01

### Changed

- Releases are certified from a reviewed immutable SemVer tag. Branch and pull-request builds validate only and never publish artifacts or create tags.
- The TypeScript SDK uses the owned npm identity `@syntaxcircus/cmsify-client`; trusted publishing leaves token-style registry configuration unset so npm can exchange the GitHub Actions OIDC token.

### Release note

- `v0.2.0` was not completed as a GitHub Release. Its NuGet submissions and OCI images were accepted before npm rejected the unowned `@cmsify/client` scope. That tag remains immutable historical evidence; the next complete same-source release is `v0.2.1`.

## [0.2.0] - 2026-08-31

### Added

- First-party .NET and TypeScript SDK packages, Admin accessibility certification, production-like release smoke tests, and deterministic upgrade/rollback rehearsal.
- OIDC administration support, durable media reconciliation, package import/export, and release provenance/SBOM attestations.

### Changed

- Public SDK packages (`SyntaxCircus.Cmsify.Contracts`, both .NET clients, and `@syntaxcircus/cmsify-client`) are MIT-licensed; the server repository and OCI images remain AGPL-3.0-or-later.
- Workspace responses now include the actor-specific `canWrite` capability for permission-aware clients.
- The Admin app now generates slugs from a new workspace, template, picklist, or component name until the slug is manually edited.
- User-management forms show API validation details inline.
- Workspace management and selection now honor user role and per-workspace grants. The workspace picker is selectable only when multiple workspaces are available.
- Admin navigation now shows only settings available to the current role; webhook management remains an Editor-level, workspace-scoped feature.
- Admin static CSS, scripts, and branding assets use Blazor asset fingerprinting so deployments receive changed assets without stale browser caches.
- API JSON uses Cmsify's shared camel-case, string-enum wire format consistently.

### Fixed

- Unauthorized API requests now return normal `401` or `403` responses instead of requiring an unconfigured authentication scheme.

## [0.1.3] - 2026-08-21

### Fixed

- Corrected workspace permissions and administration authorization behavior.

## [0.1.0] - 2026-08-20

### Added

- Initial Cmsify release with a versioned HTTP API, PostgreSQL persistence, and a Blazor administration UI.
- Versioned templates, inline components, choice sets, content lifecycle, media, API clients, workspaces, audit history, webhooks, and scheduled publishing.
- First-party TypeScript and .NET clients for server-side integrations.
