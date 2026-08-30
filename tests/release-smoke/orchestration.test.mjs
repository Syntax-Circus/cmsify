import assert from "node:assert/strict";
import { parse } from "node:path";
import test from "node:test";

import {
  RELEASE_SMOKE_SCENARIOS,
  certifyRelease,
  createDockerAdapter,
} from "../../eng/release-smoke/harness.mjs";
import { createReleaseHttpAdapter, retryBounded } from "../../eng/release-smoke/http.mjs";
import { parseCliArguments } from "../../eng/release-smoke/cli.mjs";

const options = Object.freeze({
  apiImage: "syntaxcircus/cmsify-api:1.2.3",
  adminImage: "syntaxcircus/cmsify-admin:1.2.3",
  version: "1.2.3",
  sourceSha: "0123456789abcdef0123456789abcdef01234567",
  output: "artifacts/release-smoke/unit",
  runId: "cmsify-smoke-1234abcd",
});

test("CLI accepts only the complete certify interface and rejects duplicates or unknown flags", () => {
  assert.deepEqual(parseCliArguments([
    "certify",
    "--api-image", options.apiImage,
    "--admin-image", options.adminImage,
    "--version", options.version,
    "--source-sha", options.sourceSha,
    "--output", options.output,
  ]), {
    apiImage: options.apiImage,
    adminImage: options.adminImage,
    version: options.version,
    sourceSha: options.sourceSha,
    output: options.output,
  });
  assert.throws(() => parseCliArguments(["certify", "--api-image", options.apiImage]), /required/i);
  assert.throws(() => parseCliArguments([
    "certify", "--api-image", options.apiImage, "--api-image", options.apiImage,
    "--admin-image", options.adminImage, "--version", options.version, "--source-sha", options.sourceSha, "--output", options.output,
  ]), /duplicate/i);
  assert.throws(() => parseCliArguments(["certify", "--mystery", "value"]), /unknown/i);
});

function successfulAdapters(events, { failureAt } = {}) {
  const scenario = async (name, value = {}) => {
    events.push(name);
    if (failureAt === name) {
      throw new Error(`failed ${name} with cmsify_SENTINEL_API_TOKEN and whsec_SENTINEL_WEBHOOK_SECRET`);
    }
    return value;
  };

  const candidates = {
    api: {
      reference: options.apiImage,
      imageId: `sha256:${"a".repeat(64)}`,
      digest: `sha256:${"b".repeat(64)}`,
      version: options.version,
      sourceSha: options.sourceSha,
    },
    admin: {
      reference: options.adminImage,
      imageId: `sha256:${"c".repeat(64)}`,
      digest: `sha256:${"d".repeat(64)}`,
      version: options.version,
      sourceSha: options.sourceSha,
    },
  };

  return {
    docker: {
      inspectCandidates: () => scenario("descriptor-label-identity", candidates),
      prepareFoundation: async ({ onFirstResource }) => {
        events.push("first-resource-created");
        onFirstResource();
        events.push("second-resource-created");
        return scenario("postgresql-readiness", { attempts: 2 });
      },
      restartCandidates: ({ candidates: actual }) => {
        assert.deepEqual(actual, candidates);
        return scenario("graceful-restart-persistence");
      },
      backup: () => scenario("matched-backup", {
        postgresSha256: "1".repeat(64),
        mediaSha256: "2".repeat(64),
      }),
      destructiveCanary: () => scenario("destructive-canary", { destroyed: true }),
      restoreFresh: () => scenario("fresh-restore", {
        volumes: ["cmsify-smoke-1234abcd-restore-postgres", "cmsify-smoke-1234abcd-restore-media"],
      }),
      verifyRestoredState: () => scenario("restored-state-verification"),
      captureLogs: async ({ maxLines, maxBytes }) => {
        assert.equal(maxLines, 200);
        assert.equal(maxBytes, 256 * 1024);
        events.push("logs");
      },
      cleanup: async ({ runId }) => {
        assert.equal(runId, options.runId);
        events.push("cleanup");
      },
    },
    http: {
      waitForApi: ({ maxAttempts }) => {
        assert.equal(maxAttempts, 30);
        return scenario("api-live-ready");
      },
      waitForAdmin: ({ maxAttempts }) => {
        assert.equal(maxAttempts, 30);
        return scenario("admin-static-assets");
      },
      localLogin: () => scenario("local-login", { token: "cmsify_SENTINEL_API_TOKEN" }),
      apiClientAuth: () => scenario("workspace-api-client-auth", { apiClientToken: "cmsify_SENTINEL_CLIENT_TOKEN" }),
      templateContentCrud: () => scenario("template-content-crud-etag"),
      mediaRoundTrip: () => scenario("media-upload-download"),
      oidcFlow: () => scenario("oidc-api-admin-token-forwarding"),
      webhookDelivery: () => scenario("webhook-delivery"),
      scheduledPublication: () => scenario("scheduled-publication"),
    },
    candidates,
  };
}

