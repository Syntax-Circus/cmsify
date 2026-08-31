import { ETagStore } from "./etag";
import { CmsifyApiError, CmsifyTimeoutError, isProblemDetails } from "./errors";
import { listAll, type PageResult } from "./pagination";
import type { components } from "./generated/schema";
import type { ContentItem, ContentListItem, MediaAsset, PagedResult, Template, TemplateListItem } from "./types";

export type Delay = (milliseconds: number, signal: AbortSignal) => Promise<void>;

export interface CmsifyClientOptions {
  baseUrl: string;
  /** A Cmsify workspace GUID. Slugs are not accepted by the delivery facade. */
  workspaceId: string;
  apiToken?: string;
  fetch?: typeof fetch;
  retry?: boolean;
  /** Total request budget, shared by every attempt. Omit for no SDK timeout. */
  timeoutMs?: number;
  /** Override time only for deterministic retry/timeout integration. */
  now?: () => number;
  /** Override delay only when the host supplies its own scheduler. */
  delay?: Delay;
}

export interface RequestOptions {
  ifMatch?: string;
  idempotencyKey?: string;
  retry?: boolean;
  timeoutMs?: number;
  signal?: AbortSignal;
}

export interface ContentListOptions {
  q?: string;
  templateVersionId?: string;
  templateId?: string;
  status?: components["schemas"]["ContentStatus"];
  localeCode?: string;
  translationGroupId?: string;
  slug?: string;
  tags?: string;
  createdAfter?: string | Date;
  createdBefore?: string | Date;
  publishedAfter?: string | Date;
  publishedBefore?: string | Date;
  asOf?: string | Date;
  sortBy?: string;
  sortDesc?: boolean;
  page?: number;
  pageSize?: number;
}

export interface PageOptions {
  page?: number;
  pageSize?: number;
}

export class CmsifyClient {
  readonly content = {
    list: (options: ContentListOptions = {}) => this.request<PageResult<ContentListItem>>(this.workspacePath("/content", contentQuery(options, true))),
    listAll: (options: ContentListOptions = {}) => listAll((page) => this.content.list({ ...options, page })),
    get: (id: string, options: Pick<ContentListOptions, "asOf"> = {}) => this.request<ContentItem>(this.workspacePath(`/content/${encodeURIComponent(id)}`, detailQuery(options))),
    bySlug: (slug: string, options: Pick<ContentListOptions, "asOf"> = {}) => this.request<ContentItem>(this.workspacePath(`/content/by-slug/${encodeURIComponent(slug)}`, detailQuery(options, false))),
    translations: (id: string, options: PageOptions = {}) => this.request<PagedResult<ContentListItem>>(this.workspacePath(`/content/${encodeURIComponent(id)}/translations`, pageQuery(options))),
  };

  readonly templates = {
    list: (options: PageOptions = {}) => this.request<PagedResult<TemplateListItem>>(this.workspacePath("/templates", pageQuery(options))),
    get: (id: string) => this.request<Template>(this.workspacePath(`/templates/${encodeURIComponent(id)}`)),
  };

  readonly media = {
    list: (options: PageOptions = {}) => this.request<PagedResult<MediaAsset>>(this.workspacePath("/media", pageQuery(options))),
    get: (id: string) => this.request<MediaAsset>(this.workspacePath(`/media/${encodeURIComponent(id)}`)),
    download: (id: string) => this.request<Blob | undefined>(this.workspacePath(`/media/${encodeURIComponent(id)}/file`), {}, {}, "blob"),
  };

  private readonly baseUrl: string;
  private readonly baseOrigin: string;
  private readonly workspaceId: string;
  private readonly token: string | undefined;
  private readonly fetchImpl: typeof fetch;
  private readonly retryByDefault: boolean;
  private readonly timeoutMs: number | undefined;
  private readonly now: () => number;
  private readonly delay: Delay;
  private readonly etags = new ETagStore();

