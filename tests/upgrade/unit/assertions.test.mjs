import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import test from "node:test";

import {
  assertionCatalog,
  assertionNames,
  createDockerSqlAdapter,
  runNamedAssertion,
} from "../../../eng/upgrade-tests/assertions.mjs";
import { requestBytes, requestJson } from "../../../eng/upgrade-tests/http.mjs";
import { REQUIRED_ASSERTIONS_BY_SCENARIO } from "../../../eng/upgrade-tests/expected.mjs";
import { REQUIRED_SCENARIOS } from "../../../eng/upgrade-tests/manifest.mjs";

const expected = JSON.parse(readFileSync(new URL("../fixtures/v0.1.3/expected.json", import.meta.url), "utf8"));
const manifest = JSON.parse(readFileSync(new URL("../fixtures/v0.1.3/manifest.json", import.meta.url), "utf8"));
const CANDIDATE_MIGRATIONS = [
  ...expected.migrations,
  "20260826135220_AddWebhookOutbox",
  "20260826215147_ExpandWebhookSecretCiphertext",
  "20260827135736_AddMediaLifecycleReconciliation",
];
const LEGACY_KEY = "Q21zaWZ5IGZpeHR1cmUgbGVnYWN5IGtleSAwLjEuMyE=";
const LEGACY_CIPHERTEXT = "v1.AAECAwQFBgcICQoL.BFBpi/UrF42vy+zL9I8lnA==.0pqpkko3sHX/wlWOkRiRGAJYDcLrmg==";
const LEGACY_SECRET_SHA256 = createHash("sha256").update("fixture-webhook-secret").digest("hex");

function candidateExpected() {
  return {
    ...expected,
    authentication: {
      ...expected.authentication,
      adminEmail: "fixture-admin@example.test",
      adminPassword: "Cmsify-fixture-user-only-0.1.3!",
    },
    candidate: {
      migrations: CANDIDATE_MIGRATIONS,
      storageProvider: "s3",
      legacyWebhookKeyBase64: LEGACY_KEY,
      legacyWebhookSecretSha256: LEGACY_SECRET_SHA256,
    },
  };
}

function context(overrides = {}) {
  return {
    fixture: manifest,
    expected: candidateExpected(),
    docker: {},
    apiBaseUrl: "http://candidate.test",
    token: expected.authentication.readerToken,
    phase: "baseline",
    ...overrides,
  };
}

test("Docker SQL adapter passes values as psql variables and withholds failed query material", async () => {
  const calls = [];
  const secretValue = "fixture-sql-value-secret";
  const harness = {
    async exec(service, args, options) {
      calls.push({ service, args, options });
      if (calls.length === 1) return { exitCode: 0, stdout: "1\n", stderr: "", durationMs: 0 };
      throw new Error(`database leaked ${secretValue}`);
    },
  };
  const adapter = createDockerSqlAdapter(harness);

  assert.equal(await adapter.scalar("SELECT :'fixture_value';", { fixture_value: secretValue }), "1");
  assert.equal(calls[0].service, "postgres");
  assert.ok(calls[0].args.includes(`fixture_value=${secretValue}`));
  assert.equal(calls[0].args.includes("--command"), false);
  assert.equal(calls[0].args.at(-1), "--file=-");
  assert.equal(calls[0].options.stdin, "SELECT :'fixture_value';");
  await assert.rejects(
    () => adapter.scalar("SELECT :'fixture_value';", { fixture_value: secretValue }),
    (error) => {
      assert.match(error.message, /query failed.*withheld/i);
      assert.doesNotMatch(error.message, new RegExp(secretValue));
      return true;
    },
  );
});

test("every required scenario and expected category has a baseline assertion", () => {
  const catalog = assertionCatalog("baseline");

  for (const scenario of REQUIRED_SCENARIOS) {
    assert(catalog.some((entry) => entry.scenario === scenario), `${scenario} has no registered assertion`);
    for (const category of REQUIRED_ASSERTIONS_BY_SCENARIO[scenario]) {
      assert(
        catalog.some((entry) => entry.scenario === scenario && entry.category === category),
        `${scenario}.${category} has no registered assertion`,
      );
    }
  }
});

test("rollback cannot use a weaker assertion set", () => {
  assert.deepEqual(assertionNames("rollback"), assertionNames("baseline"));
});

