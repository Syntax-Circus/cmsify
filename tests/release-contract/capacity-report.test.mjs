import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  renameSync as fsRenameSync,
  rmSync,
  statSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const merger = path.join(repositoryRoot, "scripts/quality/merge-capacity-reports.mjs");
const runner = path.join(repositoryRoot, "scripts/quality/run-capacity.mjs");
const sourceSha = "0123456789abcdef0123456789abcdef01234567";
const databaseVersion = "PostgreSQL 18.1";

function fragments() {
  return {
    "resolved-content.json": {
      databaseVersion,
      datasetCounts: {
        contentItems: 500,
        publishedVersions: 2500,
        eligibleItems: 499,
        filteredEligibleItems: 250,
        deletedOwners: 1,
        templates: 5,
        locales: 2,
        tags: 7,
      },
      queryCounts: [2, 2, 2, 2, 2],
      sampleCount: 5,
      elapsedMilliseconds: [10, 20, 30, 40, 50],
      p50Milliseconds: 30,
      p95Milliseconds: 50,
      p99Milliseconds: 50,
      p95AtOrBelow250Milliseconds: true,
      p99AtOrBelow500Milliseconds: true,
      blockingInvariantsPassed: true,
    },
    "webhook-claim.json": {
      databaseVersion,
      eligibleRows: 251,
      batchSize: 100,
      commandCount: 3,
      sampleCount: 5,
      samplesMilliseconds: [11, 21, 31, 41, 51],
      p50Milliseconds: 31,
      p95Milliseconds: 51,
      p99Milliseconds: 51,
      duplicateCount: 0,
      overclaimCount: 0,
      p95AtOrBelow250Milliseconds: true,
      blockingInvariantsPassed: true,
    },
    "media-streaming.json": {
      bytes: 50 * 1024 * 1024,
      sampleCount: 1,
      timeToFirstByteMilliseconds: 50,
      totalDurationMilliseconds: 500,
      maximumObservedReadRequestBytes: 64 * 1024,
      maximumObservedWriteRequestBytes: 64 * 1024,
      timeToFirstByteAtOrBelow500Milliseconds: true,
      blockingInvariantsPassed: true,
    },
  };
}

