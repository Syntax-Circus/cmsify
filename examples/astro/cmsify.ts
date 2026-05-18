import { CmsifyClient } from "@cmsify/client";

export const cms = new CmsifyClient({
  baseUrl: import.meta.env.CMSIFY_API_URL,
  apiToken: import.meta.env.CMSIFY_API_TOKEN,
  workspace: import.meta.env.CMSIFY_WORKSPACE,
});

export const getDocsPages = () =>
  cms.content.list({ templateSlug: "doc-page", status: "Published", pageSize: 50 });
