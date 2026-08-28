import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const documentationPaths = [
  "README.md",
  "AGENTS.md",
  "docs/performance.md",
  "docs/v1-release-readiness.md",
  "docs/v1-release-remediation-handoff.md",
  "docs/superpowers/plans/2026-08-28-reproducible-quality-capacity.md",
];

function read(relativePath) {
  return readFileSync(path.join(repositoryRoot, relativePath), "utf8");
}

function assertIncludesAll(document, requiredText) {
  for (const text of requiredText) {
    assert.ok(document.includes(text), `Expected documentation to include: ${text}`);
  }
}

test("documents the executable restore, build, capacity, coverage, and serial-test commands", () => {
  const performance = read("docs/performance.md");

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
    "dotnet test Cmsify.slnx --configuration Release --no-build --collect:\"XPlat Code Coverage\" --results-directory artifacts/coverage",
    "node scripts/quality/summarize-coverage.mjs --input artifacts/coverage --json artifacts/coverage/summary.json --markdown artifacts/coverage/summary.md",
    "node scripts/quality/run-capacity.mjs",
    "dotnet test Cmsify.slnx --configuration Release --no-restore --verbosity minimal -- RunConfiguration.MaxCpuCount=1",
  ]);
});

test("distinguishes blocking capacity invariants from trend-only coverage and latency", () => {
  const performance = read("docs/performance.md");

  assert.match(performance, /exactly two content SQL commands/i);
  assert.match(performance, /database-side[^\n]+(?:LIMIT|paging)/i);
  assert.match(performance, /no duplicate (?:row or )?lease/i);
  assert.match(performance, /one byte over[^\n]+no (?:database|blob|storage) state/i);
  assert.match(performance, /incremental streaming[^\n]+ownership/i);
  assert.match(performance, /coverage percentages[^\n]+trend/i);
  assert.match(performance, /latency budgets[^\n]+(?:diagnostic|trend)/i);
  assertIncludesAll(performance, [
    "p95 <= 250 ms",
    "p99 <= 500 ms",
    "TTFB <= 500 ms",
    "cmsify.coverage.v1",
    "cmsify.capacity.v1",
    "520",
    "2,600",
    "251",
    "PostgreSQL 17",
    "33.405 ms",
    "existing indexes retained",
  ]);
});

test("records only committed F-11, F-16, and F-17 evidence while retaining later release gates", () => {
  const readiness = read("docs/v1-release-readiness.md");
  const handoff = read("docs/v1-release-remediation-handoff.md");
  const combined = `${readiness}\n${handoff}`;

  for (const finding of ["F-11", "F-16", "F-17"]) {
    assert.match(readiness, new RegExp(`${finding}[\\s\\S]{0,1800}bdaa0ff`, "i"));
  }

  assertIncludesAll(combined, [
    "SyntaxCircus.Http.Resilience",
    "0.2.0-cmsify.1",
    "repository-wide action",
    "runtime-image digest",
    "SBOM",
    "signing",
    "accessibility",
    "artifact smoke",
    "governance",
    "final release certification",
    "Task 9 rollback diagnostic omission",
    "AvailableAssetWithMissingBlob_ReturnsSanitizedProblemDetails",
    "Delete_WithStaleEtag_ReturnsPreconditionFailedWithoutTombstone",
  ]);
  assert.match(combined, /public(?:\/CI)? restore[^\n]+(?:blocked|gated)/i);
  assert.doesNotMatch(combined, /hosted (?:checks|workflows)[^\n]+(?:passed|green)/i);
});

test("keeps local Markdown links in the quality and release documents resolvable", () => {
  const broken = [];

  for (const relativePath of documentationPaths) {
    const document = read(relativePath);
    const linkPattern = /(?<!!)\[[^\]]*\]\(([^)]+)\)/g;
    for (const match of document.matchAll(linkPattern)) {
      const rawTarget = match[1].trim().replace(/^<|>$/g, "");
      if (/^(?:https?:|mailto:|#)/i.test(rawTarget)) continue;

      const withoutFragment = rawTarget.split("#", 1)[0];
      if (withoutFragment.length === 0) continue;
      const decodedTarget = decodeURIComponent(withoutFragment);
      const resolved = path.resolve(repositoryRoot, path.dirname(relativePath), decodedTarget);
      if (!existsSync(resolved)) broken.push(`${relativePath} -> ${rawTarget}`);
    }
  }

  assert.deepEqual(broken, []);
});
