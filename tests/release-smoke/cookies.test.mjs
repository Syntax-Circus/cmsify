import assert from "node:assert/strict";
import test from "node:test";

import { CookieJar } from "../../eng/release-smoke/http.mjs";

function responseHeaders(...setCookies) {
  return { getSetCookie: () => [...setCookies] };
}

test("secure cookies received over HTTP are rejected and never manually replayed", () => {
  const jar = new CookieJar({ now: () => new Date("2026-08-29T12:00:00Z") });

  jar.absorb("http://admin.release-smoke.invalid/login", responseHeaders(
    "cmsify.admin.auth=unsafe; Path=/; Secure; HttpOnly; SameSite=Lax",
  ));

  assert.equal(jar.header("http://admin.release-smoke.invalid/workspaces"), "");
  assert.equal(jar.header("https://admin.release-smoke.invalid/workspaces"), "");
});

test("cookie replay is isolated by origin host and constrained by Domain and Path", () => {
  const jar = new CookieJar({ now: () => new Date("2026-08-29T12:00:00Z") });
  jar.absorb("https://admin.release-smoke.invalid/login", responseHeaders(
    "hostOnly=one; Path=/; Secure; SameSite=None",
    "adminPath=two; Domain=admin.release-smoke.invalid; Path=/workspaces; Secure; SameSite=Lax",
    "foreign=bad; Domain=issuer.release-smoke.invalid; Path=/; Secure; SameSite=None",
  ));

  assert.match(jar.header("https://admin.release-smoke.invalid/workspaces"), /hostOnly=one/);
  assert.match(jar.header("https://admin.release-smoke.invalid/workspaces"), /adminPath=two/);
  assert.equal(jar.header("https://admin.release-smoke.invalid/login"), "hostOnly=one");
  assert.equal(jar.header("https://issuer.release-smoke.invalid/authorize"), "");
});

test("expired, deleted, and insecure SameSite=None cookies are not replayed", () => {
  let instant = new Date("2026-08-29T12:00:00Z");
  const jar = new CookieJar({ now: () => instant });
  const url = "https://admin.release-smoke.invalid/login";
  jar.absorb(url, responseHeaders(
    "short=one; Path=/; Secure; Max-Age=1; SameSite=Lax",
    "expired=two; Path=/; Secure; Expires=Thu, 01 Jan 1970 00:00:00 GMT",
    "noneWithoutSecure=three; Path=/; SameSite=None",
  ));

  assert.equal(jar.header("https://admin.release-smoke.invalid/"), "short=one");
  instant = new Date("2026-08-29T12:00:02Z");
  assert.equal(jar.header("https://admin.release-smoke.invalid/"), "");

  jar.absorb(url, responseHeaders("session=live; Path=/; Secure; SameSite=Lax"));
  jar.absorb(url, responseHeaders("session=; Path=/; Secure; Max-Age=0"));
  assert.equal(jar.header("https://admin.release-smoke.invalid/"), "");
});

test("SameSite Strict and Lax cookies respect cross-site navigation context", () => {
  const jar = new CookieJar({ now: () => new Date("2026-08-29T12:00:00Z") });
  const admin = "https://admin.release-smoke.invalid/login";
  jar.absorb(admin, responseHeaders(
    "strict=one; Path=/; Secure; SameSite=Strict",
    "lax=two; Path=/; Secure; SameSite=Lax",
  ));
  const callback = "https://admin.release-smoke.invalid/signin-oidc";
  const crossSite = { initiatorUrl: "https://issuer.release-smoke.invalid/authorize", topLevelNavigation: true };

  assert.equal(jar.header(callback, { ...crossSite, method: "GET" }), "lax=two");
  assert.equal(jar.header(callback, { ...crossSite, method: "POST" }), "");
});
