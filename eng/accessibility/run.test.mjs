import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";

import { chromium } from "playwright";

import { scan } from "./run.mjs";

test("fails when a reachable login route does not render the required UI", async (context) => {
  const server = createServer((_, response) => {
    response.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    response.end("<!doctype html><html><body><h1>Unavailable</h1></body></html>");
  });
  await new Promise((resolveListen, rejectListen) => server.listen(0, "127.0.0.1", (error) => error ? rejectListen(error) : resolveListen()));
  context.after(() => new Promise((resolveClose, rejectClose) => server.close((error) => error ? rejectClose(error) : resolveClose())));
  const { port } = server.address();

  await assert.rejects(
    () => scan(`http://127.0.0.1:${port}/login`, { browser: chromium, browserOptions: { headless: true }, loginUiTimeoutMs: 100 }),
    /Timeout.*exceeded/i,
  );
});
