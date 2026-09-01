import { CmsifyClient } from "@syntaxcircus/cmsify-client";

export const cms = new CmsifyClient({
  baseUrl: process.env.CMSIFY_API_URL!,
  apiToken: process.env.CMSIFY_API_TOKEN!,
  workspaceId: process.env.CMSIFY_WORKSPACE_ID!,
});

export async function getFeaturedPosts() {
  return cms.content.list({
    status: "Published",
    tags: "featured",
    pageSize: 10,
  });
}
