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

test("rejects a generic Login heading even when the form fields are visible", async (context) => {
  const server = createServer((_, response) => {
    response.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    response.end("<!doctype html><html><body><h1>Login</h1><form action=\"/admin-auth/login\"><label for=\"email\">Email</label><input id=\"email\" name=\"email\"><label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\"></form></body></html>");
  });
  await new Promise((resolveListen, rejectListen) => server.listen(0, "127.0.0.1", (error) => error ? rejectListen(error) : resolveListen()));
  context.after(() => new Promise((resolveClose, rejectClose) => server.close((error) => error ? rejectClose(error) : resolveClose())));
  const { port } = server.address();

  await assert.rejects(
    () => scan(`http://127.0.0.1:${port}/login`, { browser: chromium, browserOptions: { headless: true }, loginUiTimeoutMs: 100 }),
    /Timeout.*exceeded/i,
  );
});

test("waits for the delayed real login heading, form action, and credential fields", async (context) => {
  const server = createServer((_, response) => {
    response.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    response.end("<!doctype html><html><body><main id=\"app\"></main><script>setTimeout(() => { document.querySelector('#app').innerHTML = '<h1>Sign in to Cmsify</h1><form method=\"post\" action=\"/admin-auth/login\"><label for=\"email\">Email</label><input id=\"email\" name=\"email\" type=\"email\"><label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\"></form>'; }, 50);</script></body></html>");
  });
  await new Promise((resolveListen, rejectListen) => server.listen(0, "127.0.0.1", (error) => error ? rejectListen(error) : resolveListen()));
  context.after(() => new Promise((resolveClose, rejectClose) => server.close((error) => error ? rejectClose(error) : resolveClose())));
  const { port } = server.address();

  const result = await scan(`http://127.0.0.1:${port}/login`, { browser: chromium, browserOptions: { headless: true }, loginUiTimeoutMs: 1_000 });
  assert.ok(Array.isArray(result.violations));
});
