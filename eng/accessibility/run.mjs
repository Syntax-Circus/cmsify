#!/usr/bin/env node
import { createRequire } from "node:module";
import { mkdir, writeFile } from "node:fs/promises";
import { parse, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { chromium } from "playwright";

const require = createRequire(import.meta.url);
const AXE_PATH = require.resolve("axe-core/axe.min.js");
const WCAG_TAGS = Object.freeze(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]);
const WAIT_TIMEOUT_MS = 60_000;
const NAVIGATION_TIMEOUT_MS = 30_000;
const AXE_TIMEOUT_MS = 60_000;
const MAX_VIOLATIONS = 100;
const MAX_NODES = 20;
const MAX_TARGETS = 8;
const MAX_TEXT = 512;
const MAX_REPORT_BYTES = 1024 * 1024;

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index === -1 || !process.argv[index + 1] || process.argv[index + 1].startsWith("--")) {
    throw new Error(`Required accessibility argument ${name} is missing.`);
  }
  if (process.argv.indexOf(name, index + 1) !== -1) throw new Error(`Accessibility argument ${name} was supplied more than once.`);
  return process.argv[index + 1];
}

function boundedText(value, limit = MAX_TEXT) {
  return String(value ?? "").replace(/[\u0000-\u001f\u007f]+/g, " ").replace(/\s+/g, " ").trim().slice(0, limit);
}

function reportUrl(value) {
  const parsed = new URL(value);
  if (!["http:", "https:"].includes(parsed.protocol) || parsed.username || parsed.password || parsed.pathname !== "/login") {
    throw new Error("Accessibility URL must be an HTTP(S) /login endpoint without credentials.");
  }
  return `${parsed.origin}/login`;
}

function outputDirectory(value) {
  if (!value || /[\0\r\n]/.test(value)) throw new Error("Accessibility output directory is invalid.");
  const directory = resolve(value);
  if (directory === parse(directory).root) throw new Error("Accessibility output directory cannot be a filesystem root.");
  return directory;
}

function sleep(milliseconds) {
  return new Promise((resolveSleep) => setTimeout(resolveSleep, milliseconds));
}

async function waitForLogin(url, waitTimeoutMs = WAIT_TIMEOUT_MS) {
  const deadline = Date.now() + waitTimeoutMs;
  let lastStatus = "unreachable";
  while (Date.now() < deadline) {
    const requestTimeout = Math.max(1, Math.min(5_000, deadline - Date.now()));
    try {
      const response = await fetch(url, { redirect: "follow", signal: AbortSignal.timeout(requestTimeout) });
      lastStatus = `HTTP ${response.status}`;
      await response.body?.cancel();
      if (response.ok) return;
    } catch (error) {
      lastStatus = boundedText(error?.name || "request failed", 64);
    }
    await sleep(Math.min(1_000, Math.max(1, deadline - Date.now())));
  }
  throw new Error(`Admin /login did not become ready within ${waitTimeoutMs}ms (${lastStatus}).`);
}

export async function waitForLoginUi(page, timeout = WAIT_TIMEOUT_MS) {
  await page.getByRole("heading", { name: /login/i }).waitFor({ state: "visible", timeout });
  await page.locator("#email, input[name='email']").waitFor({ state: "visible", timeout });
  await page.locator("#password, input[name='password']").waitFor({ state: "visible", timeout });
  await page.locator("form").filter({ has: page.locator("#email, input[name='email']") }).waitFor({ state: "visible", timeout });
}

function sanitizeViolation(violation) {
  return Object.freeze({
    id: boundedText(violation.id, 128),
    impact: boundedText(violation.impact, 32) || null,
    help: boundedText(violation.help, 256),
    helpUrl: /^https:\/\/dequeuniversity\.com\/rules\//.test(violation.helpUrl ?? "") ? boundedText(violation.helpUrl, 512) : null,
    nodes: Object.freeze((violation.nodes ?? []).slice(0, MAX_NODES).map((node) => Object.freeze({
      impact: boundedText(node.impact, 32) || null,
      target: Object.freeze((node.target ?? []).slice(0, MAX_TARGETS).map((target) => boundedText(target, 256))),
      failureSummary: boundedText(node.failureSummary, 512),
    }))),
    totalNodes: Array.isArray(violation.nodes) ? violation.nodes.length : 0,
  });
}

function xml(value) {
  return String(value).replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&apos;");
}

