# @cmsify/client

First-party TypeScript SDK for Cmsify. It targets Node 20+ and modern browsers, ships ESM and CJS builds, and wraps the generated OpenAPI types with a small ergonomic client.

```bash
npm install @cmsify/client
```

```ts
import { CmsifyClient } from "@cmsify/client";

const cms = new CmsifyClient({
  baseUrl: "https://cms.example.com",
  apiToken: process.env.CMSIFY_API_TOKEN!,
  workspace: "workspace-id-or-slug",
});

const posts = await cms.content.list({
  templateSlug: "blog-post",
  status: "Published",
  tags: ["featured"],
  pageSize: 10,
});

for await (const post of cms.content.listAll({ templateSlug: "blog-post" })) {
  console.log(post.slug);
}
```

Errors are thrown as `CmsifyApiError` and include the RFC 7807 ProblemDetails payload, status, trace ID, and correlation ID. Read responses with `ETag` headers are tracked automatically and echoed as `If-Match` on mutating requests. The client retries `429` and transient `5xx` responses up to three attempts, honoring `Retry-After`.

Regenerate types after API OpenAPI changes:

```bash
npm run generate
```
