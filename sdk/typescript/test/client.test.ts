import { describe, expect, it, vi } from "vitest";
import { CmsifyClient, createCmsifyFetchClient } from "../src";

const workspaceId = "5c2d8d41-16aa-4d08-910d-110644525b67";
const contentSummary = {
  id: "5b06c3e0-3b3e-46b4-9ee2-5720e33e61ee", workspaceId,
  templateVersionId: "0bd3db7f-bf20-46f7-85c0-c5f67e46570d", templateName: "Blog post", templateSlug: "blog-post",
  status: "Published", slug: "hello-world", localeCode: "en-US", translationGroupId: "d70ea021-802d-4788-9ce5-4450c1727a5b",
  publishAt: "2026-08-24T12:00:00Z", effectiveStartAt: null, effectiveEndAt: null, tags: ["featured"],
  createdAt: "2026-08-24T11:00:00Z", updatedAt: "2026-08-24T12:00:00Z",
};
const pagedContent = { items: [contentSummary], totalCount: 1, page: 1, pageSize: 20, totalPages: 1 };
const jsonResponse = (body: unknown, init: ResponseInit = {}) => new Response(JSON.stringify(body), {
  ...init, headers: { "Content-Type": "application/json", ...init.headers },
});
const requestHeaders = (fetchImpl: { mock: { calls: unknown[][] } }, call: number): Headers =>
  new Headers(((fetchImpl.mock.calls[call] as [unknown, RequestInit | undefined] | undefined)?.[1])?.headers);
const createClient = (fetchImpl: typeof fetch, options: Record<string, unknown> = {}) => new CmsifyClient({
  baseUrl: "https://cms.example", workspaceId, fetch: fetchImpl, ...options,
});

