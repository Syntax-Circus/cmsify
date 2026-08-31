import assert from "node:assert/strict";
import test from "node:test";

import { createReleaseHttpAdapter } from "../../eng/release-smoke/http.mjs";

function headers(values = {}, setCookies = []) {
  const normalized = new Map(Object.entries(values).map(([name, value]) => [name.toLowerCase(), String(value)]));
  return {
    get: (name) => normalized.get(name.toLowerCase()) ?? null,
    getSetCookie: () => [...setCookies],
  };
}

function response({ status = 200, body = "", bytes, values, setCookies } = {}) {
  const text = bytes === undefined ? (typeof body === "string" ? body : JSON.stringify(body)) : Buffer.from(bytes).toString("utf8");
  return {
    status,
    headers: headers(values, setCookies),
    bytes: bytes === undefined ? Buffer.from(text) : Buffer.from(bytes),
    text,
    json: () => JSON.parse(text),
  };
}

const workspaceId = "11111111-1111-4111-8111-111111111111";

test("local login completes forced password change and proves the protected Admin API-backed route", async () => {
  const calls = [];
  let loginCount = 0;
  const request = async (input) => {
    calls.push(input);
    const url = new URL(input.url);
    if (url.hostname === "admin.release-smoke.invalid") assert.equal(input.tlsCa, "TEST RELEASE CA");
    if (url.hostname === "api.release-smoke.invalid" && url.pathname === "/api/v1/auth/login") {
      loginCount += 1;
      const submitted = JSON.parse(input.body);
      assert.equal(submitted.password, loginCount === 1 ? "Initial-Seed-Password" : "Changed-Smoke-Password");
      return response({ body: { token: `local-token-${loginCount}`.padEnd(40, "x"), mustChangePassword: loginCount === 1 } });
    }
    if (url.pathname === "/api/v1/auth/change-password") {
      assert.match(input.headers.authorization, /^Bearer local-token-1/);
      assert.deepEqual(JSON.parse(input.body), { currentPassword: "Initial-Seed-Password", newPassword: "Changed-Smoke-Password" });
      return response({ status: 204 });
    }
    if (url.pathname === "/api/v1/workspaces") return response({ body: { items: [{ id: workspaceId, slug: "release-smoke" }] } });
    if (url.hostname === "admin.release-smoke.invalid" && url.pathname === "/login") {
      return response({
        body: '<form><input name="__RequestVerificationToken" value="anti-token"></form>',
        setCookies: ["antiforgery=anti; Path=/; Secure; HttpOnly; SameSite=Lax"],
      });
    }
    if (url.pathname === "/admin-auth/login") {
      assert.match(input.headers.cookie, /antiforgery=anti/);
      const form = new URLSearchParams(input.body);
      assert.equal(form.get("password"), "Changed-Smoke-Password");
      return response({
        status: 302,
        values: { location: "/workspaces" },
        setCookies: ["cmsify.admin.auth=authenticated; Path=/; Secure; HttpOnly; SameSite=Lax"],
      });
    }
    if (url.pathname === `/admin-auth/release-smoke/cmsify-smoke-1234abcd/protected-workspaces`) {
      assert.match(input.headers.cookie, /cmsify\.admin\.auth=authenticated/);
      return response({ body: { proof: "cmsify.release-smoke.admin-api.v1", workspaces: [{ id: workspaceId, name: "Release Smoke Workspace", slug: "release-smoke" }] } });
    }
    throw new Error(`Unexpected local-login request ${input.method} ${input.url}`);
  };
  const adapter = createReleaseHttpAdapter({ request });
  const context = {
    runId: "cmsify-smoke-1234abcd",
    runtime: { apiBase: "https://api.release-smoke.invalid", adminBase: "https://admin.release-smoke.invalid", tlsCa: "TEST RELEASE CA" },
    secrets: { seedPassword: "Initial-Seed-Password", changedAdminPassword: "Changed-Smoke-Password" },
  };

  const result = await adapter.localLogin(context);

  assert.equal(result.workspaceId, workspaceId);
  assert.equal(loginCount, 2);
  assert.equal(calls.at(-1).url, "https://admin.release-smoke.invalid/admin-auth/release-smoke/cmsify-smoke-1234abcd/protected-workspaces");
  assert.equal(JSON.stringify(result).includes("cmsify.admin.auth"), false);
});

