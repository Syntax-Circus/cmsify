import createClient from "openapi-fetch";
import type { paths } from "./schema";

export const createCmsifyFetchClient = (baseUrl: string, fetchImpl?: typeof fetch) =>
  fetchImpl ? createClient<paths>({ baseUrl, fetch: fetchImpl as never }) : createClient<paths>({ baseUrl });
