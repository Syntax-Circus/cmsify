# Future content routing

This is a proposal for an optional routing feature. Cmsify currently resolves public content by ID or workspace-scoped slug. It should remain that way until a product requires managed public paths, nested sections, aliases, or redirects.

## Outcome

Let a published content version resolve from a normalized public path, such as `/products/widgets`, without turning routing into a second publishing or scheduling system.

The routing projection must always use Cmsify's existing timing rules:

- `PublishAt` controls when content is published.
- A single optional `EffectiveStartAt`/`EffectiveEndAt` range controls which published version is visible at a given time.
- Routes never introduce separate display, publication, or availability periods.

## Proposed model

Treat a route as a derived, version-aware public projection—not a replacement for the content item's slug.

| Concept | Proposed responsibility |
| --- | --- |
| Canonical route | The one normalized path that resolves to a published content version. |
| Route segment | A template-designated text value used to build a path. It is distinct from the content item's slug. |
| Parent route reference | An optional content reference that contributes the parent path, allowing nested routes. |
| Alias | An additional normalized path retained for compatibility; it either resolves canonically or redirects. |
| Redirect | An explicit response from an alias to a canonical path or approved relative/external target. |
| Route projection | The indexed lookup data produced from published content versions and invalidated when their effective visibility changes. |

Store canonical routes and aliases independently from editable content. Each projection should retain the source `ContentVersionId`, workspace, locale (when applicable), normalized path, and redirect behavior. This makes historical resolution and invalidation deterministic.

## Authoring design

Template fields should opt into routing through a narrowly scoped configuration rather than adding special content types everywhere:

- one optional **route segment** field per template version;
- one optional **parent route reference** field, restricted to routable templates;
- zero or more alias values; and
- an optional redirect target for aliases.

Authors edit values as normal content. On draft save, the API can preview the computed canonical path and report collisions. Publishing is the only action that activates or changes a route projection.

Useful validation rules:

- normalize paths consistently: leading slash, no duplicate separators, no dot segments, and a documented trailing-slash policy;
- reject empty segments, reserved paths, and invalid URL characters;
- enforce uniqueness for an active canonical path within the workspace and locale scope;
- prevent a content item from becoming its own route parent, directly or indirectly;
- reject alias/canonical conflicts unless an explicit ownership-transfer workflow is used;
- reject redirect loops and cap redirect-chain traversal; and
- never silently replace another content item's active route.

The initial version should keep a content item's existing `Slug` as independent metadata. A later product decision may offer a migration path from slug-based URLs, but slug changes must not implicitly rewrite routes or create redirects.

## Public resolution API

Add a read endpoint shaped like:

```http
GET /api/v1/workspaces/{workspaceId}/content/by-route?path=/products/widgets&locale=en&asOf=2026-08-20T12:00:00Z
```

The response should be one of:

- a resolved content-version payload with its canonical path;
- a redirect payload containing status code and target; or
- `404` when no route is visible at the requested time.

`asOf` must use the same effective-range selection already used by public content resolution. Route lookup must not select a draft, a scheduled-but-unpublished item, or a version outside its effective range.

For a future delivery endpoint that emits HTTP redirects directly, default permanent aliases to `308` and allow an explicitly configured temporary status only when needed. API consumers should still be able to receive redirect metadata without automatically following it.

## Resolution order

For one normalized path and locale:

1. Resolve an active canonical route for the `asOf` instant.
2. If none exists, resolve an active alias.
3. If the alias redirects, validate and return the target; otherwise return its canonical content-version result.
4. Return `404` if neither is visible.

If locale fallback is ever introduced, make it an explicit workspace policy and return the resolved locale. Do not silently mix locale variants with different paths.

## Persistence, indexing, and caching

Use relational tables for route projections and aliases, with a normalized-path column suitable for a unique active-route index. Keep immutable source-version identifiers so a content version can be retired without losing auditability.

Maintain a small route-resolution cache keyed by workspace, normalized path, locale, and resolution instant bucket only if profiling demonstrates it is needed. Invalidate affected entries when a content version is published, retired, archived, scheduled, or when a parent path changes. Cache invalidation must include descendants when hierarchical paths are supported.

## Delivery plan

1. Define path normalization, reserved-path policy, locale scope, and canonical/alias ownership rules.
2. Add route configuration to template versions and server-side preview/validation.
3. Add route projection tables, migrations, audit events, and conflict-safe publish transactions.
4. Add public route resolution with canonical, alias, redirect, effective-range, and historical `asOf` tests.
5. Add Admin route preview, conflict messages, alias management, and redirect controls.
6. Add package support only after the route contract is stable; imported routes must use explicit conflict resolutions.

## Deliberate exclusions for the first release

- No automatic aliases from every slug change.
- No wildcard, regex, or arbitrary route-template matching.
- No cross-workspace routes.
- No route changes outside normal content versioning and publish authorization.
- No separate routing schedule or visibility period.

This keeps routing useful for public websites while preserving Cmsify's existing content lifecycle and its single effective-range model.
