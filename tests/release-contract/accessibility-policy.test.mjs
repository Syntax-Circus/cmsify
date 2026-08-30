import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const branchWorkflow = readFileSync(resolve(repositoryRoot, ".github/workflows/admin-accessibility.yml"), "utf8");
const releaseWorkflow = readFileSync(resolve(repositoryRoot, ".github/workflows/publish-cmsify.yml"), "utf8");
const requiredPaths = [
  "src/Cmsify.Admin/**",
  "src/Cmsify.Contracts/**",
  "src/Cmsify.Core/**",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/**",
  "eng/accessibility/**",
  "Directory.Build.props",
  "Directory.Packages.props",
  "global.json",
  "Cmsify.slnx",
  ".github/workflows/admin-accessibility.yml",
];

function jobBody(workflow, name) {
  const start = workflow.search(new RegExp(`^  ${name}:`, "m"));
  if (start === -1) return "";
  const following = workflow.slice(start + 1);
  const nextJob = following.search(/^  [A-Za-z0-9_-]+:/m);
  return nextJob === -1 ? workflow.slice(start) : workflow.slice(start, start + 1 + nextJob);
}

function eventPaths(workflow, event) {
  const lines = workflow.replaceAll("\r\n", "\n").split("\n");
  const eventIndex = lines.findIndex((line) => new RegExp(`^  ${event}:\\s*$`).test(line));
  if (eventIndex === -1) return [];
  let eventEnd = lines.length;
  for (let index = eventIndex + 1; index < lines.length; index += 1) {
    if (!/^[ \t]*(?:#.*)?$/.test(lines[index]) && /^(?:[^ \t]| {2}\S)/.test(lines[index])) { eventEnd = index; break; }
  }
  const eventLines = lines.slice(eventIndex, eventEnd);
  const pathIndex = eventLines.findIndex((line) => /^    paths:[ \t]*(?:#.*)?$/.test(line));
  if (pathIndex === -1) return [];
  const entries = [];
  for (const line of eventLines.slice(pathIndex + 1)) {
    if (/^[ \t]*(?:#.*)?$/.test(line)) continue;
    if (/^(?:[^ \t]| {2}[A-Za-z0-9_.-]+:| {4}[A-Za-z0-9_.-]+:)/.test(line)) break;
    const item = /^ {6}- (.*)$/.exec(line)?.[1];
    const quoted = /^"([^"\r\n]*)"(?:[ \t]+#.*)?$/.exec(item ?? "");
    if (!quoted || quoted[1].startsWith("!")) return [];
    entries.push(quoted[1]);
  }
  return entries;
}

test("branch accessibility runs manually and for relevant main/PR changes", () => {
  assert.match(branchWorkflow, /workflow_dispatch:/);
  assert.match(branchWorkflow, /push:\s*\n\s+branches:\s*\[main\]/);
  assert.match(branchWorkflow, /pull_request:/);
  for (const path of requiredPaths) {
    assert.equal(eventPaths(branchWorkflow, "push").filter((candidate) => candidate === path).length, 1, `${path} must gate main pushes exactly once`);
    assert.equal(eventPaths(branchWorkflow, "pull_request").filter((candidate) => candidate === path).length, 1, `${path} must gate pull requests exactly once`);
  }
  assert.doesNotMatch(branchWorkflow, /tags:/);
});

test("paths-ignore is not accepted as an accessibility path trigger", () => {
  for (const event of ["push", "pull_request"]) {
    const ignoredPaths = event === "push"
      ? branchWorkflow.replace("  push:\n    branches: [main]\n    paths:", "  push:\n    branches: [main]\n    paths-ignore:")
      : branchWorkflow.replace("  pull_request:\n    paths:", "  pull_request:\n    paths-ignore:");
    assert.deepEqual(eventPaths(ignoredPaths, event), [], `${event} paths-ignore must not be treated as paths`);
  }
});

test("negative paths invalidate each accessibility event path list", () => {
  for (const event of ["push", "pull_request"]) {
    const negatedPaths = event === "push"
      ? branchWorkflow.replace('      - "src/Cmsify.Admin/**"\n', '      - "src/Cmsify.Admin/**"\n      - "!src/Cmsify.Admin/**"\n')
      : branchWorkflow.replace('  pull_request:\n    paths:\n      - "src/Cmsify.Admin/**"\n', '  pull_request:\n    paths:\n      - "src/Cmsify.Admin/**"\n      - "!src/Cmsify.Admin/**"\n');
    assert.deepEqual(eventPaths(negatedPaths, event), [], `${event} negative path must invalidate its path list`);
  }
});

test("single-quoted and separated negative paths invalidate each accessibility event path list", () => {
  for (const event of ["push", "pull_request"]) {
    const mutate = event === "push"
      ? (replacement) => branchWorkflow.replace('      - "src/Cmsify.Admin/**"\n', replacement)
      : (replacement) => branchWorkflow.replace('  pull_request:\n    paths:\n      - "src/Cmsify.Admin/**"\n', `  pull_request:\n    paths:\n${replacement}`);
    for (const [name, replacement] of [
      ["single-quoted", '      - "src/Cmsify.Admin/**"\n      - \'!src/Cmsify.Admin/**\'\n'],
      ["separated", '      - "src/Cmsify.Admin/**"\n\n      # later path entry\n      - "!src/Cmsify.Admin/**"\n'],
    ]) {
      assert.deepEqual(eventPaths(mutate(replacement), event), [], `${event} ${name} negative path must invalidate its path list`);
    }
  }
});

test("branch accessibility installs only the locked harness and emits bounded evidence", () => {
  assert.match(branchWorkflow, /npm ci --prefix eng\/accessibility/);
  assert.doesNotMatch(branchWorkflow, /npx\s+--yes|npm install(?!\s+--global)/);
  assert.match(branchWorkflow, /node eng\/accessibility\/run\.mjs[^\n]*--url http:\/\/127\.0\.0\.1:5177\/login[^\n]*--output artifacts\/accessibility/);
  assert.match(branchWorkflow, /if:\s*always\(\)[\s\S]*accessibility\.json[\s\S]*accessibility\.junit\.xml[\s\S]*retention-days:\s*14/);
});

test("accessibility package and lock pin axe and browser dependencies exactly", () => {
  const packageJson = JSON.parse(readFileSync(resolve(repositoryRoot, "eng/accessibility/package.json"), "utf8"));
  const lock = JSON.parse(readFileSync(resolve(repositoryRoot, "eng/accessibility/package-lock.json"), "utf8"));
  assert.equal(packageJson.private, true);
  assert.match(packageJson.engines?.node ?? "", /^>=20/);
  for (const name of ["axe-core", "playwright"]) {
    assert.match(packageJson.dependencies?.[name] ?? "", /^\d+\.\d+\.\d+$/, `${name} must use an exact version`);
    assert.equal(lock.packages?.[""]?.dependencies?.[name], packageJson.dependencies[name]);
    assert.equal(lock.packages?.[`node_modules/${name}`]?.version, packageJson.dependencies[name]);
    assert.match(lock.packages?.[`node_modules/${name}`]?.integrity ?? "", /^sha512-/);
  }
});

test("the harness waits and navigates with timeouts, scans /login at WCAG 2.0/2.1 A/AA, and bounds sanitized JSON/JUnit", () => {
  const runner = readFileSync(resolve(repositoryRoot, "eng/accessibility/run.mjs"), "utf8");
  for (const tag of ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]) assert.match(runner, new RegExp(`"${tag}"`));
  assert.match(runner, /\/login/);
  assert.match(runner, /WAIT_TIMEOUT_MS|waitTimeoutMs/);
  assert.match(runner, /NAVIGATION_TIMEOUT_MS|navigationTimeoutMs/);
  assert.match(runner, /getByRole\("heading", \{ name: "Sign in to Cmsify", exact: true \}\)/);
  assert.match(runner, /#password|\[name="password"\]/);
  assert.match(runner, /form\[action='\/admin-auth\/login'\]/);
  assert.match(runner, /MAX_(?:VIOLATIONS|NODES|REPORT_BYTES)/);
  assert.match(runner, /accessibility\.json/);
  assert.match(runner, /accessibility\.junit\.xml/);
  assert.doesNotMatch(runner, /\.html\b|html:\s*node\.html/i);
});

test("candidate accessibility consumes the exact downloaded Admin OCI archive without rebuilding or pulling", () => {
  const candidate = jobBody(releaseWorkflow, "candidate-accessibility");
  assert.match(candidate, /needs:\s*\[resolve, build\]/);
  assert.match(candidate, /download-artifact@[0-9a-f]{40}[\s\S]*release-candidate-/);
  assert.match(candidate, /sha256sum --check SHA256SUMS/);
  assert.match(candidate, /docker load --input artifacts\/oci\/cmsify-admin\.oci\.tar/);
  assert.match(candidate, /docker run[^\n]*--pull=never[^\n]*"docker\.io\/syntaxcircus\/cmsify-admin:\$VERSION"/);
  assert.match(candidate, /--url http:\/\/127\.0\.0\.1:18081\/login/);
  assert.doesNotMatch(candidate, /dotnet (?:run|build|publish)|docker (?:build|pull)|docker buildx|npm pack/);
});
