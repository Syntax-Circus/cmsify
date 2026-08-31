import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { parseYamlSubset } from "./yaml-subset.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const documentationPaths = [
  "README.md",
  "AGENTS.md",
  "docs/performance.md",
  "docs/v1-release-readiness.md",
  "docs/v1-release-remediation-handoff.md",
  "docs/superpowers/plans/2026-08-28-reproducible-quality-capacity.md",
];
const singleNodeCommand = "dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -m:1";

function read(relativePath) {
  return readFileSync(path.join(repositoryRoot, relativePath), "utf8");
}

function loadDocuments() {
  return Object.fromEntries(documentationPaths.map((relativePath) => [relativePath, read(relativePath)]));
}

function assertIncludesAll(document, requiredText) {
  for (const text of requiredText) {
    assert.ok(document.includes(text), `Expected documentation to include: ${text}`);
  }
}

function extractSection(document, heading) {
  const lines = document.split(/\r?\n/);
  const escaped = heading.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const start = lines.findIndex((line) => new RegExp(`^(#{1,6})\\s+${escaped}\\s*$`, "i").test(line));
  assert.notEqual(start, -1, `Missing section: ${heading}`);
  const level = lines[start].match(/^#+/)[0].length;
  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    const match = lines[index].match(/^(#{1,6})\s+/);
    if (match && match[1].length <= level) {
      end = index;
      break;
    }
  }
  return lines.slice(start, end).join("\n");
}

function headingAnchors(document) {
  const counts = new Map();
  const anchors = new Set();
  for (const line of document.split(/\r?\n/)) {
    const match = line.match(/^#{1,6}\s+(.+?)\s*#*\s*$/);
    if (!match) continue;
    const base = match[1]
      .replace(/\[([^\]]+)\]\([^)]+\)/g, "$1")
      .replace(/<[^>]+>/g, "")
      .replace(/[`*_~]/g, "")
      .toLowerCase()
      .replace(/[^\p{L}\p{N}\s-]/gu, "")
      .trim()
      .replace(/\s+/g, "-");
    const count = counts.get(base) ?? 0;
    anchors.add(count === 0 ? base : `${base}-${count}`);
    counts.set(base, count + 1);
  }
  return anchors;
}

function findBrokenLocalLinks(documents) {
  const broken = [];
  for (const [relativePath, document] of Object.entries(documents)) {
    const linkPattern = /(?<!!)\[[^\]]*\]\(([^)]+)\)/g;
    for (const match of document.matchAll(linkPattern)) {
      const rawTarget = match[1].trim().replace(/^<|>$/g, "");
      if (/^(?:https?:|mailto:)/i.test(rawTarget)) continue;
      const [filePart, fragment] = rawTarget.split("#", 2);
      const targetPath = filePart.length === 0
        ? relativePath
        : path.normalize(path.join(path.dirname(relativePath), decodeURIComponent(filePart)));
      const resolved = path.resolve(repositoryRoot, targetPath);
      if (!existsSync(resolved)) {
        broken.push(`${relativePath} -> ${rawTarget}`);
        continue;
      }
      if (fragment) {
        const targetDocument = documents[targetPath] ?? readFileSync(resolved, "utf8");
        if (!headingAnchors(targetDocument).has(decodeURIComponent(fragment).toLowerCase())) {
          broken.push(`${relativePath} -> ${rawTarget}`);
        }
      }
    }
  }
  return broken;
}

function assertBoundAggregate(section, documentName) {
  const match = section.match(/Fresh committed-tree full solution: (\d+)\/\1 passed \(Core (\d+) \+ \.NET client (\d+) \+ Admin (\d+) \+ Infrastructure (\d+) \+ API (\d+) = (\d+)\)\./);
  assert.ok(match, `${documentName} must bind the aggregate test total to its five project totals`);
  const [aggregate, ...parts] = match.slice(1).map(Number);
  const statedSum = parts.pop();
  assert.equal(parts.reduce((sum, value) => sum + value, 0), statedSum, `${documentName} component test totals must add up`);
  assert.equal(aggregate, statedSum, `${documentName} aggregate test total must equal its component total`);
}

function assertNoPrematureCertification(section, documentName) {
  assert.doesNotMatch(section, /\bfully certified\b/i, `${documentName} must not claim full certification`);
  assert.doesNotMatch(
    section,
    /\bhosted(?: validation| checks?| workflows?)? (?:passed|succeeded|validated|green|certified)\b/i,
    `${documentName} must not claim hosted validation succeeded`,
  );
}

function workflowCommands(relativePath, jobName) {
  const workflow = parseYamlSubset(read(relativePath), relativePath);
  return workflow.jobs[jobName].steps.flatMap((step) => typeof step.run === "string" ? [step.run.trim()] : []);
}

function validateDocumentationContract(documents) {
  const performance = documents["docs/performance.md"];
  const readiness = documents["docs/v1-release-readiness.md"];
  const handoff = documents["docs/v1-release-remediation-handoff.md"];
  const plan = documents["docs/superpowers/plans/2026-08-28-reproducible-quality-capacity.md"];
  const readinessUpdate = readiness.split("## Locked v1 decisions", 1)[0];
  const readinessEvidence = extractSection(readiness, "Task 11 quality and capacity evidence");
  const readinessF11 = extractSection(readiness, "F-11 — Release builds are noisy and do not enforce first-party warning quality");
  const readinessMedium = extractSection(readiness, "Medium findings and enhancements");
  const handoffResume = extractSection(handoff, "Resume point");
  const handoffCarry = extractSection(handoff, "Carry to Task 12 final review");
  const handoffEvidence = extractSection(handoff, "Task 11 quality and capacity evidence");

  assertIncludesAll(performance, [
    "dotnet --version",
    "10.0.400",
    "dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --locked-mode",
    "dotnet restore Cmsify.slnx --locked-mode",
    "dotnet restore Cmsify.slnx --configfile artifacts/local-nuget/NuGet.Config --packages artifacts/local-nuget/packages --force-evaluate",
    "dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental",
    "dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test Cmsify.slnx --configuration Release --no-build --collect:\"XPlat Code Coverage\" --results-directory artifacts/coverage --verbosity minimal",
    "node scripts/quality/summarize-coverage.mjs --input artifacts/coverage --json artifacts/coverage/summary.json --markdown artifacts/coverage/summary.md",
    "node scripts/quality/run-capacity.mjs",
    singleNodeCommand,
    "single MSBuild node",
    "does not serialize xUnit test cases",
  ]);
  assert.doesNotMatch(performance, /RunConfiguration\.MaxCpuCount/);

  assert.match(performance, /exactly two content SQL commands/i);
  assert.match(performance, /database-side[^\n]+(?:LIMIT|paging)/i);
  assert.match(performance, /no duplicate (?:row or )?lease/i);
  assert.match(performance, /one byte over[^\n]+no (?:database|blob|storage) state/i);
  assert.match(performance, /incremental streaming[^\n]+ownership/i);
  assert.match(performance, /coverage percentages[^\n]+trend/i);
  assert.match(performance, /latency budgets[^\n]+(?:diagnostic|trend)/i);
  assertIncludesAll(performance, [
    "p95 <= 250 ms", "p99 <= 500 ms", "TTFB <= 500 ms",
    "cmsify.coverage.v1", "cmsify.capacity.v1", "sourceSha", "schema",
    "520", "2,600", "251", "PostgreSQL 17", "33.405 ms",
    "existing indexes retained", "no new index",
  ]);

  assertIncludesAll(readinessUpdate, [
    "F-11, F-16, and F-17 are remediated at the local source/test level",
    "bdaa0ff4a8f6d5e9b6692575f57a524e925a9ca4",
    "overall release decision remains **not ready**",
  ]);
  assertIncludesAll(readinessF11, ["F-11", "bdaa0ff4a8f6d5e9b6692575f57a524e925a9ca4"]);
  assertIncludesAll(readinessMedium, ["F-16", "F-17", "bdaa0ff4a8f6d5e9b6692575f57a524e925a9ca4"]);
  assertIncludesAll(handoffResume, [
    "Tasks 1–11 are implemented and validated locally",
    "outer remediation Task 12 remains open",
    "SyntaxCircus.Http.Resilience",
    "0.2.0-cmsify.1",
  ]);
  assertIncludesAll(handoffCarry, [
    "exact candidate package certification",
    "exact candidate container certification",
    "accessibility certification",
    "backup and restart certification",
    "CRUD, media, OIDC, webhook, and scheduled-publication scenario certification",
    "clean consumers",
    "API and SDK compatibility",
    "deprecation policy",
    "SECURITY.md",
    "support policy",
    "release ownership",
    "vulnerability reporting",
    "release runbook",
    "rollback runbook",
    "repository-wide third-party action SHA review",
    "runtime-image digest pins",
    "SBOM",
    "signing/attestation",
    "immutable digest",
    "Task 9 rollback diagnostic omission",
    "AvailableAssetWithMissingBlob_ReturnsSanitizedProblemDetails",
    "Delete_WithStaleEtag_ReturnsPreconditionFailedWithoutTombstone",
  ]);
  assert.match(handoffCarry, /public(?:\/CI)? restore[^\n]+(?:complete|validated|passed)/i);
  assertBoundAggregate(readinessEvidence, "readiness evidence");
  assertBoundAggregate(handoffEvidence, "handoff evidence");
  assertNoPrematureCertification(readinessUpdate, "readiness update");
  assertNoPrematureCertification(handoffResume, "handoff resume");
  assertNoPrematureCertification(handoffCarry, "handoff carry");

  assert.match(read("scripts/quality/summarize-coverage.mjs"), /schema: "cmsify\.coverage\.v1"/);
  assert.match(read("scripts/quality/merge-capacity-reports.mjs"), /schema: "cmsify\.capacity\.v1"/);
  const dotnetCommands = workflowCommands(".github/workflows/dotnet-test.yml", "test");
  const capacityCommands = workflowCommands(".github/workflows/capacity-trends.yml", "capacity-trends");
  for (const command of [
    "dotnet restore Cmsify.slnx --locked-mode",
    "dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental",
    "dotnet test Cmsify.slnx --configuration Release --no-build --collect:\"XPlat Code Coverage\" --results-directory artifacts/coverage --verbosity minimal",
    "node scripts/quality/summarize-coverage.mjs --input artifacts/coverage --json artifacts/coverage/summary.json --markdown artifacts/coverage/summary.md",
    "dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
  ]) assert.ok(dotnetCommands.includes(command), `dotnet-test workflow must contain documented command: ${command}`);
  assert.ok(capacityCommands.includes("node scripts/quality/run-capacity.mjs"));

  const task11 = extractSection(plan, "Task 11: Add Dependency Automation and Apply Locked Restore Everywhere");
  assertIncludesAll(task11.split("- [ ] **Step 1", 1)[0], [
    "docs/superpowers/specs/2026-08-28-reproducible-quality-capacity-design.md",
    ".github/workflows/openapi-contract.yml",
    ".github/workflows/typescript-sdk.yml",
    "tests/release-contract/yaml-subset.mjs",
  ]);
  assert.deepEqual(findBrokenLocalLinks(documents), []);
}

test("quality and release documentation satisfies the executable operating contract", () => {
  validateDocumentationContract(loadDocuments());
});

const mutations = [
  ["rejects a wrong aggregate total", (documents) => {
    documents["docs/v1-release-readiness.md"] = documents["docs/v1-release-readiness.md"].replace("587/587", "552/552");
  }, /aggregate test total/],
  ["rejects a fully certified readiness status", (documents) => {
    documents["docs/v1-release-readiness.md"] = documents["docs/v1-release-readiness.md"].replace("overall release decision remains **not ready**", "overall release decision is fully certified");
  }, /full certification|not ready/],
  ["rejects hosted validation success language", (documents) => {
    documents["docs/v1-release-remediation-handoff.md"] = documents["docs/v1-release-remediation-handoff.md"].replace("## Authoritative plans and audit", "Hosted validation succeeded.\n\n## Authoritative plans and audit");
  }, /hosted validation succeeded/],
  ["rejects removal of a required handoff carry", (documents) => {
    documents["docs/v1-release-remediation-handoff.md"] = documents["docs/v1-release-remediation-handoff.md"].replace("CRUD, media, OIDC, webhook, and scheduled-publication scenario certification", "scenario certification");
  }, /CRUD, media, OIDC/],
  ["rejects a bad local anchor", (documents) => {
    documents["docs/v1-release-remediation-handoff.md"] = documents["docs/v1-release-remediation-handoff.md"].replace("performance.md)", "performance.md#missing-anchor)");
  }, /deep-equal|missing-anchor/],
  ["rejects the wrong serial command", (documents) => {
    documents["docs/performance.md"] = documents["docs/performance.md"].replace(singleNodeCommand, "dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -- RunConfiguration.MaxCpuCount=1");
  }, /single MSBuild|RunConfiguration|Expected documentation to include/],
];

for (const [name, mutate, expected] of mutations) {
  test(name, () => {
    const documents = loadDocuments();
    assert.doesNotThrow(() => validateDocumentationContract(documents), "The unmutated documentation fixture must satisfy the contract");
    mutate(documents);
    assert.throws(() => validateDocumentationContract(documents), expected);
  });
}
