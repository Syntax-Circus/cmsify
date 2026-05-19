import { CmsifyClient } from "@cmsify/client";

export const cms = new CmsifyClient({
  baseUrl: process.env.CMSIFY_API_URL!,
  apiToken: process.env.CMSIFY_API_TOKEN!,
  workspace: process.env.CMSIFY_WORKSPACE!,
});

export async function getFeaturedPosts() {
  return cms.content.list({
    templateSlug: "blog-post",
    status: "Published",
    tags: ["featured"],
    pageSize: 10,
  });
}
