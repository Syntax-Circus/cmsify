import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import test from "node:test";

import { createDockerHttpAdapter, HTTP_LIMITS, requestJson } from "../../../eng/upgrade-tests/http.mjs";

function dockerFixture({ status = 200, body = Buffer.from("{}"), headers, bodySize = body.length, onCurl, onCleanup } = {}) {
  const responseHeaders = headers ?? `HTTP/1.1 ${status} Fixture\r\nContent-Type: application/json\r\n\r\n`;
  const calls = [];
  return {
    calls,
    harness: {
      async exec(service, args, options = {}) {
        calls.push({ service, args, options });
        if (args[0] === "curl") {
          if (onCurl) return onCurl(options);
          return { exitCode: 0, stdout: String(status), stderr: "", durationMs: 0 };
        }
        if (args[0] === "wc") {
          const size = args.at(-1).endsWith(".headers") ? Buffer.byteLength(responseHeaders) : bodySize;
          return { exitCode: 0, stdout: `${size} ${args.at(-1)}\n`, stderr: "", durationMs: 0 };
        }
        if (args[0] === "cat") {
          const stdout = args.at(-1).endsWith(".headers") ? responseHeaders : Buffer.from(body);
          return { exitCode: 0, stdout, stderr: "", durationMs: 0 };
        }
        if (args[0] === "sha256sum") {
          return { exitCode: 0, stdout: `${createHash("sha256").update(body).digest("hex")}  response\n`, stderr: "", durationMs: 0 };
        }
        if (args[0] === "rm") {
          if (onCleanup) return onCleanup(options);
          return { exitCode: 0, stdout: "", stderr: "", durationMs: 0 };
        }
        throw new Error(`unexpected Docker fixture command ${args[0]}`);
      },
    },
  };
}

function requestWithThrowingGetter(field, marker) {
  const request = {
    url: "https://cmsify.test/api/v1/content?secret=query-secret",
    method: "POST",
    headers: { "x-fixture": "fixture-header" },
    token: "cmsify_fixture-token",
    body: { title: "Fixture" },
    expectedStatuses: new Set([200]),
    fetchImpl: async () => new Response("{}", { status: 200 }),
    signalFactory: () => new AbortController().signal,
    signal: new AbortController().signal,
  };
  Object.defineProperty(request, field, {
    enumerable: true,
    get() {
      throw new Error(marker);
    },
  });
  return request;
}

test("Docker JSON requests disable ambient curl behavior and preserve sanitized strict-status diagnostics", async () => {
  const token = "cmsify_docker-secret-token";
  const bodySecret = "docker-response-body-secret";
  const fixture = dockerFixture({
    status: 500,
    headers: "HTTP/1.1 500 Failure\r\nContent-Type: application/problem+json\r\nX-Correlation-ID: correlation-docker-001\r\n\r\n",
    body: Buffer.from(JSON.stringify({ traceId: "trace-docker-001", detail: bodySecret })),
  });
  const adapter = createDockerHttpAdapter(fixture.harness, "baseline-api");

  await assert.rejects(
    () => adapter.requestJson({
      url: "http://localhost:8080/api/v1/content?secret=query-secret",
      method: "POST",
      token,
      body: { secret: "request-body-secret" },
      expectedStatuses: new Set([201]),
    }),
    (error) => {
      assert.match(error.message, /POST \/api\/v1\/content.*status 500/);
      assert.match(error.message, /correlation-docker-001/);
      assert.match(error.message, /trace-docker-001/);
      assert.doesNotMatch(error.message, /query-secret|request-body-secret|docker-response-body-secret|cmsify_docker-secret-token/);
      return true;
    },
  );

  const curl = fixture.calls.find(({ args }) => args[0] === "curl");
  assert.equal(curl.args[1], "--disable");
  assert.deepEqual(curl.args.slice(curl.args.indexOf("--max-time"), curl.args.indexOf("--max-time") + 2), ["--max-time", "5"]);
  assert.deepEqual(curl.args.slice(curl.args.indexOf("--max-redirs"), curl.args.indexOf("--max-redirs") + 2), ["--max-redirs", "0"]);
  assert.deepEqual(curl.args.slice(curl.args.indexOf("--max-filesize"), curl.args.indexOf("--max-filesize") + 2), ["--max-filesize", String(HTTP_LIMITS.maximumJsonBytes)]);
  assert.equal(curl.args.includes("--location"), false);
  assert.equal(curl.args.includes("--netrc"), false);
  assert.equal(curl.options.timeoutMs, HTTP_LIMITS.requestTimeoutMs);
  assert.ok(curl.options.signal instanceof AbortSignal);
});

test("Docker JSON and byte requests reject responses over their exact caps before parsing or hashing", async () => {
  const jsonFixture = dockerFixture({ bodySize: HTTP_LIMITS.maximumJsonBytes + 1 });
  const byteFixture = dockerFixture({ bodySize: HTTP_LIMITS.maximumByteResponseBytes + 1 });

  await assert.rejects(
    () => createDockerHttpAdapter(jsonFixture.harness, "baseline-api").requestJson({
      url: "http://localhost:8080/oversized-json",
      expectedStatuses: new Set([200]),
    }),
    /response exceeds 1 MiB/,
  );
  await assert.rejects(
    () => createDockerHttpAdapter(byteFixture.harness, "baseline-api").requestBytes({
      url: "http://localhost:8080/oversized-bytes",
      expectedStatuses: new Set([200]),
    }),
    /response exceeds 10 MiB/,
  );

  assert.equal(byteFixture.calls.some(({ args }) => args[0] === "sha256sum"), false);
});