function withTemporaryDirectory(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "cmsify-capacity-report-"));
  try {
    callback(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function writeFragments(root, fixture = fragments()) {
  const directory = path.join(root, "fragments");
  mkdirSync(directory, { recursive: true });
  const paths = [];
  for (const [fileName, value] of Object.entries(fixture)) {
    const filePath = path.join(directory, fileName);
    writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`);
    paths.push(filePath);
  }
  return paths;
}

function runMerge(root, fragmentPaths, options = {}) {
  const output = options.output ?? path.join(root, "capacity-report.json");
  const arguments_ = [
    merger,
    "--source-sha", options.sourceSha ?? sourceSha,
    "--sdk-version", options.sdkVersion ?? "10.0.400",
    "--output", output,
  ];
  for (const fragmentPath of fragmentPaths) {
    arguments_.push("--fragment", fragmentPath);
  }
  return {
    output,
    result: spawnSync(process.execPath, arguments_, {
      cwd: repositoryRoot,
      encoding: "utf8",
    }),
  };
}

test("merges the three established fragments into the exact stable capacity schema", () => {
  withTemporaryDirectory((root) => {
    const { output, result } = runMerge(root, writeFragments(root));

    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, "");
    const report = JSON.parse(readFileSync(output, "utf8"));
    assert.match(report.generatedAtUtc, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
    delete report.generatedAtUtc;
    assert.deepEqual(report, {
      schema: "cmsify.capacity.v1",
      sourceSha,
      sdkVersion: "10.0.400",
      databaseVersion,
      datasets: {
        mediaStreaming: { bytes: 50 * 1024 * 1024 },
        resolvedContent: {
          contentItems: 500,
          publishedVersions: 2500,
          eligibleItems: 499,
          filteredEligibleItems: 250,
          deletedOwners: 1,
          templates: 5,
          locales: 2,
          tags: 7,
        },
        webhookClaim: { eligibleRows: 251, batchSize: 100 },
      },
      measurements: {
        mediaStreaming: {
          sampleCount: 1,
          timeToFirstByteMilliseconds: 50,
          totalDurationMilliseconds: 500,
          maximumObservedReadRequestBytes: 64 * 1024,
          maximumObservedWriteRequestBytes: 64 * 1024,
        },
        resolvedContent: {
          sampleCount: 5,
          queryCounts: [2, 2, 2, 2, 2],
          elapsedMilliseconds: [10, 20, 30, 40, 50],
          p50Milliseconds: 30,
          p95Milliseconds: 50,
          p99Milliseconds: 50,
        },
        webhookClaim: {
          sampleCount: 5,
          commandCount: 3,
          samplesMilliseconds: [11, 21, 31, 41, 51],
          p50Milliseconds: 31,
          p95Milliseconds: 51,
          p99Milliseconds: 51,
          duplicateCount: 0,
          overclaimCount: 0,
        },
      },
      diagnosticBudgets: {
        mediaStreamingTimeToFirstByte: {
          actualMilliseconds: 50,
          thresholdMilliseconds: 500,
          passed: true,
        },
        resolvedContentP95: {
          actualMilliseconds: 50,
          thresholdMilliseconds: 250,
          passed: true,
        },
        resolvedContentP99: {
          actualMilliseconds: 50,
          thresholdMilliseconds: 500,
          passed: true,
        },
        webhookClaimP95: {
          actualMilliseconds: 51,
          thresholdMilliseconds: 250,
          passed: true,
        },
      },
      blockingInvariantsPassed: true,
    });
    assert.equal(readFileSync(output, "utf8").endsWith("\n"), true);
  });
});

test("requires exact lowercase source and SDK identities before replacing output", () => {
  withTemporaryDirectory((root) => {
    const fragmentPaths = writeFragments(root);
    const output = path.join(root, "capacity-report.json");
    writeFileSync(output, "sentinel\n");
    const invalidIdentities = [
      { sourceSha: sourceSha.toUpperCase(), sdkVersion: "10.0.400", diagnostic: /lowercase.*40|40.*lowercase/i },
      { sourceSha: sourceSha.slice(1), sdkVersion: "10.0.400", diagnostic: /source SHA.*40/i },
      { sourceSha, sdkVersion: "10.0.401", diagnostic: /SDK.*10\.0\.400/i },
    ];

    for (const identity of invalidIdentities) {
      const { result } = runMerge(root, fragmentPaths, { ...identity, output });

      assert.notEqual(result.status, 0);
      assert.match(result.stderr, identity.diagnostic);
      assert.equal(readFileSync(output, "utf8"), "sentinel\n");
    }
  });
});

test("rejects missing, duplicate, and unknown fragment identities", () => {
  withTemporaryDirectory((root) => {
    const fragmentPaths = writeFragments(root);
    const unknown = path.join(root, "fragments", "unknown.json");
    writeFileSync(unknown, "{}\n");
    const cases = [
      { paths: fragmentPaths.slice(1), diagnostic: /missing.*resolved-content\.json/i },
      { paths: [...fragmentPaths, fragmentPaths[0]], diagnostic: /duplicate.*resolved-content\.json/i },
      { paths: [...fragmentPaths, unknown], diagnostic: /unknown.*unknown\.json/i },
    ];

    for (const fixture of cases) {
      const { output, result } = runMerge(root, fixture.paths);

      assert.notEqual(result.status, 0);
      assert.match(result.stderr, fixture.diagnostic);
      assert.equal(existsSync(output), false);
    }
  });
});

test("requires PostgreSQL identity agreement between database-backed fragments", () => {
  withTemporaryDirectory((root) => {
    const fixture = fragments();
    fixture["webhook-claim.json"].databaseVersion = "PostgreSQL 17.6";

    const { output, result } = runMerge(root, writeFragments(root, fixture));

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /database.*identity.*disagree|PostgreSQL.*disagree/i);
    assert.equal(existsSync(output), false);
  });
});

test("requires generatedAtUtc to equal the canonical UTC form emitted by Date.toISOString", async () => {
  await withAsyncTemporaryDirectory(async (root) => {
    const fragmentPaths = writeFragments(root);
    const { buildCapacityReport } = await import(pathToFileURL(merger).href);
    for (const generatedAtUtc of [
      "2026-08-28",
      "2026-08-28T12:34:56Z",
      "2026-08-28T12:34:56.789+00:00",
      "2026-08-28t12:34:56.789z",
    ]) {
      assert.throws(
        () => buildCapacityReport({
          fragmentPaths,
          sourceSha,
          sdkVersion: "10.0.400",
          generatedAtUtc,
        }),
        /generatedAtUtc.*canonical.*UTC|canonical.*UTC.*generatedAtUtc/i,
        generatedAtUtc,
      );
    }
  });
});

test("rejects missing, non-finite, negative, and inconsistent sample or query measurements", () => {
  const cases = [
    {
      mutate(fixture) { delete fixture["resolved-content.json"].sampleCount; },
      diagnostic: /resolved-content.*sampleCount/i,
    },
    {
      mutate(fixture) { fixture["resolved-content.json"].queryCounts = [2, 2, 2, 2]; },
      diagnostic: /resolved-content.*queryCounts.*sampleCount/i,
    },
    {
      mutate(fixture) { fixture["resolved-content.json"].queryCounts[0] = 3; },
      diagnostic: /resolved-content.*queryCounts.*exactly 2/i,
    },
    {
      mutate(fixture) { fixture["webhook-claim.json"].commandCount = 0; },
      diagnostic: /webhook-claim.*commandCount.*positive/i,
    },
    {
      mutate(fixture) { fixture["media-streaming.json"].timeToFirstByteMilliseconds = -1; },
      diagnostic: /media-streaming.*timeToFirstByteMilliseconds.*non-negative/i,
    },
    {
      mutate() {},
      rawMutation(source) {
        return source.replace('"p50Milliseconds": 30', '"p50Milliseconds": 1e400');
      },
      diagnostic: /resolved-content.*p50Milliseconds.*finite/i,
    },
  ];

  for (const [index, fixtureCase] of cases.entries()) {
    withTemporaryDirectory((root) => {
      const fixture = fragments();
      fixtureCase.mutate(fixture);
      const fragmentPaths = writeFragments(root, fixture);
      if (fixtureCase.rawMutation) {
        const resolvedPath = fragmentPaths.find((entry) => entry.endsWith("resolved-content.json"));
        const source = fixtureCase.rawMutation(readFileSync(resolvedPath, "utf8"));
        writeFileSync(resolvedPath, source);
      }

      const { output, result } = runMerge(root, fragmentPaths);

      assert.notEqual(result.status, 0);
      assert.match(result.stderr, fixtureCase.diagnostic);
      assert.equal(existsSync(output), false);
    });
  }
});

test("rejects duplicate or overclaimed webhook rows and false blocking flags", () => {
  const cases = [
    {
      mutate(fixture) { fixture["webhook-claim.json"].duplicateCount = 1; },
      diagnostic: /webhook-claim.*duplicateCount.*zero/i,
    },
    {
      mutate(fixture) { fixture["webhook-claim.json"].overclaimCount = 1; },
      diagnostic: /webhook-claim.*overclaimCount.*zero/i,
    },
    {
      mutate(fixture) { fixture["media-streaming.json"].blockingInvariantsPassed = false; },
      diagnostic: /media-streaming.*blockingInvariantsPassed.*true/i,
    },
  ];

  for (const fixtureCase of cases) {
    withTemporaryDirectory((root) => {
      const fixture = fragments();
      fixtureCase.mutate(fixture);

      const { output, result } = runMerge(root, writeFragments(root, fixture));

      assert.notEqual(result.status, 0);
      assert.match(result.stderr, fixtureCase.diagnostic);
      assert.equal(existsSync(output), false);
    });
  }
});

test("records diagnostic budget misses with passed false, a warning, and exit zero", () => {
  withTemporaryDirectory((root) => {
    const fixture = fragments();
    fixture["resolved-content.json"].elapsedMilliseconds[4] = 300;
    fixture["resolved-content.json"].p95Milliseconds = 300;
    fixture["resolved-content.json"].p99Milliseconds = 300;
    fixture["resolved-content.json"].p95AtOrBelow250Milliseconds = false;
    fixture["webhook-claim.json"].samplesMilliseconds[4] = 275;
    fixture["webhook-claim.json"].p95Milliseconds = 275;
    fixture["webhook-claim.json"].p99Milliseconds = 275;
    fixture["webhook-claim.json"].p95AtOrBelow250Milliseconds = false;

    const { output, result } = runMerge(root, writeFragments(root, fixture));

    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stderr, /^::warning::.*diagnostic capacity budgets missed.*resolvedContentP95.*webhookClaimP95/im);
    const report = JSON.parse(readFileSync(output, "utf8"));
    assert.equal(report.diagnosticBudgets.resolvedContentP95.passed, false);
    assert.equal(report.diagnosticBudgets.webhookClaimP95.passed, false);
    assert.equal(report.blockingInvariantsPassed, true);
  });
});

test("rejects an output alias of an input fragment without changing fragment bytes", () => {
  withTemporaryDirectory((root) => {
    const fragmentPaths = writeFragments(root);
    const output = fragmentPaths[0];
    const before = readFileSync(output);

    const { result } = runMerge(root, fragmentPaths, { output });

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /output.*fragment|fragment.*output/i);
    assert.deepEqual(readFileSync(output), before);
  });
});

test("restores the previous capacity report when atomic installation fails", async () => {
  await withAsyncTemporaryDirectory(async (root) => {
    const { replaceCapacityReport } = await import(pathToFileURL(merger).href);
    const destination = path.join(root, "capacity-report.json");
    writeFileSync(destination, "old report\n");
    let destinationObservedDuringInstall = null;
    let canonicalMovedBeforeInstall = false;
    const operations = {
      existsSync,
      mkdirSync,
      statSync,
      temporaryPath: (target, purpose) => `${target}.${purpose}`,
      writeFileSync,
      renameSync(source, target) {
        if (source === destination) canonicalMovedBeforeInstall = true;
        if (source.endsWith(".stage")) {
          destinationObservedDuringInstall = existsSync(destination)
            ? readFileSync(destination, "utf8")
            : null;
          throw new Error("injected install failure");
        }
        fsRenameSync(source, target);
      },
      rmSync,
    };

    assert.throws(
      () => replaceCapacityReport(destination, "new report\n", operations),
      /before commit.*injected install failure/i,
    );
    assert.equal(canonicalMovedBeforeInstall, false);
    assert.equal(destinationObservedDuringInstall, "old report\n");
    assert.equal(readFileSync(destination, "utf8"), "old report\n");
    assert.equal(existsSync(`${destination}.stage`), false);
    assert.equal(existsSync(`${destination}.backup`), false);
  });
});

async function withAsyncTemporaryDirectory(callback) {
  const root = mkdtempSync(path.join(tmpdir(), "cmsify-capacity-runner-"));
  try {
    return await callback(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function capacityProcessDouble(calls, fixture = fragments(), failProject = null) {
  return (command, arguments_, options) => {
    calls.push({ command, arguments: [...arguments_], cwd: options.cwd, env: options.env });
    if (command === "git") {
      return { status: 0, stdout: `${sourceSha}\n`, stderr: "" };
    }
    if (command === "dotnet" && arguments_.length === 1 && arguments_[0] === "--version") {
      return { status: 0, stdout: "10.0.400\n", stderr: "" };
    }
    assert.equal(command, "dotnet");
    const project = arguments_[1].replaceAll("\\", "/");
    if (project === failProject) {
      return { status: 1, stdout: "", stderr: "capacity invariant failed\n" };
    }
    const fileName = project.includes("Cmsify.Api.Integration.Tests")
      ? "resolved-content.json"
      : project.includes("Cmsify.Infrastructure.Tests")
        ? "webhook-claim.json"
        : "media-streaming.json";
    mkdirSync(options.env.CMSIFY_CAPACITY_REPORT_DIR, { recursive: true });
    writeFileSync(
      path.join(options.env.CMSIFY_CAPACITY_REPORT_DIR, fileName),
      `${JSON.stringify(fixture[fileName], null, 2)}\n`,
    );
    return { status: 0, stdout: "capacity passed\n", stderr: "" };
  };
}

function createDirectoryLinkOrSkip(context, target, linkPath) {
  try {
    symlinkSync(target, linkPath, process.platform === "win32" ? "junction" : "dir");
    return true;
  } catch (error) {
    context.skip(`Directory links are unavailable on this host: ${error.message}`);
    return false;
  }
}

test("runner rejects a capacity ancestor junction before it can delete an external sentinel", async (context) => {
  const root = mkdtempSync(path.join(tmpdir(), "cmsify-capacity-ancestor-link-"));
  const external = mkdtempSync(path.join(tmpdir(), "cmsify-capacity-external-"));
  const sentinel = path.join(external, "fragments", "sentinel.txt");
  try {
    mkdirSync(path.dirname(sentinel), { recursive: true });
    writeFileSync(sentinel, "external sentinel\n");
    mkdirSync(path.join(root, "artifacts"), { recursive: true });
    if (!createDirectoryLinkOrSkip(context, external, path.join(root, "artifacts", "capacity"))) return;
    const calls = [];
    const { runCapacity } = await import(pathToFileURL(runner).href);

    assert.throws(
      () => runCapacity({ repositoryRoot: root, execute: capacityProcessDouble(calls) }),
      /symbolic link|junction|reparse|outside.*repository/i,
    );
    assert.equal(readFileSync(sentinel, "utf8"), "external sentinel\n");
    assert.equal(calls.length, 2, "cleanup rejection must happen before any test project starts");
  } finally {
    rmSync(root, { recursive: true, force: true });
    rmSync(external, { recursive: true, force: true });
  }
});

test("runner rejects a nested fragment junction without traversing or deleting it", async (context) => {
  const root = mkdtempSync(path.join(tmpdir(), "cmsify-capacity-nested-link-"));
  const external = mkdtempSync(path.join(tmpdir(), "cmsify-capacity-external-"));
  const sentinel = path.join(external, "sentinel.txt");
  try {
    writeFileSync(sentinel, "external sentinel\n");
    const fragmentDirectory = path.join(root, "artifacts", "capacity", "fragments");
    mkdirSync(fragmentDirectory, { recursive: true });
    writeFileSync(path.join(fragmentDirectory, "stale.json"), "stale\n");
    if (!createDirectoryLinkOrSkip(context, external, path.join(fragmentDirectory, "external"))) return;
    const calls = [];
    const { runCapacity } = await import(pathToFileURL(runner).href);

    assert.throws(
      () => runCapacity({ repositoryRoot: root, execute: capacityProcessDouble(calls) }),
      /symbolic link|junction|reparse/i,
    );
    assert.equal(readFileSync(sentinel, "utf8"), "external sentinel\n");
    assert.equal(calls.length, 2, "cleanup rejection must happen before any test project starts");
  } finally {
    rmSync(root, { recursive: true, force: true });
    rmSync(external, { recursive: true, force: true });
  }
});

test("runner cleans only the exact fragment directory and runs the three Release capacity projects", async () => {
  await withAsyncTemporaryDirectory(async (root) => {
    const capacityRoot = path.join(root, "artifacts", "capacity");
    const fragmentDirectory = path.join(capacityRoot, "fragments");
    mkdirSync(fragmentDirectory, { recursive: true });
    writeFileSync(path.join(fragmentDirectory, "stale.json"), "stale\n");
    writeFileSync(path.join(capacityRoot, "keep.txt"), "keep\n");
    const calls = [];
    const { runCapacity } = await import(pathToFileURL(runner).href);

    const result = runCapacity({
      repositoryRoot: root,
      execute: capacityProcessDouble(calls),
      now: new Date("2026-08-28T12:34:56.789Z"),
    });

    assert.deepEqual(calls.map((call) => [call.command, call.arguments]), [
      ["git", ["rev-parse", "HEAD"]],
      ["dotnet", ["--version"]],
      ["dotnet", [
        "test",
        "tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj",
        "--configuration", "Release",
        "--no-build",
        "--filter", "Category=Capacity",
      ]],
      ["dotnet", [
        "test",
        "tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj",
        "--configuration", "Release",
        "--no-build",
        "--filter", "Category=Capacity",
      ]],
      ["dotnet", [
        "test",
        "sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj",
        "--configuration", "Release",
        "--no-build",
        "--filter", "Category=Capacity",
      ]],
    ]);
    assert.equal(calls.every((call) => call.cwd === root), true);
    for (const call of calls.slice(2)) {
      assert.equal(call.env.CMSIFY_CAPACITY_TIMING, "true");
      assert.equal(call.env.CMSIFY_CAPACITY_REPORT_DIR, fragmentDirectory);
    }
    assert.deepEqual(
      [...new Set(calls.slice(2).map((call) => call.env))].length,
      1,
      "all test projects must receive one stable captured environment",
    );
    assert.equal(readFileSync(path.join(capacityRoot, "keep.txt"), "utf8"), "keep\n");
    assert.deepEqual(
      [...new Set(Object.keys(fragments()))].sort(),
      (await import("node:fs")).readdirSync(fragmentDirectory).sort(),
    );
    assert.equal(result.report.generatedAtUtc, "2026-08-28T12:34:56.789Z");
    assert.deepEqual(
      JSON.parse(readFileSync(path.join(capacityRoot, "capacity-report.json"), "utf8")),
      result.report,
    );
    assert.equal(result.markdown, `# Capacity trend

Source SHA: \`${sourceSha}\`${"  "}
SDK: \`10.0.400\`${"  "}
Database: \`${databaseVersion}\`

| Scenario | Samples | Latency | Diagnostic budget |
| --- | ---: | ---: | --- |
| Media streaming | 1 | TTFB 50.000 ms | TTFB <= 500 ms: passed |
| Resolved content | 5 | p95 50.000 ms; p99 50.000 ms | p95 <= 250 ms: passed; p99 <= 500 ms: passed |
| Webhook claim | 5 | p95 51.000 ms | p95 <= 250 ms: passed |
`);
  });
});