test("runs the mandatory scenarios in exact order and registers cleanup at the first resource", async () => {
  const events = [];
  const reports = [];
  const { docker, http } = successfulAdapters(events);

  const result = await certifyRelease(options, {
    docker,
    http,
    registerCleanup(cleanup) {
      assert.equal(typeof cleanup, "function");
      events.push("cleanup-registered");
      return () => events.push("cleanup-unregistered");
    },
    evidenceWriter: async (report) => reports.push(structuredClone(report)),
  });

  const scenarioEvents = events.filter((value) => RELEASE_SMOKE_SCENARIOS.includes(value));
  assert.deepEqual(scenarioEvents, RELEASE_SMOKE_SCENARIOS);
  assert.ok(events.indexOf("first-resource-created") < events.indexOf("cleanup-registered"));
  assert.ok(events.indexOf("cleanup-registered") < events.indexOf("second-resource-created"));
  assert.ok(events.indexOf("matched-backup") < events.indexOf("destructive-canary"));
  assert.ok(events.indexOf("destructive-canary") < events.indexOf("fresh-restore"));
  assert.equal(events.at(-2), "cleanup");
  assert.equal(events.at(-1), "cleanup-unregistered");
  assert.equal(result.status, "passed");
  assert.equal(reports.at(-1).status, "passed");
  assert.deepEqual(result.scenarios.map(({ name }) => name), RELEASE_SMOKE_SCENARIOS);
  assert.ok(result.scenarios.every(({ status }) => status === "passed"));
});

test("validates every CLI boundary before inspecting images or creating resources", async () => {
  const calls = [];
  const { docker, http } = successfulAdapters(calls);

  await assert.rejects(
    certifyRelease({ ...options, sourceSha: "short" }, { docker, http }),
    /source SHA/i,
  );
  await assert.rejects(
    certifyRelease({ ...options, apiImage: "https://registry.test/image:tag" }, { docker, http }),
    /API image/i,
  );
  await assert.rejects(
    certifyRelease({ ...options, output: "" }, { docker, http }),
    /output/i,
  );
  await assert.rejects(
    certifyRelease({ ...options, output: parse(process.cwd()).root }, { docker, http, evidenceWriter: async () => {} }),
    /filesystem root/i,
  );
  assert.deepEqual(calls, []);
});

test("fails closed when restore reports either destroyed data volume as its target", async () => {
  const events = [];
  const reports = [];
  const { docker, http } = successfulAdapters(events);
  docker.destructiveCanary = async () => {
    events.push("destructive-canary");
    return { destroyed: true, volumes: ["cmsify-smoke-1234abcd-postgres-data", "cmsify-smoke-1234abcd-media-data"] };
  };
  docker.restoreFresh = async () => {
    events.push("fresh-restore");
    return { volumes: ["cmsify-smoke-1234abcd-postgres-data", "cmsify-smoke-1234abcd-restore-media-data"] };
  };

  await assert.rejects(
    certifyRelease(options, {
      docker,
      http,
      registerCleanup: () => () => {},
      evidenceWriter: async (report) => reports.push(report),
    }),
    /release smoke failed/i,
  );
  assert.equal(reports.at(-1).failure.scenario, "fresh-restore");
  assert.equal(events.filter((event) => event === "cleanup").length, 1);
});

