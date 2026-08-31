import { spawnSync } from "node:child_process";
import {
  lstatSync,
  mkdirSync,
  readdirSync,
  realpathSync,
  rmdirSync,
  unlinkSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import {
  mergeCapacityReports,
  missedDiagnosticBudgetNames,
} from "./merge-capacity-reports.mjs";

const capacityProjects = [
  "tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj",
  "tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj",
  "sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj",
];

function fail(message) {
  throw new Error(message);
}

function defaultExecute(command, arguments_, options) {
  return spawnSync(command, arguments_, {
    cwd: options.cwd,
    encoding: "utf8",
    env: options.env,
    windowsHide: true,
  });
}

function executeChecked(execute, command, arguments_, options, label) {
  const result = execute(command, arguments_, options);
  if (result.error) throw result.error;
  if (result.status !== 0) {
    const details = [result.stdout, result.stderr]
      .filter((value) => typeof value === "string" && value.trim() !== "")
      .map((value) => value.trim())
      .join("\n");
    fail(`${label} failed with exit code ${result.status}${details ? `:\n${details}` : "."}`);
  }
  return typeof result.stdout === "string" ? result.stdout.trim() : "";
}

function normalizedPath(filePath) {
  const resolved = path.resolve(filePath);
  return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}

function isWithin(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === ""
    || (!path.isAbsolute(relative) && relative !== ".." && !relative.startsWith(`..${path.sep}`));
}

function lstatIfPresent(filePath) {
  try {
    return lstatSync(filePath);
  } catch (error) {
    if (error?.code === "ENOENT") return null;
    throw error;
  }
}

function assertPhysicalEntry(entryPath, repositoryRoot, expectedKind) {
  const details = lstatSync(entryPath);
  if (details.isSymbolicLink()) {
    fail(`Refusing capacity cleanup through a symbolic link, junction, or reparse point: ${entryPath}`);
  }
  if (expectedKind === "directory" && !details.isDirectory()) {
    fail(`Capacity cleanup path component must be a directory: ${entryPath}`);
  }
  if (expectedKind === "file" && !details.isFile()) {
    fail(`Capacity cleanup entry must be a regular file: ${entryPath}`);
  }
  const physical = realpathSync.native(entryPath);
  if (!isWithin(repositoryRoot, physical)) {
    fail(`Capacity cleanup path resolves outside the repository: ${entryPath}`);
  }
  if (normalizedPath(physical) !== normalizedPath(entryPath)) {
    fail(`Refusing capacity cleanup through a reparse point: ${entryPath}`);
  }
  return details;
}

function canonicalRepositoryRoot(repositoryRoot) {
  const requestedRoot = path.resolve(repositoryRoot);
  const details = lstatSync(requestedRoot);
  if (details.isSymbolicLink()) {
    fail(`Repository root must not be a symbolic link, junction, or reparse point: ${requestedRoot}`);
  }
  if (!details.isDirectory()) fail(`Repository root must be a directory: ${requestedRoot}`);
  return realpathSync.native(requestedRoot);
}

function ensureSafeDirectory(repositoryRoot, directoryPath) {
  const relative = path.relative(repositoryRoot, directoryPath);
  if (path.isAbsolute(relative) || relative === ".." || relative.startsWith(`..${path.sep}`)) {
    fail(`Capacity directory must remain beneath the repository: ${directoryPath}`);
  }
  let current = repositoryRoot;
  for (const segment of relative.split(path.sep).filter(Boolean)) {
    current = path.join(current, segment);
    if (lstatIfPresent(current) === null) {
      try {
        mkdirSync(current);
      } catch (error) {
        if (error?.code !== "EEXIST") throw error;
      }
    }
    assertPhysicalEntry(current, repositoryRoot, "directory");
  }
}

function assertSafeTree(entryPath, repositoryRoot) {
  const details = lstatSync(entryPath);
  if (details.isSymbolicLink()) {
    fail(`Refusing capacity cleanup through a symbolic link, junction, or reparse point: ${entryPath}`);
  }
  if (details.isDirectory()) {
    assertPhysicalEntry(entryPath, repositoryRoot, "directory");
    for (const name of readdirSync(entryPath).sort()) {
      assertSafeTree(path.join(entryPath, name), repositoryRoot);
    }
    return;
  }
  if (details.isFile()) {
    assertPhysicalEntry(entryPath, repositoryRoot, "file");
    return;
  }
  fail(`Capacity cleanup tree contains a non-file entry: ${entryPath}`);
}

function removeSafeTree(entryPath, repositoryRoot) {
  const details = lstatSync(entryPath);
  if (details.isSymbolicLink()) {
    fail(`Refusing capacity cleanup through a symbolic link, junction, or reparse point: ${entryPath}`);
  }
  if (details.isDirectory()) {
    assertPhysicalEntry(entryPath, repositoryRoot, "directory");
    for (const name of readdirSync(entryPath).sort()) {
      removeSafeTree(path.join(entryPath, name), repositoryRoot);
    }
    assertPhysicalEntry(entryPath, repositoryRoot, "directory");
    rmdirSync(entryPath);
    return;
  }
  if (details.isFile()) {
    assertPhysicalEntry(entryPath, repositoryRoot, "file");
    unlinkSync(entryPath);
    return;
  }
  fail(`Capacity cleanup tree contains a non-file entry: ${entryPath}`);
}

function prepareFragmentDirectory(repositoryRoot) {
  const root = canonicalRepositoryRoot(repositoryRoot);
  const artifactsRoot = path.join(root, "artifacts");
  const capacityRoot = path.join(artifactsRoot, "capacity");
  const fragmentDirectory = path.join(capacityRoot, "fragments");
  for (const existingPath of [artifactsRoot, capacityRoot, fragmentDirectory]) {
    if (lstatIfPresent(existingPath) !== null) {
      assertPhysicalEntry(existingPath, root, "directory");
    }
  }
  ensureSafeDirectory(root, capacityRoot);
  if (lstatIfPresent(fragmentDirectory) !== null) {
    assertSafeTree(fragmentDirectory, root);
    removeSafeTree(fragmentDirectory, root);
  }
  mkdirSync(fragmentDirectory);
  assertPhysicalEntry(fragmentDirectory, root, "directory");
  return { root, capacityRoot, fragmentDirectory };
}

function formatMilliseconds(value) {
  return Number(value).toFixed(3);
}

function passWord(value) {
  return value ? "passed" : "missed";
}

export function renderCapacityMarkdown(report) {
  const media = report.measurements.mediaStreaming;
  const resolved = report.measurements.resolvedContent;
  const webhook = report.measurements.webhookClaim;
  const budgets = report.diagnosticBudgets;
  return [
    "# Capacity trend",
    "",
    `Source SHA: \`${report.sourceSha}\`  `,
    `SDK: \`${report.sdkVersion}\`  `,
    `Database: \`${report.databaseVersion}\``,
    "",
    "| Scenario | Samples | Latency | Diagnostic budget |",
    "| --- | ---: | ---: | --- |",
    `| Media streaming | ${media.sampleCount} | TTFB ${formatMilliseconds(media.timeToFirstByteMilliseconds)} ms | TTFB <= 500 ms: ${passWord(budgets.mediaStreamingTimeToFirstByte.passed)} |`,
    `| Resolved content | ${resolved.sampleCount} | p95 ${formatMilliseconds(resolved.p95Milliseconds)} ms; p99 ${formatMilliseconds(resolved.p99Milliseconds)} ms | p95 <= 250 ms: ${passWord(budgets.resolvedContentP95.passed)}; p99 <= 500 ms: ${passWord(budgets.resolvedContentP99.passed)} |`,
    `| Webhook claim | ${webhook.sampleCount} | p95 ${formatMilliseconds(webhook.p95Milliseconds)} ms | p95 <= 250 ms: ${passWord(budgets.webhookClaimP95.passed)} |`,
    "",
  ].join("\n");
}

export function runCapacity({
  repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../.."),
  execute = defaultExecute,
  now = new Date(),
} = {}) {
  const requestedRoot = canonicalRepositoryRoot(repositoryRoot);
  const sourceSha = executeChecked(
    execute,
    "git",
    ["rev-parse", "HEAD"],
    { cwd: requestedRoot, env: process.env },
    "git source identity",
  );
  if (!/^[0-9a-f]{40}$/.test(sourceSha)) {
    fail("git rev-parse HEAD must return exactly 40 lowercase hexadecimal characters.");
  }
  const sdkVersion = executeChecked(
    execute,
    "dotnet",
    ["--version"],
    { cwd: requestedRoot, env: process.env },
    ".NET SDK identity",
  );
  if (sdkVersion !== "10.0.400") fail("dotnet --version must return exactly 10.0.400.");

  const { root, capacityRoot, fragmentDirectory } = prepareFragmentDirectory(requestedRoot);
  const capacityEnvironment = {
    ...process.env,
    CMSIFY_CAPACITY_TIMING: "true",
    CMSIFY_CAPACITY_REPORT_DIR: fragmentDirectory,
  };
  for (const project of capacityProjects) {
    executeChecked(
      execute,
      "dotnet",
      [
        "test",
        project,
        "--configuration", "Release",
        "--no-build",
        "--filter", "Category=Capacity",
      ],
      { cwd: root, env: capacityEnvironment },
      `${project} capacity invariants`,
    );
  }

  const output = path.join(capacityRoot, "capacity-report.json");
  const report = mergeCapacityReports({
    fragmentPaths: [
      path.join(fragmentDirectory, "resolved-content.json"),
      path.join(fragmentDirectory, "webhook-claim.json"),
      path.join(fragmentDirectory, "media-streaming.json"),
    ],
    sourceSha,
    sdkVersion,
    output,
    now,
  });
  return {
    report,
    markdown: renderCapacityMarkdown(report),
    missedBudgets: missedDiagnosticBudgetNames(report),
  };
}

export function main() {
  const result = runCapacity();
  process.stdout.write(result.markdown);
  if (result.missedBudgets.length > 0) {
    process.stderr.write(`::warning::Diagnostic capacity budgets missed: ${result.missedBudgets.join(", ")}\n`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
