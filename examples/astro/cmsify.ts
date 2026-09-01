import { CmsifyClient } from "@syntaxcircus/cmsify-client";

export const cms = new CmsifyClient({
  baseUrl: import.meta.env.CMSIFY_API_URL,
  apiToken: import.meta.env.CMSIFY_API_TOKEN,
  workspaceId: import.meta.env.CMSIFY_WORKSPACE_ID,
});

export const getDocsPages = () =>
  cms.content.list({ status: "Published", pageSize: 50 });