  constructor(options: CmsifyClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/$/, "");
    this.baseOrigin = new URL(this.baseUrl).origin;
    this.workspaceId = options.workspaceId;
    this.token = options.apiToken;
    this.fetchImpl = options.fetch ?? fetch;
    this.retryByDefault = options.retry !== false;
    this.timeoutMs = options.timeoutMs;
    this.now = options.now ?? Date.now;
    this.delay = options.delay ?? delay;
  }

  async request<T>(path: string, init: RequestInit = {}, options: RequestOptions = {}, responseType: "json" | "blob" = "json"): Promise<T> {
    const url = this.requestUrl(path);
    const headers = new Headers(init.headers);
    headers.set("Accept", responseType === "blob" ? "*/*" : "application/json");
    headers.set("X-Correlation-Id", createCorrelationId());
    if (this.token) headers.set("Authorization", `Bearer ${this.token}`);
    if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    if (options.idempotencyKey) headers.set("Idempotency-Key", options.idempotencyKey);

    const method = (init.method ?? "GET").toUpperCase();
    const trackedEtag = options.ifMatch ?? this.etags.get(url);
    if (trackedEtag && isMutation(method)) headers.set("If-Match", trackedEtag);

    const retry = (options.retry ?? this.retryByDefault) && (isIdempotent(method) || Boolean(options.idempotencyKey)) && isReplayableBody(init.body);
    const response = await this.fetchWithRetry(url, { ...init, headers }, retry, options.signal, options.timeoutMs ?? this.timeoutMs);
    this.etags.set(url, response.headers.get("ETag"));

    if (!response.ok) {
      const correlationId = response.headers.get("X-Correlation-Id") ?? headers.get("X-Correlation-Id") ?? undefined;
      const body = await this.safeReadJson(response);
      const problem = isProblemDetails(body) ? { ...body, status: response.status } : { status: response.status, title: response.statusText };
      throw new CmsifyApiError(problem, correlationId);
    }

    if (hasEmptySuccessBody(response)) return undefined as T;
    if (responseType === "blob") return await response.blob() as T;
    return await this.safeReadJson(response) as T;
  }

  private workspacePath(path: string, query?: Record<string, string | number | boolean | undefined>): string {
    if (!isGuid(this.workspaceId)) throw new Error("workspaceId must be a GUID.");
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(query ?? {})) if (value !== undefined) search.set(key, String(value));
    const suffix = search.size > 0 ? `?${search}` : "";
    return `/api/v1/workspaces/${encodeURIComponent(this.workspaceId)}${path}${suffix}`;
  }

  private requestUrl(path: string): string {
    const url = new URL(path, `${this.baseUrl}/`);
    if (url.origin !== this.baseOrigin) throw new Error("CmsifyClient requests must target the configured Cmsify origin.");
    return url.toString();
  }

  private async fetchWithRetry(url: string, init: RequestInit, retry: boolean, callerSignal: AbortSignal | undefined, timeoutMs: number | undefined): Promise<Response> {
    const deadline = timeoutMs === undefined ? undefined : this.now() + Math.max(0, timeoutMs);
    for (let attempt = 1; ; attempt += 1) {
      const controller = new AbortController();
      const timeout = timeoutMs === undefined ? undefined : Math.max(0, (deadline ?? this.now()) - this.now());
      const cleanup = connectAbortSignals(controller, callerSignal, timeout, timeoutMs);
      try {
        const response = await this.fetchImpl(url, { ...init, signal: controller.signal });
        if (!retry || attempt >= 3 || !isRetryableResponse(response)) return response;
        await this.delay(retryDelay(response.headers.get("Retry-After"), attempt, this.now), controller.signal);
      } catch (error) {
        if (controller.signal.aborted) throw controller.signal.reason ?? error;
        if (!retry || attempt >= 3 || !isTransportFault(error)) throw error;
        await this.delay(100 * 2 ** (attempt - 1), controller.signal);
      } finally {
        cleanup();
      }
    }
  }

  private async safeReadJson(response: Response): Promise<unknown> {
    const text = await response.text();
    if (!text) return undefined;
    try { return JSON.parse(text) as unknown; } catch { return { status: response.status, title: response.statusText }; }
  }
}