test("historical unpaged template and content version collections remain observable", async () => {
  const ids = { ...expected.ids, ...expected.relatedIds };
  const fakeHttp = {
    async requestJson(request) {
      const path = new URL(request.url).pathname;
      if (path.endsWith(`/templates/${ids.template}/versions`)) return { status: 200, headers: new Headers(), body: [{ id: ids.templateVersion }] };
      if (path.endsWith(`/templates/${ids.template}/versions/1`)) {
        return {
          status: 200,
          headers: new Headers(),
          body: {
            fields: [
              { id: ids.titleField, primitiveType: "Text", componentId: null },
              { id: ids.choiceField, primitiveType: "PickList", componentId: null },
              { id: ids.componentField, primitiveType: null, componentId: ids.component },
            ],
          },
        };
      }
      if (path.endsWith(`/templates/${ids.template}`)) return { status: 200, headers: new Headers(), body: { id: ids.template, currentVersion: { id: ids.templateVersion } } };
      if (path.endsWith("/templates")) return { status: 200, headers: new Headers(), body: { items: [{ id: ids.template }] } };
      if (path.endsWith(`/content/${ids.publishedContent}/versions`)) return { status: 200, headers: new Headers(), body: [{ id: ids.publishedVersion }] };
      if (path.endsWith(`/content/${ids.draftContent}`)) return { status: 200, headers: new Headers(), body: { id: ids.draftContent, status: "Draft" } };
      if (path.endsWith(`/content/${ids.publishedContent}`)) return { status: 200, headers: new Headers(), body: { id: ids.publishedContent, status: "Published" } };
      if (path.endsWith("/content")) return { status: 200, headers: new Headers(), body: { items: [{ id: ids.draftContent }, { id: ids.publishedContent }] } };
      throw new Error(`unexpected fake route ${path}`);
    },
  };
  const fakeSql = { async scalar() { return "3"; } };

  await runNamedAssertion("published-template-fields", context({ http: fakeHttp, sql: fakeSql }));
  fakeSql.scalar = async () => "2";
  await runNamedAssertion("draft-and-published-distinct", context({ http: fakeHttp, sql: fakeSql }));
});

test("historical unpaged reusable-model collections remain observable", async () => {
  const ids = { ...expected.ids, ...expected.relatedIds };
  let pickListRequests = 0;
  const fakeHttp = {
    async requestJson(request) {
      const path = new URL(request.url).pathname;
      if (path.endsWith("/components")) return { status: 200, headers: new Headers(), body: [{ id: ids.component }] };
      if (path.endsWith(`/components/${ids.component}`)) return { status: 200, headers: new Headers(), body: { currentVersion: { id: ids.componentVersion } } };
      if (path.endsWith(`/components/${ids.component}/versions/1`)) {
        return { status: 200, headers: new Headers(), body: { fields: [{ nestedComponentId: null }, { nestedComponentId: null }] } };
      }
      if (path.endsWith(`/content/${ids.publishedContent}`)) {
        return { status: 200, headers: new Headers(), body: { fields: [{ fieldId: ids.componentField, jsonValue: { summary: "Inline published", accent: "alpha" } }] } };
      }
      if (path.endsWith("/picklists")) {
        pickListRequests += 1;
        return { status: 200, headers: new Headers(), body: [{ id: ids.choiceSet }] };
      }
      if (path.endsWith(`/picklists/${ids.choiceSet}`)) {
        return { status: 200, headers: new Headers(), body: { currentRevisionId: ids.choiceRevisionTwo, currentVersionNumber: 2, options: [{ value: "alpha", label: expected.content.currentChoiceLabel }] } };
      }
      if (path.endsWith(`/picklists/${ids.choiceSet}/revisions/${ids.choiceRevisionOne}`)) {
        return { status: 200, headers: new Headers(), body: { options: [{ value: "alpha", label: expected.content.publishedChoiceLabel }] } };
      }
      throw new Error(`unexpected fake route ${path}`);
    },
  };
  let scalarCalls = 0;
  const fakeSql = { async scalar() { scalarCalls += 1; return scalarCalls === 2 ? "0" : scalarCalls === 3 ? "2" : "1"; } };

  await runNamedAssertion("inline-acyclic-snapshot", context({ http: fakeHttp, sql: fakeSql }));
  await runNamedAssertion("immutable-revisions", context({ http: fakeHttp, sql: fakeSql }));

  assert.equal(pickListRequests, 1);
});

