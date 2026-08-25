import { CMSIFY_API_TOKEN, CMSIFY_API_URL, CMSIFY_WORKSPACE_ID } from "$env/static/private";
import { CmsifyClient } from "@cmsify/client";

export const cms = new CmsifyClient({
  baseUrl: CMSIFY_API_URL,
  apiToken: CMSIFY_API_TOKEN,
  workspaceId: CMSIFY_WORKSPACE_ID,
});
