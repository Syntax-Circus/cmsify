# Components and versioned choice sets

Cmsify has one visibility model: `PublishAt` schedules publication and the optional `EffectiveStartAt`/`EffectiveEndAt` range controls when that published snapshot is resolved. It does not have separate display and publishing periods.

## Components

Components are workspace-scoped, inline-only schemas for reusable blocks such as calls to action, cards, and profile snippets. They cannot be published as standalone content. Create a component, define and publish its schema version, then bind it to an inline field on a template version.

Components can nest. Cmsify rejects direct and indirect cycles. Component values are stored in the parent field as JSON, so a published content version contains the full component tree and never changes when a later component schema is published.

Use the Admin **Components** page to manage schemas. In the content editor, component values are JSON objects keyed by component field key; this keeps nested values explicit and portable for API clients.

## Choice sets

Pick lists remain shared, workspace-scoped option catalogs. Every edit creates an immutable revision, and template field configuration can bind `picklistRevisionId` alongside `picklistId`.

At publication, Cmsify stores the option label in the content-version snapshot. Use `displayLabel` when displaying historical published content and `textValue` as the stable option value. Later option renames or deletions never change already-published labels.

## API and SDK

Component endpoints are under `/api/v1/workspaces/{workspaceId}/components` and follow the usual workspace authorization and ETag/`If-Match` conventions. The .NET SDK exposes these endpoints through `CmsifyClient.Components`.

When evolving these APIs, update shared contracts, regenerate the TypeScript client from OpenAPI, and run its generation/type/test/build checks. Do not hand-edit generated TypeScript files.
