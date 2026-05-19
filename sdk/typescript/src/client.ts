import { ETagStore } from "./etag";
import { CmsifyApiError, isProblemDetails } from "./errors";
import { listAll, type PageResult } from "./pagination";
import type { ContentItem, MediaAsset, PagedResult, Template, Workspace } from "./types";

export interface CmsifyClientOptions {
  baseUrl: string;
  apiToken?: string;
  workspace?: string;
  fetch?: typeof fetch;
  retry?: boolean;
}

export interface RequestOptions {
  ifMatch?: string;
  retry?: boolean;
}

export interface ContentListOptions {
  templateId?: string;
  templateSlug?: string;
  status?: string;
  tags?: string[];
  slug?: string;
  sortBy?: string;
  page?: number;
  pageSize?: number;
}

export class CmsifyClient {
  readonly auth = {
    login: (email: string, password: string) => this.request<{ token: string }>("/api/v1/auth/login", {
      method: "POST",
      body: JSON.stringify({ email, password }),
    }),
    tokenInfo: () => this.request("/api/v1/auth/me"),
  };

  readonly health = {
    live: () => this.request("/health/live", {}, { retry: false }),
    ready: () => this.request("/health/ready", {}, { retry: false }),
  };

  readonly workspaces = {
    list: () => this.request<PagedResult<Workspace>>("/api/v1/workspaces"),
    getBySlug: async (slug: string) => {
      const result = await this.workspaces.list();
      return result.items.find((workspace) => workspace.slug === slug);
    },
  };

  readonly content = {
    list: (options: ContentListOptions = {}) => this.request<PageResult<ContentItem>>(this.workspacePath("/content", options)),
    listAll: (options: ContentListOptions = {}) => listAll((page) => this.content.list({ ...options, page })),
    get: (id: string) => this.request<ContentItem>(this.workspacePath(`/content/${id}`)),
    bySlug: (slug: string) => this.request<ContentItem>(this.workspacePath(`/content/by-slug/${encodeURIComponent(slug)}`)),
    translations: (id: string) => this.request<ContentItem[]>(this.workspacePath(`/content/${id}/translations`)),
  };

  readonly templates = {
    list: () => this.request<PagedResult<Template>>(this.workspacePath("/templates")),
    get: (id: string) => this.request<Template>(this.workspacePath(`/templates/${id}`)),
  };

  readonly media = {
    list: () => this.request<PagedResult<MediaAsset>>(this.workspacePath("/media")),
    get: (id: string) => this.request<MediaAsset>(this.workspacePath(`/media/${id}`)),
    download: (id: string) => this.request<Blob>(this.workspacePath(`/media/${id}/file`), {}, {}, "blob"),
  };

  private readonly baseUrl: string;
  private readonly token: string | undefined;
  private readonly workspace: string | undefined;
  private readonly fetchImpl: typeof fetch;
  private readonly retryByDefault: boolean;
  private readonly etags = new ETagStore();

  constructor(options: CmsifyClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/$/, "");
    this.token = options.apiToken;
    this.workspace = options.workspace;
    this.fetchImpl = options.fetch ?? fetch;
    this.retryByDefault = options.retry !== false;
  }

  async request<T>(path: string, init: RequestInit = {}, options: RequestOptions = {}, responseType: "json" | "blob" = "json"): Promise<T> {
    const url = path.startsWith("http") ? path : `${this.baseUrl}${path}`;
    const headers = new Headers(init.headers);
    headers.set("Accept", responseType === "blob" ? "*/*" : "application/json");
    headers.set("X-Correlation-Id", createCorrelationId());
    if (this.token) {
      headers.set("Authorization", `Bearer ${this.token}`);
    }

    if (init.body && !headers.has("Content-Type")) {
      headers.set("Content-Type", "application/json");
    }

    const trackedEtag = options.ifMatch ?? this.etags.get(url);
    if (trackedEtag && init.method && init.method !== "GET") {
      headers.set("If-Match", trackedEtag);
    }

    const retry = options.retry ?? this.retryByDefault;
    const response = await this.fetchWithRetry(url, { ...init, headers }, retry);
    this.etags.set(url, response.headers.get("ETag"));

    if (!response.ok) {
      const correlationId = response.headers.get("X-Correlation-Id") ?? headers.get("X-Correlation-Id") ?? undefined;
      const body = await this.safeReadJson(response);
      throw new CmsifyApiError(isProblemDetails(body) ? body : { status: response.status, title: response.statusText }, correlationId);
    }

    if (responseType === "blob") {
      return await response.blob() as T;
    }

    return await response.json() as T;
  }

  private workspacePath(path: string, query?: ContentListOptions): string {
    if (!this.workspace) {
      throw new Error("A workspace slug or ID is required for this operation.");
    }

    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(query ?? {})) {
      if (value === undefined || value === null) {
        continue;
      }

      search.set(key, Array.isArray(value) ? value.join(",") : String(value));
    }

    const suffix = search.size > 0 ? `?${search}` : "";
    return `/api/v1/workspaces/${encodeURIComponent(this.workspace)}${path}${suffix}`;
  }

  private async fetchWithRetry(url: string, init: RequestInit, retry: boolean): Promise<Response> {
    const maxAttempts = retry ? 3 : 1;
    for (let attempt = 1; ; attempt += 1) {
      const response = await this.fetchImpl(url, init);
      if (attempt >= maxAttempts || (response.status !== 429 && response.status < 500)) {
        return response;
      }

      const retryAfter = Number(response.headers.get("Retry-After"));
      const delayMs = Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : 100 * 2 ** (attempt - 1);
      await new Promise((resolve) => setTimeout(resolve, delayMs));
    }
  }

  private async safeReadJson(response: Response): Promise<unknown> {
    try {
      return await response.json();
    } catch {
      return { status: response.status, title: response.statusText };
    }
  }

}

const createCorrelationId = (): string =>
  globalThis.crypto?.randomUUID?.() ?? `cmsify-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
