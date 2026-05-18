import { CMSIFY_API_TOKEN, CMSIFY_API_URL, CMSIFY_WORKSPACE } from "$env/static/private";
import { CmsifyClient } from "@cmsify/client";

export const cms = new CmsifyClient({
  baseUrl: CMSIFY_API_URL,
  apiToken: CMSIFY_API_TOKEN,
  workspace: CMSIFY_WORKSPACE,
});