for (const failureAt of RELEASE_SMOKE_SCENARIOS) {
  test(`captures bounded logs, sanitized evidence, and cleanup when ${failureAt} fails`, async () => {
    const events = [];
    const reports = [];
    const { docker, http } = successfulAdapters(events, { failureAt });

    await assert.rejects(
      certifyRelease(options, {
        docker,
        http,
        registerCleanup: () => () => {},
        evidenceWriter: async (report) => reports.push(structuredClone(report)),
        redactions: ["cmsify_SENTINEL_API_TOKEN", "whsec_SENTINEL_WEBHOOK_SECRET"],
      }),
      /release smoke failed/i,
    );

    assert.equal(events.filter((value) => value === "logs").length, 1);
    assert.equal(events.filter((value) => value === "cleanup").length, 1);
    const terminal = reports.at(-1);
    assert.equal(terminal.status, "failed");
    assert.equal(terminal.failure.scenario, failureAt);
    assert.equal(terminal.scenarios.find(({ name }) => name === failureAt).status, "failed");
    const serialized = JSON.stringify(terminal);
    assert.doesNotMatch(serialized, /SENTINEL|cmsify_[A-Za-z0-9_-]+|whsec_[A-Za-z0-9_-]+/);
  });
}

test("bounded retries stop after the configured attempt count", async () => {
  let attempts = 0;
  await assert.rejects(
    retryBounded(async () => {
      attempts += 1;
      throw new Error("not ready");
    }, { maxAttempts: 4, delayMs: 1, sleep: async () => {} }),
    /not ready/,
  );
  assert.equal(attempts, 4);
});

test("Docker candidate commands use already-loaded immutable IDs and never build or pull candidates", async () => {
  const calls = [];
  const inspect = (reference, id, digest) => ({
    stdout: JSON.stringify({
      Id: id,
      Os: "linux",
      Architecture: "amd64",
      RepoDigests: [`${reference.split(":")[0]}@${digest}`],
      Config: { Labels: {
        "org.opencontainers.image.version": options.version,
        "org.opencontainers.image.revision": options.sourceSha,
      } },
    }),
    stderr: "",
    exitCode: 0,
  });
  const run = async (command, args) => {
    calls.push([command, ...args]);
    if (args.includes(options.apiImage)) return inspect(options.apiImage, `sha256:${"a".repeat(64)}`, `sha256:${"b".repeat(64)}`);
    if (args.includes(options.adminImage)) return inspect(options.adminImage, `sha256:${"c".repeat(64)}`, `sha256:${"d".repeat(64)}`);
    if (args[0] === "port") return { stdout: "127.0.0.1:32768\n", stderr: "", exitCode: 0 };
    return { stdout: "", stderr: "", exitCode: 0 };
  };
  const docker = createDockerAdapter({ run, repositoryRoot: process.cwd() });
  const candidates = await docker.inspectCandidates(options);
  await docker.startCandidates({ ...options, candidates, runId: options.runId }, { restored: false });

  assert.equal(calls.some(([, first]) => first === "build" || first === "pull"), false);
  const candidateRuns = calls.filter((call) => call[1] === "run" && call.some((arg) => arg === candidates.api.imageId || arg === candidates.admin.imageId));
  assert.equal(candidateRuns.length, 2);
  assert.ok(candidateRuns.every((call) => call.includes("--pull") && call.includes("never")));
});

test("Docker cleanup refuses a correctly-labelled resource whose name is outside the validated run scope", async () => {
  const calls = [];
  const run = async (command, args) => {
    calls.push([command, ...args]);
    if (args[0] === "ps") return { stdout: "foreign-id\n", stderr: "", exitCode: 0 };
    if (args[0] === "container" && args[1] === "inspect") {
      return {
        stdout: JSON.stringify({ Name: "/some-other-run-api", Config: { Labels: {
          "io.syntaxcircus.cmsify.release-smoke": "true",
          "io.syntaxcircus.cmsify.release-smoke-run": options.runId,
        } } }),
        stderr: "",
        exitCode: 0,
      };
    }
    return { stdout: "", stderr: "", exitCode: 0 };
  };
  const docker = createDockerAdapter({ run, repositoryRoot: process.cwd() });

  await assert.rejects(docker.cleanup({ runId: options.runId }), /outside the validated run scope/i);
  assert.equal(calls.some((call) => call[1] === "rm" && call.includes("foreign-id")), false);
});