test("Docker JSON requests preserve caller cancellation and sanitize execution failures", async () => {
  const controller = new AbortController();
  const executionSecret = "docker-execution-secret";
  const fixture = dockerFixture({
    onCurl(options) {
      return new Promise((_resolve, reject) => {
        options.signal.addEventListener("abort", () => reject(new Error(executionSecret)), { once: true });
      });
    },
  });
  const pending = createDockerHttpAdapter(fixture.harness, "baseline-api").requestJson({
    url: "http://localhost:8080/slow",
    expectedStatuses: new Set([200]),
    signal: controller.signal,
  });
  controller.abort(new Error(executionSecret));

  await assert.rejects(pending, (error) => {
    assert.match(error.message, /GET \/slow.*transport error/);
    assert.doesNotMatch(error.message, new RegExp(executionSecret));
    return true;
  });
});

test("Docker request cleanup stays inside the caller cancellation boundary without masking the primary failure", async () => {
  const controller = new AbortController();
  const primarySecret = "docker-primary-failure-secret";
  const cleanupSecret = "docker-cleanup-failure-secret";
  let curlSignal;
  let cleanupOptions;
  let cleanupStartedResolve;
  const cleanupStarted = new Promise((resolve) => { cleanupStartedResolve = resolve; });
  const fixture = dockerFixture({
    onCurl(options) {
      curlSignal = options.signal;
      throw new Error(primarySecret);
    },
    onCleanup(options) {
      cleanupOptions = options;
      cleanupStartedResolve();
      return new Promise((_resolve, reject) => {
        if (options.signal?.aborted) reject(new Error(cleanupSecret));
        else options.signal?.addEventListener("abort", () => reject(new Error(cleanupSecret)), { once: true });
      });
    },
  });
  const pending = createDockerHttpAdapter(fixture.harness, "baseline-api").requestJson({
    url: "http://localhost:8080/cleanup",
    expectedStatuses: new Set([200]),
    signal: controller.signal,
  });
  await cleanupStarted;
  controller.abort(new Error(cleanupSecret));

  let timeout;
  const outcome = await Promise.race([
    pending.then(
      () => ({ status: "resolved" }),
      (error) => ({ status: "rejected", error }),
    ),
    new Promise((resolve) => { timeout = setTimeout(() => resolve({ status: "stalled" }), 100); }),
  ]);
  clearTimeout(timeout);

  assert.equal(outcome.status, "rejected", "caller cancellation must interrupt best-effort cleanup");
  assert.match(outcome.error.message, /GET \/cleanup.*transport error/);
  assert.doesNotMatch(outcome.error.message, new RegExp(`${primarySecret}|${cleanupSecret}`));
  assert.equal(cleanupOptions.signal, curlSignal, "cleanup must use the exchange's shared signal");
  assert.equal(cleanupOptions.signal.aborted, true);
});

test("fetch request preparation sanitizes every throwing request-field getter", async () => {
  const fields = ["url", "method", "headers", "token", "body", "expectedStatuses", "fetchImpl", "signalFactory", "signal"];
  for (const field of fields) {
    const marker = `fetch-${field}-getter-secret`;
    await assert.rejects(
      () => requestJson(requestWithThrowingGetter(field, marker)),
      (error) => {
        assert.match(error.message, /request preparation error/);
        assert.doesNotMatch(error.message, new RegExp(marker));
        return true;
      },
      `throwing ${field} getter must be sanitized`,
    );
  }
});

test("Docker request preparation sanitizes every throwing request-field getter", async () => {
  const fields = ["url", "method", "headers", "token", "body", "expectedStatuses", "signalFactory", "signal"];
  for (const field of fields) {
    const marker = `docker-${field}-getter-secret`;
    const fixture = dockerFixture();
    await assert.rejects(
      () => createDockerHttpAdapter(fixture.harness, "baseline-api").requestJson(requestWithThrowingGetter(field, marker)),
      (error) => {
        assert.match(error.message, /request preparation error/);
        assert.doesNotMatch(error.message, new RegExp(marker));
        return true;
      },
      `throwing ${field} getter must be sanitized`,
    );
  }
});

test("Docker JSON requests reject malformed UTF-8 without exposing response bytes", async () => {
  const fixture = dockerFixture({ body: Buffer.from([0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xff, 0x22, 0x7d]) });

  await assert.rejects(
    () => createDockerHttpAdapter(fixture.harness, "baseline-api").requestJson({
      url: "http://localhost:8080/malformed",
      expectedStatuses: new Set([200]),
    }),
    (error) => {
      assert.match(error.message, /GET \/malformed.*invalid JSON response/);
      assert.doesNotMatch(error.message, /�/);
      return true;
    },
  );
});
