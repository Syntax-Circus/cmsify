import https from "node:https";
import { Readable } from "node:stream";
import { setTimeout as delay } from "node:timers/promises";

const DEFAULT_TIMEOUT_MS = 5_000;
const DEFAULT_MAX_BYTES = 1024 * 1024;
const RUN_ID = /^cmsify-smoke-[a-z0-9-]{8,32}$/;

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

export async function retryBounded(operation, {
  maxAttempts,
  delayMs,
  sleep = (milliseconds, abortSignal) => delay(milliseconds, undefined, { signal: abortSignal }),
  signal,
} = {}) {
  assert(typeof operation === "function", "A retry operation is required.");
  assert(Number.isSafeInteger(maxAttempts) && maxAttempts >= 1 && maxAttempts <= 300, "Retry maxAttempts must be between 1 and 300.");
  assert(Number.isSafeInteger(delayMs) && delayMs >= 0 && delayMs <= 60_000, "Retry delayMs must be between 0 and 60000.");
  assert(typeof sleep === "function", "Retry sleep must be a function.");
  assert(signal === undefined || signal instanceof AbortSignal, "Retry signal must be an AbortSignal.");

  let lastError;
  for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
    if (signal?.aborted) throw signal.reason ?? new Error("Retry operation was aborted.");
    try {
      return await operation(attempt);
    } catch (error) {
      lastError = error;
      if (signal?.aborted) throw signal.reason ?? new Error("Retry operation was aborted.");
      if (attempt < maxAttempts) await sleep(delayMs, signal);
    }
  }
  throw lastError ?? new Error("Retry operation failed.");
}

function expectedStatuses(value) {
  const values = value instanceof Set ? [...value] : Array.isArray(value) ? value : [];
  assert(values.length > 0 && values.every((status) => Number.isInteger(status) && status >= 100 && status <= 599), "HTTP expectedStatuses must contain valid statuses.");
  return new Set(values);
}