test("the real HTTP adapter uses health/static probes and conditional template/content CRUD contracts", async () => {
  const calls = [];
  const templateId = "11111111-1111-4111-8111-111111111111";
  const templateVersionId = "22222222-2222-4222-8222-222222222222";
  const contentId = "33333333-3333-4333-8333-333333333333";
  const respond = (value, { status = 200, headers = {} } = {}) => {
    const text = typeof value === "string" ? value : JSON.stringify(value);
    return { status, headers: new Headers(headers), bytes: Buffer.from(text), text, json: () => JSON.parse(text) };
  };
  const request = async (input) => {
    calls.push(structuredClone({ url: input.url, method: input.method ?? "GET", headers: input.headers ?? {}, body: input.body }));
    const { pathname } = new URL(input.url);
    if (pathname === "/health/live" || pathname === "/health/ready") return respond({ status: "Healthy" });
    if (pathname === "/") return respond('<html><title>Cmsify Admin</title><script src="/_framework/blazor.web.js"></script></html>');
    if (pathname === "/_framework/blazor.web.js") return respond("globalThis.Blazor={};");
    if (pathname.endsWith("/templates") && input.method === "POST") return respond({ id: templateId, currentVersion: { id: templateVersionId, versionNumber: 1 } }, { status: 201, headers: { etag: '"template-1"' } });
    if (pathname.endsWith(`/templates/${templateId}`) && input.method === "GET") return respond({ id: templateId }, { headers: { etag: '"template-1"' } });
    if (pathname.endsWith(`/templates/${templateId}`) && input.method === "PUT") return respond({ id: templateId }, { headers: { etag: '"template-2"' } });
    if (pathname.endsWith(`/templates/${templateId}/versions/1/publish`) && input.method === "PUT") return respond({ id: templateVersionId, status: "Published" });
    if (pathname.endsWith("/content") && input.method === "POST") return respond({ id: contentId }, { status: 201, headers: { etag: '"content-1"' } });
    if (pathname.endsWith(`/content/${contentId}`) && input.method === "GET") return respond({ id: contentId, slug: "release-smoke-crud" }, { headers: { etag: '"content-1"' } });
    if (pathname.endsWith(`/content/${contentId}`) && input.method === "PUT") return respond({ id: contentId, slug: "release-smoke-crud-updated" }, { headers: { etag: '"content-2"' } });
    if (pathname.endsWith(`/content/${contentId}`) && input.method === "DELETE") return respond("", { status: 204 });
    if (pathname.endsWith(`/templates/${templateId}`) && input.method === "DELETE") return respond("", { status: 204 });
    throw new Error(`unexpected ${input.method ?? "GET"} ${pathname}`);
  };
  const http = createReleaseHttpAdapter({ request, sleep: async () => {} });
  const context = {
    runtime: { apiBase: "http://api.test", adminBase: "http://admin.test", localToken: "cmsify_test", workspaceId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" },
    artifacts: {},
  };

  await http.waitForApi({ ...context, maxAttempts: 2 });
  await http.waitForAdmin({ ...context, maxAttempts: 2 });
  const result = await http.templateContentCrud(context);

  assert.equal(result.deletedContentId, contentId);
  const conditional = calls.filter((call) => call.headers["if-match"] || call.headers["If-Match"]);
  assert.deepEqual(conditional.map((call) => call.headers["if-match"] ?? call.headers["If-Match"]), ['"template-1"', '"content-1"', '"content-2"', '"template-2"']);
  assert.equal(calls.some((call) => new URL(call.url).pathname === "/_framework/blazor.web.js"), true);
});
