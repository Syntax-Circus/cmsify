import assert from "node:assert/strict";
import test from "node:test";

import { createReleaseHttpAdapter, requestHttp } from "../../eng/release-smoke/http.mjs";

test("chunked responses without Content-Length are cancelled at maximum plus one byte", async () => {
  let cancelled = false;
  let pulls = 0;
  const body = new ReadableStream({
    pull(controller) {
      pulls += 1;
      controller.enqueue(Uint8Array.from(pulls === 1 ? [1, 2, 3, 4] : [5, 6, 7, 8]));
    },
    cancel() {
      cancelled = true;
    },
  }, { highWaterMark: 0 });
  const fetchImpl = async () => new Response(body, { status: 200 });

  await assert.rejects(
    requestHttp({ url: "https://api.release-smoke.invalid/chunked", maxBytes: 5, fetchImpl }),
    /exceeded 5 bytes/i,
  );
  assert.equal(pulls, 2);
  assert.equal(cancelled, true);
});

test("the shared abort signal is forwarded to every real HTTP request and retry", async () => {
  const controller = new AbortController();
  const observed = [];
  const adapter = createReleaseHttpAdapter({
    signal: controller.signal,
    request: async (input) => {
      observed.push(input.signal);
      return {
        status: 200,
        headers: { get: () => null, getSetCookie: () => [] },
        bytes: Buffer.from("ok"),
        text: "ok",
        json: () => ({ ok: true }),
      };
    },
    sleep: async () => {},
  });

  await adapter.waitForApi({ runtime: { apiBase: "http://api.release-smoke.invalid" }, maxAttempts: 1 });

  assert.deepEqual(observed, [controller.signal, controller.signal]);
});