test("workspace API-client and media transcripts prove scope, multipart upload, and exact download bytes", async () => {
  const calls = [];
  const mediaId = "33333333-3333-4333-8333-333333333333";
  const expectedMedia = Buffer.from("cmsify-release-smoke-media-v1\n");
  const request = async (input) => {
    calls.push(input);
    const url = new URL(input.url);
    if (url.pathname === "/api/v1/clients") {
      const payload = JSON.parse(input.body);
      assert.equal(payload.workspaceId, workspaceId);
      assert.equal(payload.role, "Editor");
      return response({ status: 201, body: { token: "cmsify_workspace_scoped_token", client: { id: "44444444-4444-4444-8444-444444444444" } } });
    }
    if (url.pathname === "/api/v1/auth/me") return response({ body: { workspaceId, role: "Editor" } });
    if (url.pathname.endsWith("/media") && input.method === "POST") {
      assert.ok(input.body instanceof FormData);
      assert.equal(input.body.get("altText"), "Release smoke media");
      return response({ status: 201, body: { id: mediaId } });
    }
    if (url.pathname.endsWith(`/media/${mediaId}/file`)) return response({ bytes: expectedMedia });
    throw new Error(`Unexpected client/media request ${input.method} ${input.url}`);
  };
  const adapter = createReleaseHttpAdapter({ request });
  const context = { runtime: { apiBase: "http://api.release-smoke.invalid", localToken: "local-token", workspaceId }, secrets: {} };

  const client = await adapter.apiClientAuth(context);
  Object.assign(context.runtime, client);
  const media = await adapter.mediaRoundTrip(context);

  assert.equal(client.apiClientId, "44444444-4444-4444-8444-444444444444");
  assert.equal(media.mediaId, mediaId);
  assert.ok(calls.every(({ headers: requestHeaders }) => requestHeaders.authorization?.startsWith("Bearer ")));
});

test("scheduled publication and persistence transcripts prove lifecycle state across both verification passes", async () => {
  const calls = [];
  const templateId = "55555555-5555-4555-8555-555555555555";
  const versionId = "66666666-6666-4666-8666-666666666666";
  const contentId = "77777777-7777-4777-8777-777777777777";
  const mediaId = "88888888-8888-4888-8888-888888888888";
  const mediaBytes = Buffer.from("persistent-media");
  let statusReads = 0;
  const request = async (input) => {
    calls.push(input);
    const url = new URL(input.url);
    if (url.pathname === "/health/live" || url.pathname === "/health/ready") return response({ body: { status: "Healthy" } });
    if (url.hostname === "admin.release-smoke.invalid" && url.pathname === "/") return response({ body: '<title>Cmsify Admin</title><script src="/_framework/blazor.web.js"></script>' });
    if (url.pathname === "/_framework/blazor.web.js") return response({ body: "globalThis.Blazor={};" });
    if (url.pathname.endsWith("/templates") && input.method === "POST") return response({ status: 201, body: { id: templateId, currentVersion: { id: versionId } } });
    if (url.pathname.endsWith(`/templates/${templateId}/versions/1/publish`)) return response({ body: { status: "Published" } });
    if (url.pathname.endsWith("/content") && input.method === "POST") return response({ status: 201, body: { id: contentId } });
    if (url.pathname.endsWith(`/content/${contentId}/submit`) || url.pathname.endsWith(`/content/${contentId}/approve`)) return response({ body: { id: contentId } });
    if (url.pathname.endsWith(`/content/${contentId}/publish`)) {
      const payload = JSON.parse(input.body);
      assert.equal(payload.effectiveStartAt, null);
      assert.equal(payload.effectiveEndAt, null);
      assert.equal(payload.publishAt, "2026-08-29T12:00:01.500Z");
      return response({ body: { id: contentId, status: "Approved" } });
    }
    if (url.pathname.endsWith(`/content/${contentId}`)) {
      statusReads += 1;
      return response({ body: { id: contentId, status: "Published" } });
    }
    if (url.pathname === "/api/v1/auth/login") {
      assert.equal(JSON.parse(input.body).password, "changed-password");
      return response({ body: { token: "persistence-token".padEnd(40, "x") } });
    }
    if (url.pathname.endsWith("/content/by-slug/release-smoke-persisted")) return response({ body: { id: contentId, status: "Published" } });
    if (url.pathname.endsWith(`/media/${mediaId}/file`)) return response({ bytes: mediaBytes });
    throw new Error(`Unexpected schedule/persistence request ${input.method} ${input.url}`);
  };
  const adapter = createReleaseHttpAdapter({ request, sleep: async () => {}, now: () => new Date("2026-08-29T12:00:00Z") });
  const context = {
    runtime: {
      apiBase: "http://api.release-smoke.invalid", adminBase: "https://admin.release-smoke.invalid",
      localToken: "local-token", workspaceId, tlsCa: "TEST RELEASE CA",
    },
    secrets: { seedPassword: "initial-password", changedAdminPassword: "changed-password" },
    artifacts: { mediaId, mediaBytes: mediaBytes.toString("base64") },
  };

  Object.assign(context.artifacts, await adapter.scheduledPublication(context));
  await adapter.verifyPersistence(context);
  await adapter.verifyRestoredState(context);

  assert.equal(statusReads, 1);
  assert.equal(calls.filter(({ url }) => new URL(url).pathname.endsWith("/content/by-slug/release-smoke-persisted")).length, 2);
  assert.equal(calls.filter(({ url }) => new URL(url).pathname.endsWith(`/media/${mediaId}/file`)).length, 2);
});

