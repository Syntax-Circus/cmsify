import { describe, expect, it, vi } from "vitest";
import { CmsifyClient, CmsifyApiError } from "../src";

const jsonResponse = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...init.headers,
    },
  });

describe("CmsifyClient", () => {
  it("sends bearer auth and correlation headers", async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => jsonResponse({ items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 }));
    const client = new CmsifyClient({ baseUrl: "https://cms.example", apiToken: "cmsify_token", workspace: "workspace-id", fetch: fetchMock as typeof fetch });

    await client.content.list();

    const headers = fetchMock.mock.calls[0]?.[1]?.headers as Headers;
    expect(headers.get("Authorization")).toBe("Bearer cmsify_token");
    expect(headers.get("X-Correlation-Id")).toBeTruthy();
  });

  it("tracks ETags and sends If-Match on mutation requests", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ id: "1" }, { headers: { ETag: 'W/"abc"' } }))
      .mockResolvedValueOnce(jsonResponse({ id: "1" }));
    const client = new CmsifyClient({ baseUrl: "https://cms.example", apiToken: "cmsify_token", workspace: "workspace-id", fetch: fetchMock as typeof fetch });

    await client.content.get("1");
    await client.request("/api/v1/workspaces/workspace-id/content/1", { method: "PUT", body: JSON.stringify({}) });

    const headers = fetchMock.mock.calls[1]?.[1]?.headers as Headers;
    expect(headers.get("If-Match")).toBe('W/"abc"');
  });

  it("requests resolved content lists with optional asOf", async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => jsonResponse({ items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 }));
    const client = new CmsifyClient({ baseUrl: "https://cms.example", workspace: "workspace-id", fetch: fetchMock as typeof fetch });

    await client.content.list({ asOf: new Date("2026-12-24T12:00:00Z") });

    expect(String(fetchMock.mock.calls[0]?.[0])).toBe("https://cms.example/api/v1/workspaces/workspace-id/content?asOf=2026-12-24T12%3A00%3A00.000Z&resolve=true");
  });

  it("maps ProblemDetails responses to CmsifyApiError", async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ type: "https://cmsify.dev/errors/not-found", title: "Not found", status: 404, traceId: "trace" }, { status: 404 }));
    const client = new CmsifyClient({ baseUrl: "https://cms.example", workspace: "workspace-id", fetch: fetchMock as typeof fetch });

    await expect(client.content.get("missing")).rejects.toMatchObject({
      name: "CmsifyApiError",
      status: 404,
      traceId: "trace",
    });
  });

  it("retries rate-limited requests", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ title: "Rate limit", status: 429 }, { status: 429, headers: { "Retry-After": "0" } }))
      .mockResolvedValueOnce(jsonResponse({ status: "ready" }));
    const client = new CmsifyClient({ baseUrl: "https://cms.example", fetch: fetchMock as typeof fetch });

    await client.request("/health/ready");

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
