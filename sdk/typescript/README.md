# `@cmsify/client`

The first-party TypeScript client wraps Cmsify's OpenAPI contract with typed helpers for server-side applications. It targets Node 20+ and modern browsers, ships ESM and CJS builds, and supports workspaces, content, templates, media, health, and authentication.

The package is currently maintained in this repository and its public npm publication is pending. During development, work from `sdk/typescript` and use the checked-in examples under [`../../examples`](../../examples).

## Install and build locally

```powershell
Set-Location sdk/typescript
npm ci
npm run typecheck
npm test
npm run build
```

When the package is published, the consumer install will be:

```powershell
npm install @cmsify/client
```

## Configure a server-side client

```ts
import { CmsifyClient } from "@cmsify/client";

const cms = new CmsifyClient({
  baseUrl: process.env.CMSIFY_API_URL!,
  apiToken: process.env.CMSIFY_API_TOKEN!,
  workspace: process.env.CMSIFY_WORKSPACE!, // workspace ID or slug
});

const posts = await cms.content.list({
  templateSlug: "blog-post",
  status: "Published",
  tags: ["featured"],
  sortBy: "publishedAt",
  pageSize: 10,
});

const post = await cms.content.bySlug("my-first-post");
```

Keep `CMSIFY_API_TOKEN` in a server-only secret store. Do not pass it to browser code, expose it in a public environment variable, or include it in a client-side bundle. The supported integration examples use the private/server environment mechanisms for Next.js App Router, Astro, and SvelteKit.

## Common operations

All methods return promises. List methods return `{ items, totalCount, page, pageSize, totalPages }`; use `listAll` when the application should consume every page:

```ts
for await (const post of cms.content.listAll({ templateSlug: "blog-post" })) {
  console.log(post.slug);
}
```

The client also exposes:

- `cms.auth` — login for interactive tooling and `tokenInfo`.
- `cms.workspaces` — list and get by slug.
- `cms.content` — list, get, slug lookup, translations, and lifecycle/tooling operations.
- `cms.templates` — list and get.
- `cms.media` — list, get, and download.
- `cms.health` — liveness and readiness checks.

Mutating operations are available for tooling and scripts. Production content delivery should normally use a workspace-scoped `Reader` API client.

## Errors, retries, and concurrency

Failures are thrown as `CmsifyApiError` instances carrying the RFC 7807 ProblemDetails fields (`type`, `title`, `status`, `detail`, `errors`, `extensions`, and `traceId`) plus the request correlation ID.

The client retries `429` responses (honoring `Retry-After`) and transient `5xx` responses with exponential backoff, up to three attempts. Pass `retry: false` to disable retries when appropriate.

Read responses with `ETag` headers are tracked automatically and echoed as `If-Match` on mutating requests. An explicit `ifMatch` option can override the tracked value. Preserve this behavior when dropping down to the generated client or raw HTTP.

## OpenAPI generation

Generated files are under `src/generated` and should not be edited by hand. After an API contract change:

```powershell
npm run generate
npm run generate:check
npm run typecheck
npm test
npm run build
```

The generator reads the API's Swagger document when available or the pinned `openapi.snapshot.json` for offline/CI checks. CI fails if generated output drifts from the source contract.