test("runner stops immediately on a capacity invariant test failure", async () => {
  await withAsyncTemporaryDirectory(async (root) => {
    const calls = [];
    const failingProject = "tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj";
    const { runCapacity } = await import(pathToFileURL(runner).href);

    assert.throws(
      () => runCapacity({
        repositoryRoot: root,
        execute: capacityProcessDouble(calls, fragments(), failingProject),
      }),
      /Cmsify\.Infrastructure\.Tests.*exit code 1.*capacity invariant failed/is,
    );
    assert.equal(
      calls.some((call) => call.arguments.some((argument) => argument.includes("SyntaxCircus.Cmsify.Client.Tests"))),
      false,
    );
    assert.equal(existsSync(path.join(root, "artifacts", "capacity", "capacity-report.json")), false);
  });
});

test("scheduled capacity workflow is manual and weekly with only pinned actions and locked Release inputs", () => {
  const workflow = readFileSync(path.join(repositoryRoot, ".github/workflows/capacity-trends.yml"), "utf8");

  assert.match(workflow, /workflow_dispatch\s*:/);
  assert.match(workflow, /schedule\s*:\s*\n\s*-\s*cron\s*:\s*["']\d+\s+\d+\s+\*\s+\*\s+\d["']/);
  assert.match(workflow, /permissions\s*:\s*\n\s*contents\s*:\s*read/);
  assert.match(workflow, /actions\/checkout@11bd71901bbe5b1630ceea73d27597364c9af683/);
  assert.match(workflow, /actions\/setup-dotnet@c2fa09f4bde5ebb9d1777cf28262a3eb3db3ced7/);
  assert.match(workflow, /actions\/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02/);
  assert.doesNotMatch(workflow, /uses:\s*[^\s]+@v\d/i);
  assert.match(workflow, /global-json-file\s*:\s*global\.json/);
  assert.match(workflow, /dotnet restore Cmsify\.slnx --locked-mode/);
  assert.match(workflow, /dotnet build Cmsify\.slnx --configuration Release --no-restore/);
  assert.match(workflow, /node scripts\/quality\/run-capacity\.mjs/);
  assert.match(workflow, /path\s*:\s*artifacts\/capacity\/capacity-report\.json/);
  assert.doesNotMatch(workflow, /local-nuget|NuGet\.Config|\.nupkg/i);
});