test("successful webhook transcript uses the exact run-bound host and exact event type", async () => {
  let createdUrl;
  const request = async (input) => {
    const url = new URL(input.url);
    if (url.pathname.endsWith("/webhooks")) {
      createdUrl = JSON.parse(input.body).url;
      return response({ status: 201, body: { endpoint: { id: "99999999-9999-4999-8999-999999999999" } } });
    }
    if (url.pathname === `/api/v1/workspaces/${workspaceId}` && input.method === "GET") return response({ body: { slug: "release-smoke" }, values: { etag: '"workspace"' } });
    if (url.pathname === `/api/v1/workspaces/${workspaceId}` && input.method === "PUT") return response({ body: { id: workspaceId } });
    if (url.pathname === "/status") return response({ body: { count: 1, eventTypes: ["workspace.updated"] } });
    throw new Error(`Unexpected successful webhook request ${input.method} ${input.url}`);
  };
  const adapter = createReleaseHttpAdapter({ request, sleep: async () => {} });
  const context = {
    runId: "cmsify-smoke-1234abcd",
    runtime: { apiBase: "http://api.release-smoke.invalid", webhookBase: "http://receiver.release-smoke.invalid", workspaceId, localToken: "local-token" },
  };

  const result = await adapter.webhookDelivery(context);

  assert.equal(createdUrl, "http://webhook.cmsify-smoke-1234abcd.release-smoke.invalid:8080/hook");
  assert.equal(result.deliveries, 1);
});

