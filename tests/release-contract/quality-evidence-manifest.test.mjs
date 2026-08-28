import assert from "node:assert/strict";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { buildCapacityReport } from "../../scripts/quality/merge-capacity-reports.mjs";
import { main as summarizeCoverage } from "../../scripts/quality/summarize-coverage.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const manifestPath = "docs/evidence/task-11-local-verification.json";
const evidenceSourceSha = "e72b4681158cf687f0462bb2aa29f9ed47771e49";

function read(relativePath) {
  return readFileSync(path.join(repositoryRoot, relativePath), "utf8");
}

function loadFixture() {
  return {
    manifest: JSON.parse(read(manifestPath)),
    readiness: read("docs/v1-release-readiness.md"),
    handoff: read("docs/v1-release-remediation-handoff.md"),
    performance: read("docs/performance.md"),
    plan: read("docs/superpowers/plans/2026-08-28-reproducible-quality-capacity.md"),
  };
}

function extractSection(document, heading) {
  const lines = document.split(/\r?\n/);
  const escaped = heading.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const start = lines.findIndex((line) => new RegExp(`^(#{1,6})\\s+${escaped}\\s*$`, "i").test(line));
  assert.notEqual(start, -1, `Missing governed section: ${heading}`);
  const level = lines[start].match(/^#+/)[0].length;
  const endOffset = lines.slice(start + 1).findIndex((line) => {
    const match = line.match(/^(#{1,6})\s+/);
    return match && match[1].length <= level;
  });
  const end = endOffset === -1 ? lines.length : start + 1 + endOffset;
  return lines.slice(start, end).join("\n");
}

function extractTableRow(document, identifier) {
  const row = document.split(/\r?\n/).find((line) => line.startsWith(`| ${identifier} |`));
  assert.ok(row, `Missing governed table row: ${identifier}`);
  return row;
}

function temporaryDirectory(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "cmsify-quality-evidence-"));
  try {
    return callback(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function coverageSourceContract() {
  return temporaryDirectory((root) => {
    const input = path.join(root, "raw");
    const reportDirectory = path.join(input, "run");
    mkdirSync(reportDirectory, { recursive: true });
    writeFileSync(path.join(reportDirectory, "coverage.cobertura.xml"), `<?xml version="1.0"?>
<coverage><packages><package name="Evidence"><classes><class name="Evidence" filename="Evidence.cs"><methods/><lines><line number="1" hits="1" branch="False"/></lines></class></classes></package></packages></coverage>
`);
    const json = path.join(root, "summary.json");
    const markdown = path.join(root, "summary.md");
    const previousSha = process.env.GITHUB_SHA;
    process.env.GITHUB_SHA = evidenceSourceSha;
    try {
      summarizeCoverage(["--input", input, "--json", json, "--markdown", markdown]);
    } finally {
      if (previousSha === undefined) delete process.env.GITHUB_SHA;
      else process.env.GITHUB_SHA = previousSha;
    }
    const report = JSON.parse(readFileSync(json, "utf8"));
    return {
      schema: report.schema,
      topLevelFields: Object.keys(report),
      assemblyFields: Object.keys(report.assemblies[0]),
      metricFields: Object.keys(report.assemblies[0].lines),
    };
  });
}

function capacityFragments() {
  return {
    "resolved-content.json": {
      databaseVersion: "PostgreSQL 17 evidence",
      datasetCounts: {
        contentItems: 520,
        publishedVersions: 2600,
        eligibleItems: 500,
        filteredEligibleItems: 250,
        deletedOwners: 20,
        templates: 5,
        locales: 2,
        tags: 7,
      },
      queryCounts: [2], sampleCount: 1, elapsedMilliseconds: [10],
      p50Milliseconds: 10, p95Milliseconds: 10, p99Milliseconds: 10,
      p95AtOrBelow250Milliseconds: true, p99AtOrBelow500Milliseconds: true,
      blockingInvariantsPassed: true,
    },
    "webhook-claim.json": {
      databaseVersion: "PostgreSQL 17 evidence", eligibleRows: 251, batchSize: 100,
      commandCount: 3, sampleCount: 1, samplesMilliseconds: [10],
      p50Milliseconds: 10, p95Milliseconds: 10, p99Milliseconds: 10,
      duplicateCount: 0, overclaimCount: 0, p95AtOrBelow250Milliseconds: true,
      blockingInvariantsPassed: true,
    },
    "media-streaming.json": {
      bytes: 52428800, sampleCount: 1, timeToFirstByteMilliseconds: 10,
      totalDurationMilliseconds: 20, maximumObservedReadRequestBytes: 65536,
      maximumObservedWriteRequestBytes: 65536,
      timeToFirstByteAtOrBelow500Milliseconds: true, blockingInvariantsPassed: true,
    },
  };
}

function capacitySourceContract() {
  return temporaryDirectory((root) => {
    const fragmentPaths = [];
    for (const [name, value] of Object.entries(capacityFragments())) {
      const fragmentPath = path.join(root, name);
      writeFileSync(fragmentPath, `${JSON.stringify(value)}\n`);
      fragmentPaths.push(fragmentPath);
    }
    const report = buildCapacityReport({
      fragmentPaths,
      sourceSha: evidenceSourceSha,
      sdkVersion: "10.0.400",
      generatedAtUtc: "2026-08-28T00:00:00.000Z",
    });
    return {
      schema: report.schema,
      topLevelFields: Object.keys(report),
      datasetFields: Object.fromEntries(Object.entries(report.datasets).map(([name, value]) => [name, Object.keys(value)])),
      measurementFields: Object.fromEntries(Object.entries(report.measurements).map(([name, value]) => [name, Object.keys(value)])),
      diagnosticBudgetNames: Object.keys(report.diagnosticBudgets),
      diagnosticBudgetFields: Object.keys(report.diagnosticBudgets.mediaStreamingTimeToFirstByte),
      thresholdsMilliseconds: Object.fromEntries(Object.entries(report.diagnosticBudgets).map(([name, value]) => [name, value.thresholdMilliseconds])),
    };
  });
}

const sourceContracts = {
  coverage: coverageSourceContract(),
  capacity: capacitySourceContract(),
};

function datasetConstantsFromCSharp() {
  const resolved = read("tests/Cmsify.Api.Integration.Tests/ResolvedContentListQueryTests.cs");
  const webhookSource = read("tests/Cmsify.Infrastructure.Tests/WebhookDurabilityRepositoryTests.cs");
  const webhook = webhookSource.match(/OutboxClaim_BoundsSequentialBatchesAndConcurrentLeases\(\)[\s\S]{0,1200}/)?.[0] ?? "";
  const contentItems = Number(resolved.match(/CapacityContentItemCount\s*=\s*(\d+)/)?.[1]);
  const versionsPerItem = Number(resolved.match(/CapacityVersionCount\s*=\s*CapacityContentItemCount\s*\*\s*(\d+)/)?.[1]);
  const webhookEligibleRows = Number(webhook.match(/eligibleRows\s*=\s*(\d+)/)?.[1]);
  assert.ok(Number.isInteger(contentItems) && Number.isInteger(versionsPerItem) && Number.isInteger(webhookEligibleRows));
  return {
    resolvedContentItems: contentItems,
    resolvedPublishedVersions: contentItems * versionsPerItem,
    webhookEligibleRows,
  };
}

function parseEvidenceTuple(section) {
  const match = section.match(/Evidence tuple: source SHA `([0-9a-f]{40})`; SDK `([^`]+)`; Core (\d+), \.NET client (\d+), Admin (\d+), Infrastructure (\d+), API (\d+); full (\d+); coverage (\d+); coverage reports (\d+); coverage schema `([^`]+)`\./);
  assert.ok(match, "Governed evidence section must contain the exact checked evidence tuple");
  return {
    evidenceSourceSha: match[1],
    sdkVersion: match[2],
    projectTests: {
      core: Number(match[3]), dotnetClient: Number(match[4]), admin: Number(match[5]),
      infrastructure: Number(match[6]), api: Number(match[7]),
    },
    fullTestTotal: Number(match[8]),
    coverageTestTotal: Number(match[9]),
    coverageReportCount: Number(match[10]),
    coverageSchema: match[11],
  };
}

function expectedEvidenceTuple(manifest) {
  return {
    evidenceSourceSha: manifest.evidenceSourceSha,
    sdkVersion: manifest.sdkVersion,
    projectTests: manifest.tests.projects,
    fullTestTotal: manifest.tests.fullTotal,
    coverageTestTotal: manifest.tests.coverageTotal,
    coverageReportCount: manifest.tests.coverageReportCount,
    coverageSchema: manifest.reportContracts.coverage.schema,
  };
}

function assertNoPositiveReleaseClaim(section, name) {
  const positiveClaims = [
    /\bfully certified\b/ig,
    /\bhosted (?:checks|workflows|validation) (?:passed|succeeded|validated|green|certified)\b/ig,
    /\bpublic(?:\/CI)? restore (?:passed|succeeded|validated|green|certified)\b/ig,
    /\brelease[- ]ready\b/ig,
    /\brelease certification (?:is )?(?:complete|completed|passed|certified)\b/ig,
  ];
  for (const pattern of positiveClaims) {
    for (const match of section.matchAll(pattern)) {
      const clauseStart = Math.max(section.lastIndexOf(".", match.index) + 1, section.lastIndexOf("\n", match.index) + 1);
      const prefix = section.slice(clauseStart, match.index);
      assert.match(prefix, /\b(?:not|no|never)\b/i, `${name} contains a positive release/certification claim: ${match[0]}`);
    }
  }
}

function validateFixture(fixture) {
  const { manifest, readiness, handoff, performance, plan } = fixture;
  assert.equal(manifest.schema, "cmsify.task11-local-verification.v1");
  assert.deepEqual(manifest.reportContracts.coverage, sourceContracts.coverage);
  assert.deepEqual(manifest.reportContracts.capacity, sourceContracts.capacity);
  assert.deepEqual(manifest.datasets, datasetConstantsFromCSharp());
  assert.deepEqual(manifest.tests.projects, {
    core: 66, dotnetClient: 71, admin: 35, infrastructure: 303, api: 112,
  });
  assert.equal(Object.values(manifest.tests.projects).reduce((sum, count) => sum + count, 0), manifest.tests.fullTotal);
  assert.equal(manifest.tests.fullTotal, 587);
  assert.equal(manifest.tests.coverageTotal, 587);
  assert.equal(manifest.tests.coverageReportCount, 5);
  assert.equal(manifest.evidenceSourceSha, evidenceSourceSha);
  assert.equal(manifest.sdkVersion, "10.0.400");

  const readinessEvidence = extractSection(readiness, "Task 11 quality and capacity evidence");
  const handoffEvidence = extractSection(handoff, "Task 11 quality and capacity evidence");
  for (const [name, section] of [["readiness evidence", readinessEvidence], ["handoff evidence", handoffEvidence]]) {
    assert.match(section, /\[checked Task 11 evidence manifest\]\(evidence\/task-11-local-verification\.json\)/);
    assert.deepEqual(parseEvidenceTuple(section), expectedEvidenceTuple(manifest), `${name} must match the checked manifest`);
  }

  assert.match(performance, /\[checked Task 11 evidence manifest\]\(evidence\/task-11-local-verification\.json\)/);
  for (const command of Object.values(manifest.commands)) assert.ok(performance.includes(command), `Runbook must contain manifest command: ${command}`);
  const task12Inventory = extractSection(plan, "Task 12: Document the Quality and Capacity Operating Contract").split("- [ ] **Step 1", 1)[0];
  assert.ok(task12Inventory.includes(manifestPath));
  assert.ok(task12Inventory.includes("tests/release-contract/quality-evidence-manifest.test.mjs"));

  const governed = [
    ["readiness update", readiness.split("## Locked v1 decisions", 1)[0]],
    ["readiness F-11", extractSection(readiness, "F-11 — Release builds are noisy and do not enforce first-party warning quality")],
    ["readiness F-16", extractTableRow(readiness, "F-16")],
    ["readiness F-17", extractTableRow(readiness, "F-17")],
    ["handoff resume", extractSection(handoff, "Resume point")],
    ["handoff carry", extractSection(handoff, "Carry to Task 12 final review")],
    ["handoff evidence", handoffEvidence],
    ["handoff next task", extractSection(handoff, "Next task: user publication gate, then outer Task 12")],
  ];
  for (const [name, section] of governed) assertNoPositiveReleaseClaim(section, name);
  assert.doesNotMatch(handoff, /AGENTS\.md requires `rtk` command prefixes/i);
}

test("checked Task 11 evidence and quality contracts are bound to executable sources", () => {
  validateFixture(loadFixture());
});

const evidenceMutations = [
  ["rejects internally consistent but false 5/5 project evidence", (fixture) => {
    for (const document of ["readiness", "handoff"]) {
      fixture[document] = fixture[document]
        .replace(/Core 66, \.NET client 71, Admin 35, Infrastructure 303, API 112; full 587/g, "Core 5, .NET client 5, Admin 5, Infrastructure 5, API 5; full 25")
        .replace(/587\/587 passed \(Core 66 \+ \.NET client 71 \+ Admin 35 \+ Infrastructure 303 \+ API 112 = 587\)/g, "25/25 passed (Core 5 + .NET client 5 + Admin 5 + Infrastructure 5 + API 5 = 25)");
    }
  }],
  ["rejects false coverage total 552", (fixture) => {
    fixture.readiness = fixture.readiness.replace("coverage 587;", "coverage 552;");
  }],
  ["rejects false coverage report count", (fixture) => {
    fixture.handoff = fixture.handoff.replace("coverage reports 5;", "coverage reports 4;");
  }],
  ["rejects false evidence source SHA", (fixture) => {
    fixture.readiness = fixture.readiness.replaceAll(evidenceSourceSha, "0123456789abcdef0123456789abcdef01234567");
  }],
  ["rejects completed F-11 release certification", (fixture) => {
    fixture.readiness = fixture.readiness.replace("final release certification remains open", "release certification is complete");
  }],
  ["rejects hosted checks passed in handoff evidence", (fixture) => {
    fixture.handoff = fixture.handoff.replace("No hosted run", "Hosted checks passed");
  }],
];

for (const [name, mutate] of evidenceMutations) {
  test(name, () => {
    const fixture = loadFixture();
    assert.doesNotThrow(() => validateFixture(fixture), "The unmutated documentation and manifest must satisfy the contract");
    mutate(fixture);
    assert.throws(() => validateFixture(fixture));
  });
}