const contentQuery = (options: ContentListOptions, resolve: boolean): Record<string, string | number | boolean | undefined> => ({
  Q: options.q, TemplateVersionId: options.templateVersionId, TemplateId: options.templateId, Status: options.status,
  LocaleCode: options.localeCode, TranslationGroupId: options.translationGroupId, Slug: options.slug, Tags: options.tags,
  CreatedAfter: asIso(options.createdAfter), CreatedBefore: asIso(options.createdBefore), PublishedAfter: asIso(options.publishedAfter), PublishedBefore: asIso(options.publishedBefore),
  Resolve: resolve, AsOf: asIso(options.asOf), SortBy: options.sortBy, SortDesc: options.sortDesc, page: options.page, pageSize: options.pageSize,
});
const detailQuery = (options: Pick<ContentListOptions, "asOf">, resolve = true): Record<string, string | boolean | undefined> => options.asOf === undefined ? {} : { ...(resolve ? { resolve: true } : {}), asOf: asIso(options.asOf) };
const pageQuery = (options: PageOptions): Record<string, number | undefined> => ({ page: options.page, pageSize: options.pageSize });
const asIso = (value: string | Date | undefined): string | undefined => value instanceof Date ? value.toISOString() : value;
const isGuid = (value: string): boolean => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
const isIdempotent = (method: string): boolean => ["GET", "HEAD", "OPTIONS", "PUT", "DELETE"].includes(method);
const isMutation = (method: string): boolean => !["GET", "HEAD", "OPTIONS"].includes(method);
const isRetryableResponse = (response: Response): boolean => response.status === 429 || response.status >= 500;
const isTransportFault = (error: unknown): boolean => error instanceof TypeError || (error instanceof Error && error.name === "NetworkError");
const isReplayableBody = (body: BodyInit | null | undefined): boolean => !(typeof ReadableStream !== "undefined" && body instanceof ReadableStream);
const hasEmptySuccessBody = (response: Response): boolean => response.status === 204 || response.headers.get("Content-Length") === "0";
const retryDelay = (header: string | null, attempt: number, now: () => number): number => retryAfterDelay(header, now) ?? 100 * 2 ** (attempt - 1);
const retryAfterDelay = (header: string | null, now: () => number): number | undefined => {
  if (header === null) return undefined;
  const seconds = Number(header);
  if (Number.isFinite(seconds) && seconds >= 0) return seconds * 1000;
  const date = Date.parse(header);
  return Number.isNaN(date) ? undefined : Math.max(0, date - now());
};
const connectAbortSignals = (controller: AbortController, callerSignal: AbortSignal | undefined, timeout: number | undefined, timeoutBudget: number | undefined): (() => void) => {
  const abortForCaller = () => controller.abort(callerSignal?.reason);
  if (callerSignal?.aborted) abortForCaller(); else callerSignal?.addEventListener("abort", abortForCaller, { once: true });
  const timer = timeout === undefined ? undefined : setTimeout(() => controller.abort(new CmsifyTimeoutError(timeoutBudget ?? timeout)), timeout);
  return () => { callerSignal?.removeEventListener("abort", abortForCaller); if (timer !== undefined) clearTimeout(timer); };
};
const delay: Delay = (milliseconds, signal) => new Promise((resolve, reject) => {
  if (signal.aborted) { reject(signal.reason); return; }
  const timer = setTimeout(resolve, milliseconds);
  signal.addEventListener("abort", () => { clearTimeout(timer); reject(signal.reason); }, { once: true });
});
const createCorrelationId = (): string => globalThis.crypto?.randomUUID?.() ?? `cmsify-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