test("OIDC keeps Admin cookies off the issuer and proves token forwarding through a protected Admin render", async () => {
  const calls = [];
  const request = async (input) => {
    calls.push(input);
    const url = new URL(input.url);
    if (["admin.release-smoke.invalid", "issuer.release-smoke.invalid"].includes(url.hostname)) assert.equal(input.tlsCa, "TEST RELEASE CA");
    if (url.hostname === "issuer.release-smoke.invalid" && url.pathname === "/configure") return response({ body: { configured: true } });
    if (url.hostname === "issuer.release-smoke.invalid" && url.pathname === "/test-token") return response({ body: { access_token: "header.payload.signature" } });
    if (url.hostname === "api.release-smoke.invalid" && url.pathname === "/api/v1/auth/me") return response({ body: { role: "Admin", workspaceId } });
    if (url.hostname === "admin.release-smoke.invalid" && url.pathname === "/admin-auth/oidc-login") {
      return response({
        status: 302,
        values: { location: "https://oidc:8080/authorize?state=state&nonce=nonce&redirect_uri=https%3A%2F%2Fadmin.release-smoke.invalid%2Fsignin-oidc" },
        setCookies: [
          ".AspNetCore.Correlation.release=correlation; Path=/signin-oidc; Secure; HttpOnly; SameSite=None",
          ".AspNetCore.OpenIdConnect.Nonce.release=nonce; Path=/signin-oidc; Secure; HttpOnly; SameSite=None",
        ],
      });
    }
    if (url.hostname === "issuer.release-smoke.invalid" && url.pathname === "/authorize") {
      assert.equal(input.headers.cookie, undefined);
      return response({ status: 302, values: { location: "https://admin.release-smoke.invalid/signin-oidc?code=release-smoke-code&state=state" } });
    }
    if (url.hostname === "admin.release-smoke.invalid" && url.pathname === "/signin-oidc") {
      assert.match(input.headers.cookie, /Correlation\.release=correlation/);
      assert.match(input.headers.cookie, /Nonce\.release=nonce/);
      return response({
        status: 302,
        values: { location: "/workspaces" },
        setCookies: ["cmsify.admin.auth=oidc-session; Path=/; Secure; HttpOnly; SameSite=Lax"],
      });
    }
    if (url.hostname === "admin.release-smoke.invalid" && url.pathname === "/admin-auth/release-smoke/cmsify-smoke-1234abcd/protected-workspaces") {
      assert.match(input.headers.cookie, /cmsify\.admin\.auth=oidc-session/);
      return response({ body: { proof: "cmsify.release-smoke.admin-api.v1", workspaces: [{ id: workspaceId, name: "Release Smoke Workspace", slug: "release-smoke" }] } });
    }
    throw new Error(`Unexpected OIDC request ${input.method} ${input.url}`);
  };
  const adapter = createReleaseHttpAdapter({ request });
  const context = {
    runId: "cmsify-smoke-1234abcd",
    runtime: {
      apiBase: "https://api.release-smoke.invalid",
      adminBase: "https://admin.release-smoke.invalid",
      oidcBase: "https://issuer.release-smoke.invalid",
      tlsCa: "TEST RELEASE CA",
      workspaceId,
    },
  };

  await adapter.oidcFlow(context);

  const issuerCalls = calls.filter(({ url }) => new URL(url).hostname === "issuer.release-smoke.invalid");
  assert.ok(issuerCalls.every(({ headers: requestHeaders }) => requestHeaders.cookie === undefined));
  assert.equal(calls.at(-1).url, "https://admin.release-smoke.invalid/admin-auth/release-smoke/cmsify-smoke-1234abcd/protected-workspaces");
});

test("webhook delivery rejects a receiver count that does not contain the exact expected event", async () => {
  const request = async (input) => {
    const url = new URL(input.url);
    if (url.pathname.endsWith("/webhooks")) return response({ status: 201, body: { endpoint: { id: "22222222-2222-4222-8222-222222222222" } } });
    if (url.pathname === `/api/v1/workspaces/${workspaceId}` && input.method === "GET") {
      return response({ body: { slug: "release-smoke" }, values: { etag: '"workspace-etag"' } });
    }
    if (url.pathname === `/api/v1/workspaces/${workspaceId}` && input.method === "PUT") return response({ body: { id: workspaceId } });
    if (url.pathname === "/status") return response({ body: { count: 1, eventTypes: ["content.published"] } });
    throw new Error(`Unexpected webhook request ${input.method} ${input.url}`);
  };
  const adapter = createReleaseHttpAdapter({ request, sleep: async () => {} });

  await assert.rejects(adapter.webhookDelivery({
    runId: "cmsify-smoke-1234abcd",
    runtime: {
      apiBase: "https://api.release-smoke.invalid",
      webhookBase: "http://receiver.release-smoke.invalid",
      workspaceId,
      localToken: "local-token",
    },
  }), /workspace\.updated/i);
});
