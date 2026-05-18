# 26 — TypeScript SDK

## Goal
Ship a first-party TypeScript client SDK auto-generated from the Cmsify OpenAPI document, with a thin hand-written ergonomic layer on top. The SDK is the recommended way for consumers (Next.js, Astro, SvelteKit, Node services) to read content.

---

## Package

- **Name:** `@cmsify/client`
- **Repo location:** `sdk/typescript/` (top-level sibling of `src/`)
- **Published to:** npm
- **License:** MIT (matches the OSS license of Cmsify itself)
- **Node target:** Node 20+ and modern browsers (ESM-first, CJS fallback)

---

## Generation Strategy

Two-layer SDK:

### Layer 1 — Generated types and low-level client
- Tool: [`openapi-typescript`](https://github.com/drwpow/openapi-typescript) for types + [`openapi-fetch`](https://github.com/drwpow/openapi-typescript) for the runtime client
- Source: `Cmsify.Api`'s OpenAPI document (`/swagger/v1/swagger.json`)
- Regenerated on every API change via `npm run generate` (also invoked in CI)
- Output: `sdk/typescript/src/generated/{schema.ts,client.ts}` — checked in so consumers don't need a build step on install

### Layer 2 — Ergonomic wrapper
Hand-written, on top of the generated client. Provides:

```ts
import { CmsifyClient } from "@cmsify/client";

const cms = new CmsifyClient({
  baseUrl: "https://cms.example.com",
  apiToken: process.env.CMSIFY_API_TOKEN!,
  workspace: "marketing",          // resolves to workspace slug or id
});

// Strongly typed content fetch
const posts = await cms.content.list({
  templateSlug: "blog-post",
  status: "Published",
  tags: ["featured"],
  sortBy: "publishedAt",
  pageSize: 10,
});

const post = await cms.content.bySlug("my-first-post");

// Media (returns metadata; binary fetched separately)
const blob = await cms.media.download(post.fields.heroImage.mediaAssetId);
```

Surface area (MVP):
- `cms.auth` — login (for interactive use), tokenInfo
- `cms.workspaces` — list, getBySlug
- `cms.content` — list, get, bySlug, translations
- `cms.templates` — list, get
- `cms.media` — list, get, download (returns `Blob` / `ReadableStream`)
- `cms.health` — live, ready

Mutating endpoints (create/update/lifecycle transitions) are also exposed but marked in the docs as "for tooling / scripts" — the primary consumer pattern is read-only.

---

## Conventions

- **All methods return promises**. Errors are thrown as `CmsifyApiError` instances carrying the ProblemDetails payload (`type`, `title`, `status`, `detail`, `errors`, `extensions`, `traceId`).
- **Pagination:** list methods return `{ items, totalCount, page, pageSize, totalPages }`. A helper `cms.content.listAll()` yields an async iterable that pages automatically.
- **ETag support:** the SDK transparently tracks `ETag` headers on reads and sends `If-Match` on updates. Consumers can also pass an explicit `ifMatch` option.
- **Retry policy:** automatic retry on `429` (honouring `Retry-After`) and on transient `5xx` (exponential backoff, max 3 attempts). Disabled by passing `retry: false`.
- **Correlation IDs:** the SDK generates a `X-Correlation-Id` per request and exposes it on error objects for support.

---

## Authentication

The SDK supports a single auth mode for consumer use: `ApiClient` token via `Authorization: Bearer cmsify_...`.

Interactive login (email/password) is supported only for tooling/CLI scenarios; the SDK exposes it but the README discourages it for production consumers.

OIDC tokens are accepted transparently — they're just `Bearer` tokens the SDK passes through.

---

## Project Layout

```
sdk/typescript/
├── package.json
├── tsconfig.json
├── README.md
├── src/
│   ├── index.ts                 # Public entry — exports CmsifyClient
│   ├── client.ts                # Hand-written ergonomic wrapper
│   ├── errors.ts                # CmsifyApiError, error helpers
│   ├── pagination.ts            # listAll async iterable helper
│   ├── etag.ts                  # ETag tracking
│   └── generated/               # openapi-typescript output
│       ├── schema.ts
│       └── client.ts
├── test/                        # Vitest tests against a mocked fetch
└── scripts/
    └── generate.ts              # Calls openapi-typescript against /swagger/v1/swagger.json
```

---

## CI / Release

- `npm test` runs Vitest against a mocked HTTP layer (no live API needed)
- `npm run generate` regenerates the types from the local `Cmsify.Api` (started via `docker compose up api`) or from a pinned OpenAPI snapshot in `sdk/typescript/openapi.snapshot.json`
- A drift check in CI compares the generated output against the committed files; fails the build if `Cmsify.Api`'s OpenAPI changed without regenerating the SDK
- Versioning: SDK version tracks API version (`@cmsify/client@1.x.y` for `/api/v1`). Breaking API changes (new major) → new SDK major

---

## Documentation

- `sdk/typescript/README.md` — install, auth, common patterns (list, get, paginate, error handling)
- Code samples for Next.js (App Router), Astro, SvelteKit in the main repo's `examples/` directory
- TypeDoc-generated API reference published to GitHub Pages

---

## Tasks

- [x] Scaffold `sdk/typescript/` with `package.json`, `tsconfig.json`, ESM+CJS build
- [x] Install `openapi-typescript`, `openapi-fetch`, `vitest`, `tsup` (or equivalent bundler)
- [x] Implement `scripts/generate.ts` to regenerate from the API's OpenAPI document
- [x] Commit the initial generated output
- [x] Implement `CmsifyClient` ergonomic wrapper for the MVP surface area
- [x] Implement `CmsifyApiError` mapping ProblemDetails responses
- [x] Implement automatic ETag tracking and `If-Match` echo
- [x] Implement retry policy (`429` + transient `5xx`)
- [x] Implement `listAll` async iterable pagination helper
- [x] Write Vitest tests for client behaviour (auth header, ETag round-trip, error mapping, retry)
- [x] Set up CI drift check (regenerate from API → diff against committed → fail on drift)
- [x] Write `README.md` with install + usage
- [x] Author one example per framework (Next.js App Router, Astro, SvelteKit) under `examples/`
- [x] Configure npm publish workflow (manual `npm publish` from a release tag for MVP)

---

## Deliverables
- `@cmsify/client` package published to npm with full MVP read surface area
- Typed against the API's OpenAPI document with CI-enforced drift detection
- Ergonomic helpers for pagination, ETags, retries, and errors
- README + framework examples demonstrating real-world use