test("effective range assertions compare equivalent UTC instants", async () => {
  const fakeHttp = {
    async requestJson() {
      return {
        status: 200,
        headers: new Headers(),
        body: {
          id: expected.relatedIds.expiredVersion,
          effectiveStartAt: "2026-08-18T12:00:00+00:00",
          effectiveEndAt: "2026-08-19T12:00:00+00:00",
        },
      };
    },
  };
  const fakeSql = { async scalar() { return "1"; } };

  await runNamedAssertion("expired-effective-range", context({ http: fakeHttp, sql: fakeSql }));
});

test("candidate readiness uses the build informational version, not the OCI base version", async () => {
  const sourceSha = "a".repeat(40);
  const version = "1.0.0-task9";
  const fakeHttp = {
    async requestJson() {
      return {
        status: 200,
        headers: new Headers(),
        body: { status: "Healthy", metadata: { version: `${version}+${sourceSha}` } },
      };
    },
  };

  await runNamedAssertion("health-ready", context({
    phase: "candidate",
    candidate: { version, sourceSha },
    http: fakeHttp,
  }));
});

test("package provenance assertion preserves the fixture's exact populated and null columns", async () => {
  const statements = [];
  const fakeSql = {
    async scalar(statement) {
      statements.push(statement);
      return statement.includes("FROM templates")
        && statement.includes("package_namespace IS NULL")
        && statement.includes("package_id IS NULL")
        && statement.includes("package_version IS NULL")
        ? "3"
        : "0";
    },
  };

  await runNamedAssertion("package-provenance", context({ sql: fakeSql }));
  assert.equal(statements.length, 1);
});

test("editor relationship uses bound SQL values and requires an active Editor actor", async () => {
  const calls = [];
  const fakeSql = {
    async scalar(statement, parameters) {
      calls.push({ statement, parameters });
      return "1";
    },
  };

  await runNamedAssertion("editor-primary-write-grant", context({ sql: fakeSql }));

  assert.equal(calls.length, 1);
  assert.doesNotMatch(calls[0].statement, new RegExp(expected.ids.editorUser));
  assert.match(calls[0].statement, /actor\.role = 'Editor'/);
  assert.match(calls[0].statement, /actor\.is_active/);
  assert.deepEqual(calls[0].parameters, {
    editor_user_id: expected.ids.editorUser,
    primary_workspace_id: expected.ids.primaryWorkspace,
  });
});

test("hidden media SQL binds the asset to its primary workspace", async () => {
  const calls = [];
  const fakeHttp = {
    async requestJson() { return { status: 404, headers: new Headers(), body: { title: "Not Found" } }; },
    async requestBytes() { return { status: 404, headers: new Headers(), bytes: Buffer.alloc(0), byteLength: 0, sha256: createHash("sha256").digest("hex") }; },
  };
  const fakeSql = {
    async scalar(statement, parameters) {
      calls.push({ statement, parameters });
      return "1";
    },
  };
  const fakeStorage = { async sha256() { return expected.media.image.sha256; } };

  await runNamedAssertion("historical-deleted-media-hidden", context({ http: fakeHttp, sql: fakeSql, storage: fakeStorage }));

  assert.equal(calls.length, 1);
  assert.doesNotMatch(calls[0].statement, new RegExp(expected.ids.imageMedia));
  assert.match(calls[0].statement, /JOIN workspaces workspace ON workspace\.id = asset\.workspace_id/);
  assert.deepEqual(calls[0].parameters.image_media_id, expected.ids.imageMedia);
  assert.deepEqual(calls[0].parameters.primary_workspace_id, expected.ids.primaryWorkspace);
});

test("media mismatch names the asset and digests without logging payload bytes", async () => {
  const actualSha = "0".repeat(64);
  const payloadSecret = "binary-payload-must-not-appear";
  const fakeHttp = {
    async requestJson(request) {
      if (new URL(request.url).pathname.endsWith(`/media/${expected.ids.textMedia}`)) {
        return { status: 200, headers: new Headers(), body: { id: expected.ids.textMedia } };
      }
      return {
        status: 200,
        headers: new Headers(),
        body: {
          items: [{
            id: expected.ids.textMedia,
            fileName: expected.media.text.fileName,
            mimeType: expected.media.text.contentType,
            sizeBytes: expected.media.text.sizeBytes,
          }],
        },
      };
    },
    async requestBytes(request) {
      assert.match(request.url, new RegExp(expected.ids.textMedia));
      return {
        status: 200,
        headers: new Headers({ "content-type": "text/plain" }),
        bytes: Buffer.from(payloadSecret),
        byteLength: Buffer.byteLength(payloadSecret),
        sha256: actualSha,
      };
    },
  };

  await assert.rejects(
    () => runNamedAssertion("available-media-download", context({ http: fakeHttp })),
    (error) => {
      assert.match(error.message, new RegExp(expected.ids.textMedia));
      assert.match(error.message, new RegExp(expected.media.text.sha256));
      assert.match(error.message, new RegExp(actualSha));
      assert.doesNotMatch(error.message, new RegExp(payloadSecret));
      return true;
    },
  );
});

