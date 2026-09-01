# `@syntaxcircus/cmsify-client`

The first-party TypeScript client provides a typed, server/edge delivery facade for Cmsify content, templates, and media. It targets Node 20+ and modern edge runtimes; do not import it into browser bundles or expose its API token to client-side code.

For management, authentication, or any endpoint beyond the curated delivery facade, use the exported generated fetch client instead.

## Install and build locally

```powershell
Set-Location sdk/typescript
npm ci
npm run typecheck
npm test
npm run build
npm run test:consumer
```

`test:consumer` packs the SDK, installs it into an empty temporary consumer, and typechecks that consumer and the checked-in Next.js, Astro, and SvelteKit server examples through only public exports. CI runs it on Node 20 and 22.

## Configure a server-side delivery client

```ts
import { CmsifyClient } from "@syntaxcircus/cmsify-client";

const cms = new CmsifyClient({
  baseUrl: process.env.CMSIFY_API_URL!,
  apiToken: process.env.CMSIFY_API_TOKEN!,
  workspaceId: process.env.CMSIFY_WORKSPACE_ID!, // GUID only; slugs are rejected
  timeoutMs: 5_000,
});

const posts = await cms.content.list({
  status: "Published",
  tags: "featured",
  sortBy: "publishedAt",
  pageSize: 10,
});

const post = await cms.content.bySlug("my-first-post");
```

List operations return `{ items, totalCount, page, pageSize, totalPages }`, including `content.translations`. Consume every page with `content.listAll`:

```ts
for await (const post of cms.content.listAll({ status: "Published" })) {
  console.log(post.slug);
}
```

Keep `CMSIFY_API_TOKEN` in a server-only secret store. The checked-in Next.js App Router, Astro, and SvelteKit examples use server/private environment mechanisms. API tokens are opaque bearer credentials; applications must not parse or reconstruct them.

## Generated raw client

The complete generated OpenAPI surface remains available without weakening the delivery facade:

```ts
import { createCmsifyFetchClient, type paths } from "@syntaxcircus/cmsify-client";

const raw = createCmsifyFetchClient(process.env.CMSIFY_API_URL!);
const response = await raw.GET("/api/v1/workspaces/{workspaceId}/content", {
  params: { path: { workspaceId: process.env.CMSIFY_WORKSPACE_ID! } },
});
```

`generated`, `paths`, and `components` are also exported for generated schema access. Generated files under `src/generated` are not handwritten API surface and must be regenerated only through the OpenAPI workflow.

## Errors, retries, cancellation, and concurrency

Failures are `CmsifyApiError` instances carrying RFC 7807 fields (`type`, `title`, `status`, `detail`, `errors`, `extensions`, and `traceId`) plus the server correlation ID. `CmsifyTimeoutError` identifies an expired SDK timeout budget.

By default the client retries `429`, transient `5xx`, and transport faults up to three attempts for idempotent methods only. `Retry-After` supports both delta-seconds and HTTP-date values. A non-idempotent request is retried only when `RequestOptions.idempotencyKey` is supplied. Pass `retry: false`, `timeoutMs`, or a caller `signal` in `RequestOptions` to control one request; timeout budgets include retries and a caller abort is never retried.

ETags from read responses are tracked and used as `If-Match` on later mutations of the same URL. An explicit `ifMatch` overrides the tracked ETag. Successful empty and `204 No Content` responses return `undefined`.

## OpenAPI generation

Generated files are under `src/generated` and should not be edited by hand. After an API contract change:

```powershell
Set-Location ../..
dotnet restore Cmsify.slnx --locked-mode
node scripts/openapi.mjs update
Set-Location sdk/typescript
npm run generate:check
npm run typecheck
npm test
npm run build
npm run test:consumer
```

`update` is the only command allowed to modify the checked-in OpenAPI snapshot or generated TypeScript files. `generate:check` is non-mutating: it exports the live document and generates into a temporary directory before checking live-to-snapshot and generated-to-tracked drift. Both commands build `Cmsify.Api` with `--no-restore`, so complete the applicable public or approved ignored-feed locked solution restore from the repository root first.