function junit(report) {
  const failure = report.status === "passed" ? "" : `\n    <failure message="${xml(report.failure?.message ?? `${report.summary.violations} accessibility violations`)}" type="accessibility">${xml(report.failure?.code ?? report.violations.map((violation) => violation.id).join(", "))}</failure>`;
  return `<?xml version="1.0" encoding="UTF-8"?>\n<testsuites tests="1" failures="${report.status === "passed" ? 0 : 1}">\n  <testsuite name="Cmsify Admin accessibility" tests="1" failures="${report.status === "passed" ? 0 : 1}">\n  <testcase classname="Cmsify.Admin" name="GET /login WCAG 2.0/2.1 A/AA">${failure}\n  </testcase>\n  </testsuite>\n</testsuites>\n`;
}

async function writeEvidence(directory, report) {
  await mkdir(directory, { recursive: true });
  const json = `${JSON.stringify(report, null, 2)}\n`;
  const junitXml = junit(report);
  if (Buffer.byteLength(json) > MAX_REPORT_BYTES || Buffer.byteLength(junitXml) > MAX_REPORT_BYTES) {
    throw new Error("Sanitized accessibility evidence exceeded its bounded report size.");
  }
  await Promise.all([
    writeFile(resolve(directory, "accessibility.json"), json, { encoding: "utf8", mode: 0o600 }),
    writeFile(resolve(directory, "accessibility.junit.xml"), junitXml, { encoding: "utf8", mode: 0o600 }),
  ]);
}

export async function scan(url, {
  browser = chromium,
  browserOptions = { headless: true, channel: process.env.CMSIFY_ACCESSIBILITY_BROWSER_CHANNEL ?? "chrome" },
  loginUiTimeoutMs = WAIT_TIMEOUT_MS,
} = {}) {
  await waitForLogin(url);
  const browserInstance = await browser.launch(browserOptions);
  try {
    const context = await browserInstance.newContext();
    const page = await context.newPage();
    page.setDefaultNavigationTimeout(NAVIGATION_TIMEOUT_MS);
    page.setDefaultTimeout(AXE_TIMEOUT_MS);
    await page.goto(url, { waitUntil: "domcontentloaded", timeout: NAVIGATION_TIMEOUT_MS });
    if (new URL(page.url()).pathname !== "/login") throw new Error("Accessibility navigation did not remain on /login.");
    await page.locator("body").waitFor({ state: "attached", timeout: NAVIGATION_TIMEOUT_MS });
    await waitForLoginUi(page, loginUiTimeoutMs);
    await page.addScriptTag({ path: AXE_PATH });
    const evaluation = page.evaluate(async (tags) => globalThis.axe.run(document, {
      runOnly: { type: "tag", values: tags },
      resultTypes: ["violations", "incomplete", "passes", "inapplicable"],
    }), WCAG_TAGS);
    let axeTimeout;
    try {
      return await Promise.race([
        evaluation,
        new Promise((_, reject) => { axeTimeout = setTimeout(() => reject(new Error(`axe did not complete within ${AXE_TIMEOUT_MS}ms.`)), AXE_TIMEOUT_MS); }),
      ]);
    } finally {
      clearTimeout(axeTimeout);
    }
  } finally {
    await browserInstance.close();
  }
}

async function main() {
  const url = reportUrl(argument("--url"));
  const output = outputDirectory(argument("--output"));
  const checkedAt = new Date().toISOString();
  try {
    const result = await scan(url);
    const violations = (result.violations ?? []).slice(0, MAX_VIOLATIONS).map(sanitizeViolation);
    const report = Object.freeze({
      schema: "cmsify.accessibility.v1",
      status: result.violations?.length === 0 ? "passed" : "failed",
      checkedAt,
      url,
      tags: WCAG_TAGS,
      summary: Object.freeze({
        violations: result.violations?.length ?? 0,
        incomplete: result.incomplete?.length ?? 0,
        passes: result.passes?.length ?? 0,
        inapplicable: result.inapplicable?.length ?? 0,
        retainedViolations: violations.length,
      }),
      violations: Object.freeze(violations),
      failure: null,
    });
    await writeEvidence(output, report);
    if (report.status !== "passed") process.exitCode = 1;
  } catch (error) {
    const report = Object.freeze({
      schema: "cmsify.accessibility.v1",
      status: "failed",
      checkedAt,
      url,
      tags: WCAG_TAGS,
      summary: Object.freeze({ violations: 0, incomplete: 0, passes: 0, inapplicable: 0, retainedViolations: 0 }),
      violations: Object.freeze([]),
      failure: Object.freeze({ code: boundedText(error?.name || "accessibility-error", 64), message: boundedText(error?.message || "Accessibility certification failed.", 512) }),
    });
    await writeEvidence(output, report);
    process.stderr.write(`${report.failure.message}\n`);
    process.exitCode = 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