async function boundedBytes(response, maximum) {
  const declared = Number(response.headers.get("content-length"));
  if (Number.isFinite(declared) && declared > maximum) {
    await response.body?.cancel().catch(() => {});
    throw new Error(`HTTP response exceeded ${maximum} bytes.`);
  }
  if (!response.body) return new Uint8Array();
  const reader = response.body.getReader();
  const chunks = [];
  let total = 0;
  try {
    while (total <= maximum) {
      const { done, value } = await reader.read();
      if (done) break;
      const retained = value.subarray(0, maximum + 1 - total);
      chunks.push(retained);
      total += retained.byteLength;
      if (total > maximum) {
        await reader.cancel().catch(() => {});
        throw new Error(`HTTP response exceeded ${maximum} bytes.`);
      }
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return bytes;
}

export async function requestHttp({
  url,
  method = "GET",
  headers = {},
  body,
  expectedStatuses = [200],
  timeoutMs = DEFAULT_TIMEOUT_MS,
  maxBytes = DEFAULT_MAX_BYTES,
  fetchImpl = globalThis.fetch,
  signal,
  tlsCa,
} = {}) {
  assert(typeof url === "string" && /^https?:\/\//.test(url), "HTTP URL must be absolute.");
  assert(typeof method === "string" && /^[A-Z]+$/i.test(method), "HTTP method is invalid.");
  assert(headers && typeof headers === "object" && !Array.isArray(headers), "HTTP headers must be an object.");
  assert(Number.isSafeInteger(timeoutMs) && timeoutMs >= 1 && timeoutMs <= 120_000, "HTTP timeout is invalid.");
  assert(Number.isSafeInteger(maxBytes) && maxBytes >= 1 && maxBytes <= 64 * 1024 * 1024, "HTTP response limit is invalid.");
  assert(typeof fetchImpl === "function", "HTTP fetch implementation is required.");
  assert(tlsCa === undefined || typeof tlsCa === "string" || Buffer.isBuffer(tlsCa), "HTTP TLS CA must be PEM text or bytes.");
  const statuses = expectedStatuses instanceof Set ? expectedStatuses : expectedStatuses;
  const allowed = expectedStatusesFn(statuses);
  const timeoutSignal = AbortSignal.timeout(timeoutMs);
  const combined = signal === undefined ? timeoutSignal : AbortSignal.any([signal, timeoutSignal]);
  let response;
  try {
    response = tlsCa !== undefined && new URL(url).protocol === "https:"
      ? await trustedHttpsRequest(url, { method, headers, body, signal: combined, ca: tlsCa })
      : await fetchImpl(url, { method, headers, body, redirect: "manual", signal: combined });
  } catch {
    throw new Error(`HTTP ${method.toUpperCase()} ${new URL(url).pathname} failed with a transport error.`);
  }
  const bytes = await boundedBytes(response, maxBytes);
  if (!allowed.has(response.status)) {
    throw new Error(`HTTP ${method.toUpperCase()} ${new URL(url).pathname} returned unexpected status ${response.status}.`);
  }
  const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  return Object.freeze({
    status: response.status,
    headers: response.headers,
    bytes,
    text,
    json() {
      try {
        return JSON.parse(text);
      } catch {
        throw new Error(`HTTP ${method.toUpperCase()} ${new URL(url).pathname} did not return valid JSON.`);
      }
    },
  });
}

function trustedHttpsRequest(url, { method, headers, body, signal, ca }) {
  assert(body === undefined || typeof body === "string" || Buffer.isBuffer(body) || body instanceof Uint8Array,
    "Trusted HTTPS requests support only byte and string bodies.");
  return new Promise((resolve, reject) => {
    const request = https.request(url, { method, headers, ca, rejectUnauthorized: true, signal }, (incoming) => {
      const values = new Map();
      for (const [name, value] of Object.entries(incoming.headers)) {
        if (value !== undefined) values.set(name.toLowerCase(), Array.isArray(value) ? value.join(", ") : value);
      }
      const setCookies = [];
      for (let index = 0; index < incoming.rawHeaders.length; index += 2) {
        if (incoming.rawHeaders[index].toLowerCase() === "set-cookie") setCookies.push(incoming.rawHeaders[index + 1]);
      }
      resolve({
        status: incoming.statusCode ?? 0,
        headers: { get: (name) => values.get(name.toLowerCase()) ?? null, getSetCookie: () => [...setCookies] },
        body: Readable.toWeb(incoming),
      });
    });
    request.once("error", reject);
    request.end(body);
  });
}

function expectedStatusesFn(value) {
  return expectedStatuses(value);
}

export class CookieJar {
  #cookies = new Map();
  #now;
  #sequence = 0;

  constructor({ now = () => new Date() } = {}) {
    assert(typeof now === "function", "CookieJar clock must be a function.");
    this.#now = now;
  }

  absorb(url, headers) {
    const origin = new URL(url);
    assert(["http:", "https:"].includes(origin.protocol), "CookieJar URL must use HTTP or HTTPS.");
    assert(headers && typeof headers.getSetCookie === "function", "CookieJar requires Fetch Headers with getSetCookie().");
    for (const line of headers.getSetCookie()) {
      const segments = line.split(";").map((value) => value.trim());
      const pair = segments.shift() ?? "";
      const separator = pair.indexOf("=");
      if (separator <= 0) continue;
      const name = pair.slice(0, separator).trim();
      const value = pair.slice(separator + 1).trim();
      if (!/^[!#$%&'*+.^_`|~0-9A-Za-z-]+$/.test(name)) continue;
      const attributes = new Map();
      for (const segment of segments) {
        const split = segment.indexOf("=");
        const attribute = (split < 0 ? segment : segment.slice(0, split)).trim().toLowerCase();
        const attributeValue = split < 0 ? "" : segment.slice(split + 1).trim();
        if (!attributes.has(attribute)) attributes.set(attribute, attributeValue);
      }
      const secure = attributes.has("secure");
      if (secure && origin.protocol !== "https:") continue;
      const requestedDomain = attributes.get("domain")?.replace(/^\./, "").toLowerCase();
      const hostname = origin.hostname.toLowerCase();
      if (requestedDomain && !domainMatches(hostname, requestedDomain)) continue;
      const domain = requestedDomain ?? hostname;
      const hostOnly = requestedDomain === undefined;
      const requestedPath = attributes.get("path");
      const path = requestedPath?.startsWith("/") ? requestedPath : defaultCookiePath(origin.pathname);
      const sameSiteValue = attributes.get("samesite")?.toLowerCase();
      const sameSite = ["strict", "lax", "none"].includes(sameSiteValue) ? sameSiteValue : "lax";
      if (sameSite === "none" && !secure) continue;
      const now = this.#now().getTime();
      const maximumAge = attributes.has("max-age") ? Number(attributes.get("max-age")) : undefined;
      const expiresAt = Number.isFinite(maximumAge)
        ? now + Math.max(0, maximumAge) * 1_000
        : attributes.has("expires") ? Date.parse(attributes.get("expires")) : null;
      const key = `${name}\0${domain}\0${path}`;
      if (value.length === 0 || (Number.isFinite(expiresAt) && expiresAt <= now)) {
        this.#cookies.delete(key);
        continue;
      }
      this.#cookies.set(key, { name, value, domain, hostOnly, path, secure, sameSite, expiresAt, sequence: this.#sequence++ });
    }
  }

  header(url, { method = "GET", topLevelNavigation = false, initiatorUrl = url } = {}) {
    const target = new URL(url);
    const initiator = new URL(initiatorUrl);
    const now = this.#now().getTime();
    const sameSite = target.hostname.toLowerCase() === initiator.hostname.toLowerCase();
    const values = [];
    for (const [key, cookie] of this.#cookies) {
      if (Number.isFinite(cookie.expiresAt) && cookie.expiresAt <= now) {
        this.#cookies.delete(key);
        continue;
      }
      const targetHost = target.hostname.toLowerCase();
      if (cookie.hostOnly ? targetHost !== cookie.domain : !domainMatches(targetHost, cookie.domain)) continue;
      if (!pathMatches(target.pathname || "/", cookie.path)) continue;
      if (cookie.secure && target.protocol !== "https:") continue;
      if (cookie.sameSite === "strict" && !sameSite) continue;
      if (cookie.sameSite === "lax" && !sameSite && !(topLevelNavigation && ["GET", "HEAD"].includes(method.toUpperCase()))) continue;
      values.push(cookie);
    }
    return values.sort((left, right) => right.path.length - left.path.length || left.sequence - right.sequence)
      .map(({ name, value }) => `${name}=${value}`).join("; ");
  }
}

function domainMatches(hostname, domain) {
  return hostname === domain || hostname.endsWith(`.${domain}`);
}

function defaultCookiePath(pathname) {
  if (!pathname?.startsWith("/") || pathname === "/") return "/";
  const lastSlash = pathname.lastIndexOf("/");
  return lastSlash <= 0 ? "/" : pathname.slice(0, lastSlash);
}

function pathMatches(requestPath, cookiePath) {
  return requestPath === cookiePath
    || (requestPath.startsWith(cookiePath) && (cookiePath.endsWith("/") || requestPath[cookiePath.length] === "/"));
}

export async function requestWithCookies(jar, input) {
  assert(jar instanceof CookieJar, "A CookieJar is required.");
  const cookie = jar.header(input.url, input.cookieContext);
  const response = await requestHttp({
    ...input,
    headers: { ...input.headers, ...(cookie ? { cookie } : {}) },
  });
  jar.absorb(input.url, response.headers);
  return response;
}

function joinUrl(base, path) {
  return new URL(path, `${base.replace(/\/$/, "")}/`).toString();
}

function bearer(token) {
  assert(typeof token === "string" && token.length > 0, "A bearer token is required for this release smoke request.");
  return { authorization: `Bearer ${token}` };
}

function requiredHeader(response, name) {
  const value = response.headers.get(name);
  assert(typeof value === "string" && value.length > 0, `Release smoke response is missing ${name}.`);
  return value;
}

function requireObject(value, label) {
  assert(value && typeof value === "object" && !Array.isArray(value), `${label} response is invalid.`);
  return value;
}

function requireGuid(value, label) {
  assert(typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value), `${label} is not a GUID.`);
  return value;
}

function absorbCookies(jar, url, response) {
  if (typeof response.headers.getSetCookie === "function") jar.absorb(url, response.headers);
}

function antiforgeryToken(html) {
  const patterns = [
    /name=["']__RequestVerificationToken["'][^>]*value=["']([^"']+)["']/i,
    /value=["']([^"']+)["'][^>]*name=["']__RequestVerificationToken["']/i,
  ];
  const match = patterns.map((pattern) => html.match(pattern)).find(Boolean);
  assert(match, "Admin login page did not contain an antiforgery token.");
  return match[1];
}

export function createReleaseHttpAdapter({
  request = requestHttp,
  sleep = (milliseconds, abortSignal) => delay(milliseconds, undefined, { signal: abortSignal }),
  now = () => new Date(),
  signal,
} = {}) {
  assert(typeof request === "function", "Release HTTP adapter requires a request function.");
  assert(typeof sleep === "function", "Release HTTP adapter requires a sleep function.");
  assert(typeof now === "function", "Release HTTP adapter requires a clock.");

  const call = (base, path, options = {}) => request({
    url: joinUrl(base, path),
    method: options.method ?? "GET",
    headers: options.headers ?? {},
    ...(options.body === undefined ? {} : { body: options.body }),
    expectedStatuses: options.expectedStatuses ?? [200],
    timeoutMs: options.timeoutMs ?? DEFAULT_TIMEOUT_MS,
    maxBytes: options.maxBytes ?? DEFAULT_MAX_BYTES,
    ...(options.signal ?? signal ? { signal: options.signal ?? signal } : {}),
    ...(options.tlsCa === undefined ? {} : { tlsCa: options.tlsCa }),
  });
  const json = (base, path, options = {}) => call(base, path, {
    ...options,
    headers: { "content-type": "application/json", ...(options.headers ?? {}) },
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });
  const withJar = async (jar, base, path, options = {}) => {
    const url = joinUrl(base, path);
    const cookie = jar.header(url, options.cookieContext);
    const response = await call(base, path, {
      ...options,
      headers: { ...(options.headers ?? {}), ...(cookie ? { cookie } : {}) },
    });
    absorbCookies(jar, url, response);
    return response;
  };
  const proveProtectedAdminApi = async (context, jar) => {
    assert(RUN_ID.test(context.runId), "Protected Admin proof requires a validated release smoke run ID.");
    const proofResponse = await withJar(
      jar,
      context.runtime.adminBase,
      `/admin-auth/release-smoke/${context.runId}/protected-workspaces`,
      { maxBytes: 1024 * 1024, tlsCa: context.runtime.tlsCa },
    );
    const proof = requireObject(proofResponse.json(), "Protected Admin API proof");
    assert(proof.proof === "cmsify.release-smoke.admin-api.v1" && Array.isArray(proof.workspaces), "Protected Admin route returned an invalid proof contract.");
    const workspace = proof.workspaces.find((item) => item?.id === context.runtime.workspaceId);
    assert(workspace?.name === "Release Smoke Workspace" && workspace.slug === "release-smoke", "Protected Admin route did not return the API-backed release workspace.");
  };

  async function waitForApi(context) {
    let attempts = 0;
    await retryBounded(async (attempt) => {
      attempts = attempt;
      const live = await call(context.runtime.apiBase, "/health/live", { timeoutMs: 5_000 });
      const ready = await call(context.runtime.apiBase, "/health/ready", { timeoutMs: 5_000 });
      assert(live.status === 200 && ready.status === 200, "API health endpoints are not ready.");
      return true;
    }, { maxAttempts: context.maxAttempts, delayMs: 2_000, sleep, signal: context.signal ?? signal });
    return { attempts };
  }

  async function waitForAdmin(context) {
    let attempts = 0;
    await retryBounded(async (attempt) => {
      attempts = attempt;
      const page = await call(context.runtime.adminBase, "/", { timeoutMs: 5_000, maxBytes: 2 * 1024 * 1024, tlsCa: context.runtime.tlsCa });
      assert(page.text.includes("<title>Cmsify Admin</title>"), "Admin root did not return Cmsify-specific content.");
      const asset = page.text.match(/(?:src|href)=["']([^"']*(?:blazor\.web\.js|cmsify[^"']*\.(?:css|js)))["']/i)?.[1]
        ?? "/_framework/blazor.web.js";
      const staticResponse = await call(context.runtime.adminBase, asset, { timeoutMs: 5_000, maxBytes: 8 * 1024 * 1024, tlsCa: context.runtime.tlsCa });
      assert(staticResponse.bytes.byteLength > 0, "Admin static asset was empty.");
      return true;
    }, { maxAttempts: context.maxAttempts, delayMs: 2_000, sleep, signal: context.signal ?? signal });
    return { attempts };
  }

  async function localLogin(context) {
    let password = context.secrets.seedPassword;
    let login = await json(context.runtime.apiBase, "/api/v1/auth/login", {
      method: "POST",
      body: { email: "admin@release-smoke.invalid", password },
    });
    let loginBody = requireObject(login.json(), "Local login");
    assert(typeof loginBody.token === "string" && loginBody.token.length >= 32, "Local login did not return an opaque token.");
    if (loginBody.mustChangePassword === true) {
      assert(typeof context.secrets.changedAdminPassword === "string" && context.secrets.changedAdminPassword.length >= 16, "Forced password change requires a run-scoped replacement password.");
      await json(context.runtime.apiBase, "/api/v1/auth/change-password", {
        method: "POST",
        headers: bearer(loginBody.token),
        body: { currentPassword: password, newPassword: context.secrets.changedAdminPassword },
        expectedStatuses: [204],
      });
      password = context.secrets.changedAdminPassword;
      login = await json(context.runtime.apiBase, "/api/v1/auth/login", {
        method: "POST",
        body: { email: "admin@release-smoke.invalid", password },
      });
      loginBody = requireObject(login.json(), "Post-change local login");
      assert(loginBody.mustChangePassword === false && typeof loginBody.token === "string" && loginBody.token.length >= 32, "Forced password change did not establish an unrestricted API session.");
    }
    context.secrets.localToken = loginBody.token;

    const workspacePage = await json(context.runtime.apiBase, "/api/v1/workspaces?page=1&pageSize=20", {
      headers: bearer(loginBody.token),
    });
    const workspace = requireObject(workspacePage.json(), "Workspace list").items?.find((item) => item.slug === "release-smoke")
      ?? workspacePage.json().items?.[0];
    requireObject(workspace, "Seed workspace");
    requireGuid(workspace.id, "Seed workspace ID");

    const jar = new CookieJar();
    const loginPage = await withJar(jar, context.runtime.adminBase, "/login", { tlsCa: context.runtime.tlsCa });
    const form = new URLSearchParams({
      __RequestVerificationToken: antiforgeryToken(loginPage.text),
      email: "admin@release-smoke.invalid",
      password,
      returnUrl: "/workspaces",
    });
    const adminLogin = await withJar(jar, context.runtime.adminBase, "/admin-auth/login", {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: form.toString(),
      expectedStatuses: [302],
      tlsCa: context.runtime.tlsCa,
    });
    assert(requiredHeader(adminLogin, "location") === "/workspaces", "Admin local login did not establish unrestricted access.");
    assert(jar.header(joinUrl(context.runtime.adminBase, "/workspaces")).includes("cmsify.admin.auth="), "Admin local login did not establish a session cookie.");
    await proveProtectedAdminApi({ ...context, runtime: { ...context.runtime, workspaceId: workspace.id } }, jar);
    return { localToken: loginBody.token, workspaceId: workspace.id };
  }

  async function apiClientAuth(context) {
    const response = await json(context.runtime.apiBase, "/api/v1/clients", {
      method: "POST",
      headers: bearer(context.runtime.localToken),
      body: {
        name: "Release smoke workspace client",
        description: "Run-scoped release certification client",
        role: "Editor",
        workspaceId: context.runtime.workspaceId,
        expiresAt: null,
      },
      expectedStatuses: [201],
    });
    const created = requireObject(response.json(), "API client creation");
    assert(typeof created.token === "string" && created.token.startsWith("cmsify_"), "API client creation did not return a Cmsify token.");
    context.secrets.apiClientToken = created.token;
    const me = await json(context.runtime.apiBase, "/api/v1/auth/me", { headers: bearer(created.token) });
    const actor = requireObject(me.json(), "API client actor");
    assert(actor.workspaceId === context.runtime.workspaceId && actor.role === "Editor", "Workspace API client was not scoped as expected.");
    return { apiClientToken: created.token, apiClientId: created.client?.id };
  }

  async function templateContentCrud(context) {
    const token = context.runtime.localToken;
    const workspace = context.runtime.workspaceId;
    const templatesPath = `/api/v1/workspaces/${workspace}/templates`;
    const createdTemplateResponse = await json(context.runtime.apiBase, templatesPath, {
      method: "POST", headers: bearer(token), body: { name: "Release Smoke CRUD", slug: "release-smoke-crud", description: "Disposable ETag canary" }, expectedStatuses: [201],
    });
    const createdTemplate = requireObject(createdTemplateResponse.json(), "Template creation");
    const templateId = requireGuid(createdTemplate.id, "Template ID");
    const versionId = requireGuid(createdTemplate.currentVersion?.id, "Template version ID");
    assert(createdTemplate.currentVersion?.versionNumber === 1, "Template did not start at version 1.");
    const templatePath = `${templatesPath}/${templateId}`;
    const readTemplate = await json(context.runtime.apiBase, templatePath, { headers: bearer(token) });
    const templateEtag = requiredHeader(readTemplate, "etag");
    const updatedTemplate = await json(context.runtime.apiBase, templatePath, {
      method: "PUT", headers: { ...bearer(token), "if-match": templateEtag }, body: { name: "Release Smoke CRUD Updated", description: "Conditional update passed" },
    });
    const updatedTemplateEtag = requiredHeader(updatedTemplate, "etag");
    await json(context.runtime.apiBase, `${templatePath}/versions/1/publish`, { method: "PUT", headers: bearer(token) });

    const contentPath = `/api/v1/workspaces/${workspace}/content`;
    const createdContentResponse = await json(context.runtime.apiBase, contentPath, {
      method: "POST", headers: bearer(token), body: { templateVersionId: versionId, slug: "release-smoke-crud", localeCode: null, translationGroupId: null, tags: ["release-smoke"], fields: [] }, expectedStatuses: [201],
    });
    const createdContent = requireObject(createdContentResponse.json(), "Content creation");
    const contentId = requireGuid(createdContent.id, "Content ID");
    const itemPath = `${contentPath}/${contentId}`;
    const readContent = await json(context.runtime.apiBase, itemPath, { headers: bearer(token) });
    const contentEtag = requiredHeader(readContent, "etag");
    const updatedContent = await json(context.runtime.apiBase, itemPath, {
      method: "PUT", headers: { ...bearer(token), "if-match": contentEtag },
      body: { slug: "release-smoke-crud-updated", localeCode: null, translationGroupId: null, publishAt: null, tags: ["release-smoke", "updated"], fields: [] },
    });
    const updatedContentEtag = requiredHeader(updatedContent, "etag");
    await call(context.runtime.apiBase, itemPath, { method: "DELETE", headers: { ...bearer(token), "if-match": updatedContentEtag }, expectedStatuses: [204] });
    await call(context.runtime.apiBase, templatePath, { method: "DELETE", headers: { ...bearer(token), "if-match": updatedTemplateEtag }, expectedStatuses: [204] });
    return { deletedTemplateId: templateId, deletedContentId: contentId };
  }

  async function mediaRoundTrip(context) {
    const bytes = Buffer.from("cmsify-release-smoke-media-v1\n", "utf8");
    const form = new FormData();
    form.append("file", new Blob([bytes], { type: "text/plain" }), "release-smoke.txt");
    form.append("altText", "Release smoke media");
    const basePath = `/api/v1/workspaces/${context.runtime.workspaceId}/media`;
    const uploaded = await call(context.runtime.apiBase, basePath, {
      method: "POST", headers: bearer(context.runtime.localToken), body: form, expectedStatuses: [201], maxBytes: 2 * 1024 * 1024,
    });
    const asset = requireObject(uploaded.json(), "Media upload");
    const mediaId = requireGuid(asset.id, "Media ID");
    const downloaded = await call(context.runtime.apiBase, `${basePath}/${mediaId}/file`, {
      headers: bearer(context.runtime.localToken), maxBytes: 2 * 1024 * 1024,
    });
    assert(Buffer.from(downloaded.bytes).equals(bytes), "Downloaded media bytes did not match the upload.");
    return { mediaId, mediaBytes: bytes.toString("base64") };
  }

  async function oidcFlow(context) {
    await call(context.runtime.oidcBase, `/configure?workspaceId=${encodeURIComponent(context.runtime.workspaceId)}`, { tlsCa: context.runtime.tlsCa });
    const tokenResponse = await json(context.runtime.oidcBase, "/test-token", { tlsCa: context.runtime.tlsCa });
    const oidcToken = requireObject(tokenResponse.json(), "OIDC token").access_token;
    assert(typeof oidcToken === "string" && oidcToken.split(".").length === 3, "OIDC issuer did not return a JWT.");
    const me = await json(context.runtime.apiBase, "/api/v1/auth/me", { headers: bearer(oidcToken) });
    const actor = requireObject(me.json(), "OIDC API actor");
    assert(actor.role === "Admin" && actor.workspaceId === context.runtime.workspaceId, "OIDC API claims were not mapped.");

    const jar = new CookieJar();
    const challenge = await withJar(jar, context.runtime.adminBase, "/admin-auth/oidc-login?returnUrl=%2Fworkspaces", { expectedStatuses: [302], tlsCa: context.runtime.tlsCa });
    const internalAuthorize = new URL(requiredHeader(challenge, "location"));
    const authorize = await withJar(jar, context.runtime.oidcBase, `${internalAuthorize.pathname}${internalAuthorize.search}`, { expectedStatuses: [302], tlsCa: context.runtime.tlsCa });
    const callback = new URL(requiredHeader(authorize, "location"));
    const signedIn = await withJar(jar, `${callback.protocol}//${callback.host}`, `${callback.pathname}${callback.search}`, {
      expectedStatuses: [302], tlsCa: context.runtime.tlsCa,
      cookieContext: { initiatorUrl: context.runtime.oidcBase, topLevelNavigation: true, method: "GET" },
    });
    assert(requiredHeader(signedIn, "location") === "/workspaces", "OIDC Admin callback did not retain the return URL.");
    await proveProtectedAdminApi(context, jar);
    return { status: "passed" };
  }

  async function webhookDelivery(context) {
    const workspace = context.runtime.workspaceId;
    const token = context.runtime.localToken;
    const created = await json(context.runtime.apiBase, `/api/v1/workspaces/${workspace}/webhooks`, {
      method: "POST", headers: bearer(token), expectedStatuses: [201],
      body: { name: "Release smoke receiver", url: `http://webhook.${context.runId}.release-smoke.invalid:8080/hook`, secret: null, events: ["workspace.updated"] },
    });
    const endpoint = requireObject(created.json(), "Webhook creation").endpoint;
    requireGuid(endpoint?.id, "Webhook endpoint ID");
    const workspacePath = `/api/v1/workspaces/${workspace}`;
    const current = await json(context.runtime.apiBase, workspacePath, { headers: bearer(token) });
    const workspaceBody = requireObject(current.json(), "Workspace read");
    await json(context.runtime.apiBase, workspacePath, {
      method: "PUT", headers: { ...bearer(token), "if-match": requiredHeader(current, "etag") },
      body: { name: "Release Smoke Workspace", slug: workspaceBody.slug, description: "Webhook delivery certified" },
    });
    let delivered;
    await retryBounded(async () => {
      const status = requireObject((await json(context.runtime.webhookBase, "/status")).json(), "Webhook receiver status");
      assert(Number.isSafeInteger(status.count) && status.count >= 1 && Array.isArray(status.eventTypes)
        && status.eventTypes.includes("workspace.updated"), "Webhook receiver has not observed the exact workspace.updated delivery.");
      delivered = status.count;
    }, { maxAttempts: 30, delayMs: 1_000, sleep, signal: context.signal ?? signal });
    return { webhookEndpointId: endpoint.id, deliveries: delivered };
  }

  async function scheduledPublication(context) {
    const workspace = context.runtime.workspaceId;
    const token = context.runtime.localToken;
    const templates = `/api/v1/workspaces/${workspace}/templates`;
    const template = requireObject((await json(context.runtime.apiBase, templates, {
      method: "POST", headers: bearer(token), expectedStatuses: [201],
      body: { name: "Release Smoke Persistent", slug: "release-smoke-persistent", description: "Backup and restore canary" },
    })).json(), "Persistent template creation");
    await json(context.runtime.apiBase, `${templates}/${template.id}/versions/1/publish`, { method: "PUT", headers: bearer(token) });
    const contentBase = `/api/v1/workspaces/${workspace}/content`;
    const content = requireObject((await json(context.runtime.apiBase, contentBase, {
      method: "POST", headers: bearer(token), expectedStatuses: [201],
      body: { templateVersionId: template.currentVersion.id, slug: "release-smoke-persisted", localeCode: null, translationGroupId: null, tags: ["scheduled", "persisted"], fields: [] },
    })).json(), "Scheduled content creation");
    await json(context.runtime.apiBase, `${contentBase}/${content.id}/submit`, { method: "POST", headers: bearer(token), body: null });
    await json(context.runtime.apiBase, `${contentBase}/${content.id}/approve`, { method: "POST", headers: bearer(token), body: null });
    const publishAt = new Date(now().getTime() + 1_500).toISOString();
    await json(context.runtime.apiBase, `${contentBase}/${content.id}/publish`, {
      method: "POST", headers: bearer(token), body: { publishAt, effectiveStartAt: null, effectiveEndAt: null },
    });
    await retryBounded(async () => {
      const value = requireObject((await json(context.runtime.apiBase, `${contentBase}/${content.id}`, { headers: bearer(token) })).json(), "Scheduled content status");
      assert(value.status === "Published", "Scheduled content is not published yet.");
    }, { maxAttempts: 30, delayMs: 1_000, sleep, signal: context.signal ?? signal });
    return { persistentTemplateId: template.id, persistentContentId: content.id, persistentSlug: "release-smoke-persisted" };
  }

  async function verifyPersistence(context) {
    await waitForApi({ ...context, maxAttempts: 30 });
    await waitForAdmin({ ...context, maxAttempts: 30 });
    const login = requireObject((await json(context.runtime.apiBase, "/api/v1/auth/login", {
      method: "POST", body: {
        email: "admin@release-smoke.invalid",
        password: context.secrets.changedAdminPassword ?? context.secrets.seedPassword,
      },
    })).json(), "Persistence login");
    const content = requireObject((await json(context.runtime.apiBase, `/api/v1/workspaces/${context.runtime.workspaceId}/content/by-slug/${context.artifacts.persistentSlug}`, {
      headers: bearer(login.token),
    })).json(), "Persisted content");
    assert(content.id === context.artifacts.persistentContentId && content.status === "Published", "Published content did not persist.");
    const media = await call(context.runtime.apiBase, `/api/v1/workspaces/${context.runtime.workspaceId}/media/${context.artifacts.mediaId}/file`, {
      headers: bearer(login.token), maxBytes: 2 * 1024 * 1024,
    });
    assert(Buffer.from(media.bytes).toString("base64") === context.artifacts.mediaBytes, "Media did not persist.");
    return { persisted: true };
  }

  return Object.freeze({
    waitForApi,
    waitForAdmin,
    localLogin,
    apiClientAuth,
    templateContentCrud,
    mediaRoundTrip,
    oidcFlow,
    webhookDelivery,
    scheduledPublication,
    verifyPersistence,
    verifyRestoredState: verifyPersistence,
  });
}