test("JSON requests are bounded, manual, credential-free, authenticated, and status-exact", async () => {
  let timeoutMs;
  let observed;
  const signal = new AbortController().signal;
  const response = await requestJson({
    url: "https://cmsify.test/api/v1/auth/me",
    method: "GET",
    token: "cmsify_fixture-token",
    expectedStatuses: new Set([200]),
    signalFactory(ms) {
      timeoutMs = ms;
      return signal;
    },
    async fetchImpl(url, init) {
      observed = { url, init };
      return new Response(JSON.stringify({ role: "Reader" }), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    },
  });

  assert.equal(timeoutMs, 5_000);
  assert.equal(observed.url, "https://cmsify.test/api/v1/auth/me");
  assert.equal(observed.init.redirect, "manual");
  assert.equal(observed.init.credentials, "omit");
  assert.equal(observed.init.signal, signal);
  assert.equal(new Headers(observed.init.headers).get("authorization"), "Bearer cmsify_fixture-token");
  assert.equal(response.status, 200);
  assert.deepEqual(response.body, { role: "Reader" });
});

test("unexpected JSON status reports only sanitized ProblemDetails diagnostics", async () => {
  const token = "cmsify_fixture-secret-token";
  const bodySecret = "response-body-secret";
  await assert.rejects(
    () => requestJson({
      url: "https://cmsify.test/api/v1/workspaces/denied?token=query-secret",
      method: "POST",
      token,
      body: { secret: "request-body-secret" },
      expectedStatuses: new Set([200]),
      async fetchImpl() {
        return new Response(JSON.stringify({
          title: "Failure",
          detail: bodySecret,
          traceId: "trace-fixture-001",
        }), {
          status: 500,
          headers: {
            "content-type": "application/problem+json",
            "x-correlation-id": "correlation-fixture-001",
          },
        });
      },
    }),
    (error) => {
      assert.match(error.message, /POST \/api\/v1\/workspaces\/denied/);
      assert.match(error.message, /status 500/);
      assert.match(error.message, /correlation-fixture-001/);
      assert.match(error.message, /trace-fixture-001/);
      assert.doesNotMatch(error.message, /query-secret|request-body-secret|response-body-secret|cmsify_fixture-secret-token/);
      return true;
    },
  );
});

test("JSON responses reject malformed UTF-8 with sanitized diagnostics", async () => {
  const malformed = Buffer.concat([
    Buffer.from('{"traceId":"trace-fixture-utf8","value":"', "utf8"),
    Buffer.from([0xff]),
    Buffer.from('"}', "utf8"),
  ]);

  await assert.rejects(
    () => requestJson({
      url: "https://cmsify.test/api/v1/malformed",
      method: "GET",
      expectedStatuses: new Set([200]),
      async fetchImpl() {
        return new Response(malformed, { status: 200 });
      },
    }),
    (error) => {
      assert.match(error.message, /GET \/api\/v1\/malformed.*invalid JSON response/);
      assert.doesNotMatch(error.message, /trace-fixture-utf8|�/);
      return true;
    },
  );
});

test("request preparation failures cannot reflect header or body exception messages", async () => {
  const preparationSecret = "fixture-request-body-secret-marker";

  await assert.rejects(
    () => requestJson({
      url: "https://cmsify.test/api/v1/content",
      method: "POST",
      expectedStatuses: new Set([201]),
      body: {
        toJSON() {
          throw new Error(preparationSecret);
        },
      },
      async fetchImpl() {
        throw new Error("fetch must not run");
      },
    }),
    (error) => {
      assert.match(error.message, /POST \/api\/v1\/content.*request preparation error/);
      assert.doesNotMatch(error.message, new RegExp(preparationSecret));
      return true;
    },
  );
});

test("byte requests hash streamed chunks and enforce the 10 MiB cap", async () => {
  const chunks = [Buffer.from("abc"), Buffer.from("def")];
  const response = await requestBytes({
    url: "https://cmsify.test/media/file",
    method: "GET",
    token: "cmsify_fixture-token",
    expectedStatuses: new Set([200]),
    async fetchImpl() {
      return new Response(new ReadableStream({
        start(controller) {
          for (const chunk of chunks) controller.enqueue(chunk);
          controller.close();
        },
      }), { status: 200 });
    },
  });

  assert.equal(response.byteLength, 6);
  assert.equal(response.sha256, createHash("sha256").update("abcdef").digest("hex"));
  assert.deepEqual(response.bytes, Buffer.from("abcdef"));

  await assert.rejects(
    () => requestBytes({
      url: "https://cmsify.test/media/oversized",
      method: "GET",
      token: "cmsify_fixture-token",
      expectedStatuses: new Set([200]),
      async fetchImpl() {
        return new Response("must-not-appear", {
          status: 200,
          headers: { "content-length": String(10 * 1024 * 1024 + 1) },
        });
      },
    }),
    (error) => {
      assert.match(error.message, /GET \/media\/oversized/);
      assert.match(error.message, /10 MiB/);
      assert.doesNotMatch(error.message, /must-not-appear|cmsify_fixture-token/);
      return true;
    },
  );
});

test("HTTP timeout remains active while streaming and stream failures stay sanitized", async () => {
  const timeoutController = new AbortController();
  const callerController = new AbortController();
  const streamSecret = "stream-transport-secret";

  await assert.rejects(
    () => requestBytes({
      url: "https://cmsify.test/media/slow",
      method: "GET",
      token: "cmsify_fixture-token",
      expectedStatuses: new Set([200]),
      signal: callerController.signal,
      signalFactory() {
        return timeoutController.signal;
      },
      async fetchImpl(_url, init) {
        return new Response(new ReadableStream({
          start(controller) {
            let settled = false;
            init.signal.addEventListener("abort", () => {
              settled = true;
              controller.error(new Error(streamSecret));
            }, { once: true });
            setTimeout(() => timeoutController.abort(new Error(streamSecret)), 0);
            setTimeout(() => {
              if (!settled) controller.close();
            }, 25);
          },
        }), { status: 200 });
      },
    }),
    (error) => {
      assert.match(error.message, /GET \/media\/slow.*transport error/);
      assert.doesNotMatch(error.message, new RegExp(streamSecret));
      return true;
    },
  );
});

test("candidate lifecycle assertion distinguishes active and deleted historical media", async () => {
  const sqlCalls = [];
  const fakeSql = {
    async json(statement, parameters) {
      sqlCalls.push({ statement, parameters });
      return [
        {
          id: expected.ids.textMedia,
          workspaceId: expected.ids.primaryWorkspace,
          provider: "s3",
          storageKey: expected.media.text.storageKey,
          blobState: "Available",
          deletionIntentReason: null,
          deletionIntentProvider: null,
          deletionIntentStorageKey: null,
          deletionIntentCount: 0,
        },
        {
          id: expected.ids.imageMedia,
          workspaceId: expected.ids.primaryWorkspace,
          provider: "s3",
          storageKey: expected.media.image.storageKey,
          blobState: "DeletePending",
          deletionIntentReason: "migration_deleted",
          deletionIntentProvider: "s3",
          deletionIntentStorageKey: expected.media.image.storageKey,
          deletionIntentCount: 1,
        },
      ];
    },
  };

  await runNamedAssertion("candidate-deletion-boundary", context({ phase: "candidate", sql: fakeSql }));

  assert.equal(sqlCalls.length, 1);
  assert.doesNotMatch(sqlCalls[0].statement, new RegExp(expected.ids.textMedia));
  assert.deepEqual(sqlCalls[0].parameters, {
    text_media_id: expected.ids.textMedia,
    image_media_id: expected.ids.imageMedia,
    primary_workspace_id: expected.ids.primaryWorkspace,
  });
});

test("candidate legacy webhook assertion decrypts v1 ciphertext without exposing plaintext", async () => {
  const fakeSql = { async scalar() { return LEGACY_CIPHERTEXT; } };

  await runNamedAssertion("candidate-webhook-legacy-ciphertext-readable", context({
    phase: "candidate",
    sql: fakeSql,
  }));

  const wrongExpected = candidateExpected();
  wrongExpected.candidate.legacyWebhookSecretSha256 = "f".repeat(64);
  await assert.rejects(
    () => runNamedAssertion("candidate-webhook-legacy-ciphertext-readable", context({
      phase: "candidate",
      expected: wrongExpected,
      sql: fakeSql,
    })),
    (error) => {
      assert.match(error.message, new RegExp(expected.ids.webhook));
      assert.doesNotMatch(error.message, /fixture-webhook-secret|0pqpkko3sHX/);
      return true;
    },
  );
});

test("candidate canary rejects a create response without an ETag", async () => {
  const fakeHttp = {
    async requestJson(request) {
      if (request.url.endsWith("/api/v1/auth/login")) return { status: 200, body: { token: "session-token" }, headers: new Headers() };
      if (request.method === "POST") return { status: 201, body: { id: "dddddddd-dddd-4ddd-8ddd-dddddddddddd", slug: request.body.slug }, headers: new Headers() };
      return { status: 200, body: { id: "dddddddd-dddd-4ddd-8ddd-dddddddddddd", slug: "upgrade-canary-unit-run-001" }, headers: new Headers() };
    },
  };
  const fakeSql = { async json() { return {
    componentVersionCount: 1,
    componentFieldCount: 2,
    choiceRevisionCount: 2,
    contentVersionCount: 1,
    originalChoiceLabel: expected.content.publishedChoiceLabel,
    currentChoiceLabel: expected.content.currentChoiceLabel,
    publishedChoiceLabel: expected.content.publishedChoiceLabel,
  }; } };

  await assert.rejects(
    () => runNamedAssertion("candidate-canary-write-read", context({ phase: "candidate", runId: "unit-run-001", http: fakeHttp, sql: fakeSql })),
    /create.*ETag/i,
  );
});

test("candidate canary exercises ETag concurrency and preserves immutable history", async () => {
  const canaryId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
  const createEtag = '"canary-create-etag"';
  const updateEtag = '"canary-update-etag"';
  const requests = [];
  let updatedFields = [];
  const fakeHttp = {
    async requestJson(request) {
      requests.push(request);
      if (request.url.endsWith("/api/v1/auth/login")) return { status: 200, body: { token: "session-token" }, headers: new Headers() };
      if (request.method === "POST") return { status: 201, body: { id: canaryId, slug: request.body.slug, fields: request.body.fields }, headers: new Headers({ etag: createEtag }) };
      if (request.method === "PUT" && !request.headers?.["if-match"]) return { status: 412, body: { title: "Concurrency mismatch" }, headers: new Headers() };
      if (request.method === "PUT" && request.headers["if-match"] === '"stale-canary-etag"') return { status: 412, body: { title: "Concurrency mismatch" }, headers: new Headers() };
      if (request.method === "PUT") {
        updatedFields = request.body.fields;
        return { status: 200, body: { id: canaryId, slug: request.body.slug, fields: updatedFields }, headers: new Headers({ etag: updateEtag }) };
      }
      return { status: 200, body: { id: canaryId, slug: requests[1].body.slug, fields: updatedFields }, headers: new Headers({ etag: updateEtag }) };
    },
  };
  const immutableSnapshot = {
    componentVersionCount: 1,
    componentFieldCount: 2,
    choiceRevisionCount: 2,
    contentVersionCount: 1,
    originalChoiceLabel: expected.content.publishedChoiceLabel,
    currentChoiceLabel: expected.content.currentChoiceLabel,
    publishedChoiceLabel: expected.content.publishedChoiceLabel,
  };
  const sqlCalls = [];
  const fakeSql = {
    async json(statement, parameters) {
      sqlCalls.push({ statement, parameters });
      return immutableSnapshot;
    },
  };

  const result = await runNamedAssertion("candidate-canary-write-read", context({
    phase: "candidate",
    runId: "unit-run-001",
    http: fakeHttp,
    sql: fakeSql,
  }));

  assert.equal(result.detail, `canaryId=${canaryId}`);
  assert.deepEqual(requests.map((request) => request.method), ["POST", "POST", "PUT", "PUT", "PUT", "GET"]);
  assert.equal(requests[0].token, undefined);
  assert.equal(requests[1].token, "session-token");
  assert.match(requests[1].body.slug, /^upgrade-canary-unit-run-001$/);
  assert.equal(requests[2].headers, undefined);
  assert.equal(requests[3].headers["if-match"], '"stale-canary-etag"');
  assert.equal(requests[4].headers["if-match"], createEtag);
  assert.equal(requests[5].token, expected.authentication.readerToken);
  assert.equal(requests[5].body, undefined);
  assert.equal(requests[5].method, "GET");
  assert.equal(requests[4].body.fields[0].textValue, "Upgrade canary updated unit-run-001");
  assert.equal(sqlCalls.length, 2);
  assert.deepEqual(sqlCalls[0].parameters, sqlCalls[1].parameters);
});