describe("CmsifyClient", () => {
  it("rejects a workspace slug so delivery requests cannot be sent to a non-GUID workspace", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse(pagedContent));
    const client = new CmsifyClient({ baseUrl: "https://cms.example", workspaceId: "marketing", fetch: fetchImpl as typeof fetch });
    expect(() => client.content.list()).toThrow("workspaceId must be a GUID");
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it("accepts a canonical GUID without imposing an SDK-specific UUID version", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse(pagedContent));
    const client = new CmsifyClient({ baseUrl: "https://cms.example", workspaceId: "018fd602-7e22-7f33-8c31-5f0a0d292c70", fetch: fetchImpl as typeof fetch });
    await expect(client.content.list()).resolves.toEqual(pagedContent);
  });

  it("uses the explicit workspace GUID and returns the generated paged content shape", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse(pagedContent));
    await expect(createClient(fetchImpl as typeof fetch).content.list({ page: 1, pageSize: 20 })).resolves.toEqual(pagedContent);
    expect(String((fetchImpl.mock.calls as unknown[][])[0]?.[0])).toBe(`https://cms.example/api/v1/workspaces/${workspaceId}/content?Resolve=true&Page=1&PageSize=20`);
  });

  it("returns translations as a page so consumers retain page metadata", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse(pagedContent));
    await expect(createClient(fetchImpl as typeof fetch).content.translations(contentSummary.id, { page: 1, pageSize: 20 })).resolves.toEqual(pagedContent);
  });

  it("sends bearer authentication and a correlation ID on delivery requests", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse(pagedContent));
    await createClient(fetchImpl as typeof fetch, { apiToken: "cmsify_token" }).content.list();
    expect(requestHeaders(fetchImpl, 0).get("Authorization")).toBe("Bearer cmsify_token");
    expect(requestHeaders(fetchImpl, 0).get("X-Correlation-Id")).toBeTruthy();
  });

  it("tracks ETags from a read and uses If-Match for a later mutation", async () => {
    const fetchImpl = vi.fn().mockResolvedValueOnce(jsonResponse({ ...contentSummary, fields: [] }, { headers: { ETag: 'W/"abc"' } }))
      .mockResolvedValueOnce(jsonResponse({ ...contentSummary, fields: [] }));
    const client = createClient(fetchImpl as typeof fetch);
    await client.content.get(contentSummary.id);
    await client.request(`/api/v1/workspaces/${workspaceId}/content/${contentSummary.id}`, { method: "PUT", body: JSON.stringify({ tags: [], fields: [] }) });
    expect(requestHeaders(fetchImpl, 1).get("If-Match")).toBe('W/"abc"');
  });

  it("returns undefined for a successful empty response instead of attempting JSON parsing", async () => {
    const fetchImpl = vi.fn(async () => new Response(null, { status: 204 }));
    await expect(createClient(fetchImpl as typeof fetch).request<void>("/health/ready")).resolves.toBeUndefined();
  });

  it("preserves RFC 7807 fields and the server correlation ID on API errors", async () => {
    const fetchImpl = vi.fn(async () => jsonResponse({ type: "https://cmsify.dev/errors/not-found", title: "Not found", status: 404,
      detail: "The content item does not exist.", instance: "/content/missing", traceId: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00",
      errors: { id: ["Unknown content item."] }, extensions: { supportCode: "content_not_found" },
    }, { status: 404, headers: { "X-Correlation-Id": "server-correlation" } }));
    await expect(createClient(fetchImpl as typeof fetch).content.get("missing")).rejects.toMatchObject({
      name: "CmsifyApiError", status: 404, traceId: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00",
      correlationId: "server-correlation", problem: { detail: "The content item does not exist.", errors: { id: ["Unknown content item."] } },
    });
  });

  it("retries an idempotent request after an HTTP-date Retry-After delay", async () => {
    const delay = vi.fn(async () => undefined);
    const fetchImpl = vi.fn().mockResolvedValueOnce(jsonResponse({ title: "Rate limit", status: 429 }, { status: 429, headers: { "Retry-After": "Thu, 01 Jan 2026 00:00:02 GMT" } }))
      .mockResolvedValueOnce(jsonResponse({ status: "ready" }));
    const client = createClient(fetchImpl as typeof fetch, { delay, now: () => Date.parse("2026-01-01T00:00:00Z") });
    await expect(client.request<{ status: string }>("/health/ready")).resolves.toEqual({ status: "ready" });
    expect(delay).toHaveBeenCalledWith(2000, expect.any(AbortSignal));
    expect(fetchImpl).toHaveBeenCalledTimes(2);
  });

  it("retries an idempotent request after a delta-seconds Retry-After delay", async () => {
    const delay = vi.fn(async () => undefined);
    const fetchImpl = vi.fn().mockResolvedValueOnce(jsonResponse({ title: "Rate limit", status: 429 }, { status: 429, headers: { "Retry-After": "3" } }))
      .mockResolvedValueOnce(jsonResponse({ status: "ready" }));
    await expect(createClient(fetchImpl as typeof fetch, { delay }).request<{ status: string }>("/health/ready")).resolves.toEqual({ status: "ready" });
    expect(delay).toHaveBeenCalledWith(3000, expect.any(AbortSignal));
  });

  it("returns undefined for a successful declared zero-length JSON body", async () => {
    const fetchImpl = vi.fn(async () => new Response("ignored", { status: 200, headers: { "Content-Length": "0" } }));
    await expect(createClient(fetchImpl as typeof fetch).request<void>("/health/ready")).resolves.toBeUndefined();
  });

  it("does not replay an unsafe request without an idempotency key", async () => {
    const delay = vi.fn(async () => undefined);
    const fetchImpl = vi.fn(async () => jsonResponse({ title: "Unavailable", status: 503 }, { status: 503 }));
    await expect(createClient(fetchImpl as typeof fetch, { delay }).request("/api/v1/commands", { method: "POST", body: "{}" })).rejects.toMatchObject({ status: 503 });
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(delay).not.toHaveBeenCalled();
  });

  it("replays an unsafe request only when it carries an idempotency key", async () => {
    const delay = vi.fn(async () => undefined);
    const fetchImpl = vi.fn().mockResolvedValueOnce(jsonResponse({ title: "Unavailable", status: 503 }, { status: 503 }))
      .mockResolvedValueOnce(jsonResponse({ accepted: true }));
    const client = createClient(fetchImpl as typeof fetch, { delay });
    await expect(client.request<{ accepted: boolean }>("/api/v1/commands", { method: "POST", body: "{}" }, { idempotencyKey: "operation-42" })).resolves.toEqual({ accepted: true });
    expect(requestHeaders(fetchImpl, 0).get("Idempotency-Key")).toBe("operation-42");
    expect(fetchImpl).toHaveBeenCalledTimes(2);
  });

  it("retries an idempotent transport failure without sleeping in real time", async () => {
    const delay = vi.fn(async () => undefined);
    const fetchImpl = vi.fn().mockRejectedValueOnce(new TypeError("network unavailable")).mockResolvedValueOnce(jsonResponse({ status: "ready" }));
    await expect(createClient(fetchImpl as typeof fetch, { delay }).request<{ status: string }>("/health/ready")).resolves.toEqual({ status: "ready" });
    expect(delay).toHaveBeenCalledWith(100, expect.any(AbortSignal));
    expect(fetchImpl).toHaveBeenCalledTimes(2);
  });

  it("uses the caller cancellation signal and never retries an aborted request", async () => {
    const controller = new AbortController();
    const delay = vi.fn(async () => undefined);
    const fetchImpl = vi.fn(async (_url: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () => reject(init.signal?.reason), { once: true });
    }));
    const request = createClient(fetchImpl as typeof fetch, { delay }).request("/health/ready", {}, { signal: controller.signal });
    controller.abort(new DOMException("caller stopped the request", "AbortError"));
    await expect(request).rejects.toMatchObject({ name: "AbortError" });
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(delay).not.toHaveBeenCalled();
  });

  it("enforces an explicit timeout budget with an abort signal", async () => {
    const fetchImpl = vi.fn(async (_url: RequestInfo | URL, init?: RequestInit) => {
      if (!init?.signal) throw new Error("request did not receive a timeout signal");
      if (init.signal.aborted) throw init.signal.reason;
      return new Promise<Response>((_resolve, reject) => init.signal?.addEventListener("abort", () => reject(init.signal?.reason), { once: true }));
    });
    await expect(createClient(fetchImpl as typeof fetch).request("/health/ready", {}, { timeoutMs: 0 })).rejects.toMatchObject({ name: "CmsifyTimeoutError" });
  });

  it("exports the generated fetch client factory for typed raw API access", () => {
    expect(createCmsifyFetchClient("https://cms.example", async () => jsonResponse(pagedContent))).toHaveProperty("GET");
  });
});
