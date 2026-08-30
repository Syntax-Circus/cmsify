const DEFAULT_TIMEOUT_MS = 5_000;
const DEFAULT_MAX_BYTES = 1024 * 1024;

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

export async function retryBounded(operation, {
  maxAttempts,
  delayMs,
  sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)),
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
      if (attempt < maxAttempts) await sleep(delayMs);
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
  if (Number.isFinite(declared) && declared > maximum) throw new Error(`HTTP response exceeded ${maximum} bytes.`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength > maximum) throw new Error(`HTTP response exceeded ${maximum} bytes.`);
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
} = {}) {
  assert(typeof url === "string" && /^https?:\/\//.test(url), "HTTP URL must be absolute.");
  assert(typeof method === "string" && /^[A-Z]+$/i.test(method), "HTTP method is invalid.");
  assert(headers && typeof headers === "object" && !Array.isArray(headers), "HTTP headers must be an object.");
  assert(Number.isSafeInteger(timeoutMs) && timeoutMs >= 1 && timeoutMs <= 120_000, "HTTP timeout is invalid.");
  assert(Number.isSafeInteger(maxBytes) && maxBytes >= 1 && maxBytes <= 64 * 1024 * 1024, "HTTP response limit is invalid.");
  assert(typeof fetchImpl === "function", "HTTP fetch implementation is required.");
  const statuses = expectedStatuses instanceof Set ? expectedStatuses : expectedStatuses;
  const allowed = expectedStatusesFn(statuses);
  const timeoutSignal = AbortSignal.timeout(timeoutMs);
  const combined = signal === undefined ? timeoutSignal : AbortSignal.any([signal, timeoutSignal]);
  let response;
  try {
    response = await fetchImpl(url, { method, headers, body, redirect: "manual", signal: combined });
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

function expectedStatusesFn(value) {
  return expectedStatuses(value);
}

export class CookieJar {
  #cookies = new Map();

  absorb(headers) {
    assert(headers && typeof headers.getSetCookie === "function", "CookieJar requires Fetch Headers with getSetCookie().");
    for (const line of headers.getSetCookie()) {
      const pair = line.split(";", 1)[0];
      const separator = pair.indexOf("=");
      if (separator <= 0) continue;
      const name = pair.slice(0, separator).trim();
      const value = pair.slice(separator + 1).trim();
      if (value.length === 0 || /expires=Thu, 01 Jan 1970/i.test(line)) this.#cookies.delete(name);
      else this.#cookies.set(name, value);
    }
  }

  header() {
    return [...this.#cookies].map(([name, value]) => `${name}=${value}`).join("; ");
  }
}

export async function requestWithCookies(jar, input) {
  assert(jar instanceof CookieJar, "A CookieJar is required.");
  const cookie = jar.header();
  const response = await requestHttp({
    ...input,
    headers: { ...input.headers, ...(cookie ? { cookie } : {}) },
  });
  jar.absorb(response.headers);
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

function absorbCookies(jar, response) {
  if (typeof response.headers.getSetCookie === "function") jar.absorb(response.headers);
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
  sleep = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)),
  now = () => new Date(),
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
  });
  const json = (base, path, options = {}) => call(base, path, {
    ...options,
    headers: { "content-type": "application/json", ...(options.headers ?? {}) },
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });
  const withJar = async (jar, base, path, options = {}) => {
    const cookie = jar.header();
    const response = await call(base, path, {
      ...options,
      headers: { ...(options.headers ?? {}), ...(cookie ? { cookie } : {}) },
    });
    absorbCookies(jar, response);
    return response;
  };

  async function waitForApi(context) {
    let attempts = 0;
    await retryBounded(async (attempt) => {
      attempts = attempt;
      const live = await call(context.runtime.apiBase, "/health/live", { timeoutMs: 5_000 });
      const ready = await call(context.runtime.apiBase, "/health/ready", { timeoutMs: 5_000 });
      assert(live.status === 200 && ready.status === 200, "API health endpoints are not ready.");
      return true;
    }, { maxAttempts: context.maxAttempts, delayMs: 2_000, sleep });
    return { attempts };
  }

  async function waitForAdmin(context) {
    let attempts = 0;
    await retryBounded(async (attempt) => {
      attempts = attempt;
      const page = await call(context.runtime.adminBase, "/", { timeoutMs: 5_000, maxBytes: 2 * 1024 * 1024 });
      assert(page.text.includes("<title>Cmsify Admin</title>"), "Admin root did not return Cmsify-specific content.");
      const asset = page.text.match(/(?:src|href)=["']([^"']*(?:blazor\.web\.js|cmsify[^"']*\.(?:css|js)))["']/i)?.[1]
        ?? "/_framework/blazor.web.js";
      const staticResponse = await call(context.runtime.adminBase, asset, { timeoutMs: 5_000, maxBytes: 8 * 1024 * 1024 });
      assert(staticResponse.bytes.byteLength > 0, "Admin static asset was empty.");
      return true;
    }, { maxAttempts: context.maxAttempts, delayMs: 2_000, sleep });
    return { attempts };
  }

  async function localLogin(context) {
    const login = await json(context.runtime.apiBase, "/api/v1/auth/login", {
      method: "POST",
      body: { email: "admin@release-smoke.invalid", password: context.secrets.seedPassword },
    });
    const loginBody = requireObject(login.json(), "Local login");
    assert(typeof loginBody.token === "string" && loginBody.token.length >= 32, "Local login did not return an opaque token.");
    context.secrets.localToken = loginBody.token;

    const workspacePage = await json(context.runtime.apiBase, "/api/v1/workspaces?page=1&pageSize=20", {
      headers: bearer(loginBody.token),
    });
    const workspace = requireObject(workspacePage.json(), "Workspace list").items?.find((item) => item.slug === "release-smoke")
      ?? workspacePage.json().items?.[0];
    requireObject(workspace, "Seed workspace");
    requireGuid(workspace.id, "Seed workspace ID");

    const jar = new CookieJar();
    const loginPage = await withJar(jar, context.runtime.adminBase, "/login");
    const form = new URLSearchParams({
      __RequestVerificationToken: antiforgeryToken(loginPage.text),
      email: "admin@release-smoke.invalid",
      password: context.secrets.seedPassword,
      returnUrl: "/workspaces",
    });
    const adminLogin = await withJar(jar, context.runtime.adminBase, "/admin-auth/login", {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: form.toString(),
      expectedStatuses: [302],
    });
    assert(requiredHeader(adminLogin, "location").startsWith("/account/change-password") || requiredHeader(adminLogin, "location") === "/workspaces", "Admin local login returned an unexpected redirect.");
    assert(jar.header().includes("cmsify.admin.auth="), "Admin local login did not establish a session cookie.");
    return { localToken: loginBody.token, workspaceId: workspace.id, localAdminCookie: jar.header() };
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
    await call(context.runtime.oidcBase, `/configure?workspaceId=${encodeURIComponent(context.runtime.workspaceId)}`);
    const tokenResponse = await json(context.runtime.oidcBase, "/test-token");
    const oidcToken = requireObject(tokenResponse.json(), "OIDC token").access_token;
    assert(typeof oidcToken === "string" && oidcToken.split(".").length === 3, "OIDC issuer did not return a JWT.");
    const me = await json(context.runtime.apiBase, "/api/v1/auth/me", { headers: bearer(oidcToken) });
    const actor = requireObject(me.json(), "OIDC API actor");
    assert(actor.role === "Admin" && actor.workspaceId === context.runtime.workspaceId, "OIDC API claims were not mapped.");

    const jar = new CookieJar();
    const challenge = await withJar(jar, context.runtime.adminBase, "/admin-auth/oidc-login?returnUrl=%2Fworkspaces", { expectedStatuses: [302] });
    const internalAuthorize = new URL(requiredHeader(challenge, "location"));
    const authorize = await withJar(jar, context.runtime.oidcBase, `${internalAuthorize.pathname}${internalAuthorize.search}`, { expectedStatuses: [302] });
    const callback = new URL(requiredHeader(authorize, "location"));
    const signedIn = await withJar(jar, `${callback.protocol}//${callback.host}`, `${callback.pathname}${callback.search}`, { expectedStatuses: [302] });
    assert(requiredHeader(signedIn, "location") === "/workspaces", "OIDC Admin callback did not retain the return URL.");
    const page = await withJar(jar, context.runtime.adminBase, "/workspaces", { maxBytes: 4 * 1024 * 1024 });
    assert(page.text.includes("Workspaces") && page.text.includes("Release Smoke Workspace"), "OIDC Admin session did not render API-backed workspace state.");
    return { status: "passed" };
  }

  async function webhookDelivery(context) {
    const workspace = context.runtime.workspaceId;
    const token = context.runtime.localToken;
    const created = await json(context.runtime.apiBase, `/api/v1/workspaces/${workspace}/webhooks`, {
      method: "POST", headers: bearer(token), expectedStatuses: [201],
      body: { name: "Release smoke receiver", url: "http://webhook:8080/hook", secret: null, events: ["workspace.updated"] },
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
      assert(Number.isSafeInteger(status.count) && status.count >= 1, "Webhook receiver has not observed a delivery.");
      delivered = status.count;
    }, { maxAttempts: 30, delayMs: 1_000, sleep });
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
    }, { maxAttempts: 30, delayMs: 1_000, sleep });
    return { persistentTemplateId: template.id, persistentContentId: content.id, persistentSlug: "release-smoke-persisted" };
  }

  async function verifyPersistence(context) {
    await waitForApi({ ...context, maxAttempts: 30 });
    await waitForAdmin({ ...context, maxAttempts: 30 });
    const login = requireObject((await json(context.runtime.apiBase, "/api/v1/auth/login", {
      method: "POST", body: { email: "admin@release-smoke.invalid", password: context.secrets.seedPassword },
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
