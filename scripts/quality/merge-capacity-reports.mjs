import { randomUUID } from "node:crypto";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const expectedFragmentNames = [
  "media-streaming.json",
  "resolved-content.json",
  "webhook-claim.json",
];

function fail(message) {
  throw new Error(message);
}

function parseArguments(arguments_) {
  const options = { fragments: [] };
  for (let index = 0; index < arguments_.length; index += 2) {
    const name = arguments_[index];
    const value = arguments_[index + 1];
    if (!value) fail("Capacity merge arguments require a value.");
    if (name === "--fragment") {
      options.fragments.push(path.resolve(value));
    } else if (["--source-sha", "--sdk-version", "--output"].includes(name)) {
      const property = name.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
      if (options[property]) fail(`Duplicate capacity merge argument: ${name}`);
      options[property] = name === "--output" ? path.resolve(value) : value;
    } else {
      fail(`Unknown capacity merge argument: ${name ?? "<missing>"}`);
    }
  }
  for (const property of ["sourceSha", "sdkVersion", "output"]) {
    if (!options[property]) {
      fail("Usage: merge-capacity-reports.mjs --source-sha <sha> --sdk-version <version> --output <file> --fragment <file> ...");
    }
  }
  return options;
}

function validateIdentity(sourceSha, sdkVersion) {
  if (!/^[0-9a-f]{40}$/.test(sourceSha)) {
    fail("Capacity report source SHA must be exactly 40 lowercase hexadecimal characters.");
  }
  if (sdkVersion !== "10.0.400") {
    fail("Capacity report SDK version must be exactly 10.0.400.");
  }
}

function plainObject(value, label) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    fail(`${label} must be an object.`);
  }
  return value;
}

function exactKeys(value, keys, label) {
  plainObject(value, label);
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    fail(`${label} must contain exactly: ${expected.join(", ")}.`);
  }
}

function positiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0) fail(`${label} must be a positive integer.`);
  return value;
}

function nonNegativeInteger(value, label) {
  if (!Number.isSafeInteger(value) || value < 0) fail(`${label} must be a non-negative integer.`);
  return value;
}

function nonNegativeFinite(value, label) {
  if (!Number.isFinite(value)) fail(`${label} must be finite.`);
  if (value < 0) fail(`${label} must be non-negative.`);
  return value;
}

function boolean(value, label) {
  if (typeof value !== "boolean") fail(`${label} must be boolean.`);
  return value;
}

function requireBlockingPass(fragment, label) {
  if (fragment.blockingInvariantsPassed !== true) {
    fail(`${label} blockingInvariantsPassed must be true.`);
  }
}

function numericSamples(value, sampleCount, label) {
  if (!Array.isArray(value) || value.length !== sampleCount) {
    fail(`${label} must contain exactly sampleCount values.`);
  }
  const samples = value.map((entry, index) => nonNegativeFinite(entry, `${label}[${index}]`));
  for (let index = 1; index < samples.length; index += 1) {
    if (samples[index] < samples[index - 1]) fail(`${label} must be sorted ascending.`);
  }
  return samples;
}

function nearestRank(samples, percentile) {
  return samples[Math.max(0, Math.ceil(percentile * samples.length) - 1)];
}

function validatePercentiles(fragment, samples, label) {
  for (const [property, percentile] of [
    ["p50Milliseconds", 0.50],
    ["p95Milliseconds", 0.95],
    ["p99Milliseconds", 0.99],
  ]) {
    const value = nonNegativeFinite(fragment[property], `${label} ${property}`);
    if (value !== nearestRank(samples, percentile)) {
      fail(`${label} ${property} must agree with the sorted samples.`);
    }
  }
}

function validateBudgetFlag(actual, threshold, passed, label) {
  boolean(passed, `${label} budget flag`);
  if (passed !== (actual <= threshold)) fail(`${label} budget flag must agree with its measurement.`);
}

function validateDatabaseVersion(value, label) {
  if (typeof value !== "string" || !/^PostgreSQL\s+\S/.test(value)) {
    fail(`${label} databaseVersion must identify PostgreSQL.`);
  }
  return value;
}

function validateResolvedContent(fragment) {
  const label = "resolved-content.json";
  exactKeys(fragment, [
    "databaseVersion",
    "datasetCounts",
    "queryCounts",
    "sampleCount",
    "elapsedMilliseconds",
    "p50Milliseconds",
    "p95Milliseconds",
    "p99Milliseconds",
    "p95AtOrBelow250Milliseconds",
    "p99AtOrBelow500Milliseconds",
    "blockingInvariantsPassed",
  ], label);
  const databaseVersion = validateDatabaseVersion(fragment.databaseVersion, label);
  const datasetKeys = [
    "contentItems",
    "publishedVersions",
    "eligibleItems",
    "filteredEligibleItems",
    "deletedOwners",
    "templates",
    "locales",
    "tags",
  ];
  exactKeys(fragment.datasetCounts, datasetKeys, `${label} datasetCounts`);
  const datasetCounts = Object.fromEntries(datasetKeys.map((key) => [
    key,
    nonNegativeInteger(fragment.datasetCounts[key], `${label} datasetCounts.${key}`),
  ]));
  if (datasetCounts.contentItems < 500 || datasetCounts.publishedVersions < 2500) {
    fail(`${label} representative dataset must contain at least 500 items and 2500 published versions.`);
  }
  if (datasetCounts.eligibleItems > datasetCounts.contentItems
    || datasetCounts.filteredEligibleItems > datasetCounts.eligibleItems
    || datasetCounts.deletedOwners > datasetCounts.contentItems
    || datasetCounts.templates === 0
    || datasetCounts.locales === 0
    || datasetCounts.tags === 0) {
    fail(`${label} datasetCounts are inconsistent.`);
  }
  const sampleCount = positiveInteger(fragment.sampleCount, `${label} sampleCount`);
  if (!Array.isArray(fragment.queryCounts) || fragment.queryCounts.length !== sampleCount) {
    fail(`${label} queryCounts must contain exactly sampleCount values.`);
  }
  const queryCounts = fragment.queryCounts.map((value, index) => {
    positiveInteger(value, `${label} queryCounts[${index}]`);
    if (value !== 2) fail(`${label} queryCounts must all be exactly 2.`);
    return value;
  });
  const elapsedMilliseconds = numericSamples(
    fragment.elapsedMilliseconds,
    sampleCount,
    `${label} elapsedMilliseconds`,
  );
  validatePercentiles(fragment, elapsedMilliseconds, label);
  validateBudgetFlag(
    fragment.p95Milliseconds,
    250,
    fragment.p95AtOrBelow250Milliseconds,
    `${label} p95`,
  );
  validateBudgetFlag(
    fragment.p99Milliseconds,
    500,
    fragment.p99AtOrBelow500Milliseconds,
    `${label} p99`,
  );
  requireBlockingPass(fragment, label);
  return { databaseVersion, datasetCounts, sampleCount, queryCounts, elapsedMilliseconds };
}

function validateWebhookClaim(fragment) {
  const label = "webhook-claim.json";
  exactKeys(fragment, [
    "databaseVersion",
    "eligibleRows",
    "batchSize",
    "commandCount",
    "sampleCount",
    "samplesMilliseconds",
    "p50Milliseconds",
    "p95Milliseconds",
    "p99Milliseconds",
    "duplicateCount",
    "overclaimCount",
    "p95AtOrBelow250Milliseconds",
    "blockingInvariantsPassed",
  ], label);
  const databaseVersion = validateDatabaseVersion(fragment.databaseVersion, label);
  const eligibleRows = positiveInteger(fragment.eligibleRows, `${label} eligibleRows`);
  const batchSize = positiveInteger(fragment.batchSize, `${label} batchSize`);
  if (batchSize > 500 || eligibleRows <= 2 * batchSize) {
    fail(`${label} must measure more than twice a supported batch of at most 500 rows.`);
  }
  const commandCount = positiveInteger(fragment.commandCount, `${label} commandCount`);
  const sampleCount = positiveInteger(fragment.sampleCount, `${label} sampleCount`);
  const samplesMilliseconds = numericSamples(
    fragment.samplesMilliseconds,
    sampleCount,
    `${label} samplesMilliseconds`,
  );
  validatePercentiles(fragment, samplesMilliseconds, label);
  const duplicateCount = nonNegativeInteger(fragment.duplicateCount, `${label} duplicateCount`);
  if (duplicateCount !== 0) fail(`${label} duplicateCount must be zero.`);
  const overclaimCount = nonNegativeInteger(fragment.overclaimCount, `${label} overclaimCount`);
  if (overclaimCount !== 0) fail(`${label} overclaimCount must be zero.`);
  validateBudgetFlag(
    fragment.p95Milliseconds,
    250,
    fragment.p95AtOrBelow250Milliseconds,
    `${label} p95`,
  );
  requireBlockingPass(fragment, label);
  return {
    databaseVersion,
    eligibleRows,
    batchSize,
    commandCount,
    sampleCount,
    samplesMilliseconds,
    duplicateCount,
    overclaimCount,
  };
}

function validateMediaStreaming(fragment) {
  const label = "media-streaming.json";
  exactKeys(fragment, [
    "bytes",
    "sampleCount",
    "timeToFirstByteMilliseconds",
    "totalDurationMilliseconds",
    "maximumObservedReadRequestBytes",
    "maximumObservedWriteRequestBytes",
    "timeToFirstByteAtOrBelow500Milliseconds",
    "blockingInvariantsPassed",
  ], label);
  const bytes = positiveInteger(fragment.bytes, `${label} bytes`);
  if (bytes !== 50 * 1024 * 1024) fail(`${label} bytes must equal 50 MiB.`);
  const sampleCount = positiveInteger(fragment.sampleCount, `${label} sampleCount`);
  if (sampleCount !== 1) fail(`${label} sampleCount must equal 1.`);
  const timeToFirstByteMilliseconds = nonNegativeFinite(
    fragment.timeToFirstByteMilliseconds,
    `${label} timeToFirstByteMilliseconds`,
  );
  const totalDurationMilliseconds = nonNegativeFinite(
    fragment.totalDurationMilliseconds,
    `${label} totalDurationMilliseconds`,
  );
  if (timeToFirstByteMilliseconds > totalDurationMilliseconds) {
    fail(`${label} time to first byte cannot exceed total duration.`);
  }
  const maximumObservedReadRequestBytes = positiveInteger(
    fragment.maximumObservedReadRequestBytes,
    `${label} maximumObservedReadRequestBytes`,
  );
  const maximumObservedWriteRequestBytes = positiveInteger(
    fragment.maximumObservedWriteRequestBytes,
    `${label} maximumObservedWriteRequestBytes`,
  );
  if (maximumObservedReadRequestBytes > 128 * 1024 || maximumObservedWriteRequestBytes > 128 * 1024) {
    fail(`${label} observed request sizes must remain at or below 128 KiB.`);
  }
  validateBudgetFlag(
    timeToFirstByteMilliseconds,
    500,
    fragment.timeToFirstByteAtOrBelow500Milliseconds,
    `${label} time to first byte`,
  );
  requireBlockingPass(fragment, label);
  return {
    bytes,
    sampleCount,
    timeToFirstByteMilliseconds,
    totalDurationMilliseconds,
    maximumObservedReadRequestBytes,
    maximumObservedWriteRequestBytes,
  };
}

function readFragments(fragmentPaths) {
  const fragments = new Map();
  for (const fragmentPath of fragmentPaths) {
    const fileName = path.basename(fragmentPath);
    if (!expectedFragmentNames.includes(fileName)) fail(`Unknown capacity fragment: ${fileName}.`);
    if (fragments.has(fileName)) fail(`Duplicate capacity fragment: ${fileName}.`);
    let parsed;
    try {
      parsed = JSON.parse(readFileSync(fragmentPath, "utf8"));
    } catch (error) {
      fail(`Cannot read capacity fragment ${fileName}: ${error instanceof Error ? error.message : String(error)}`);
    }
    fragments.set(fileName, plainObject(parsed, fileName));
  }
  const missing = expectedFragmentNames.filter((name) => !fragments.has(name));
  if (missing.length > 0) fail(`Missing expected capacity fragments: ${missing.join(", ")}.`);
  return fragments;
}

function pathIdentity(filePath) {
  const absolute = path.resolve(filePath);
  const suffix = [];
  let existing = absolute;
  while (!existsSync(existing)) {
    const parent = path.dirname(existing);
    if (parent === existing) fail(`Cannot resolve capacity path: ${filePath}`);
    suffix.unshift(path.basename(existing));
    existing = parent;
  }
  const canonical = path.join(realpathSync.native(existing), ...suffix);
  const details = existsSync(absolute) ? statSync(absolute) : null;
  return {
    canonical: process.platform === "win32" ? canonical.toLowerCase() : canonical,
    physical: details ? `${details.dev}:${details.ino}` : null,
  };
}

function sameIdentity(left, right) {
  return left.canonical === right.canonical
    || (left.physical !== null && right.physical !== null && left.physical === right.physical);
}

function validateOutputPath(output, fragmentPaths) {
  const outputIdentity = pathIdentity(output);
  if (fragmentPaths.some((fragmentPath) => sameIdentity(outputIdentity, pathIdentity(fragmentPath)))) {
    fail("Capacity report output must not alias an input fragment.");
  }
}

export function buildCapacityReport({ fragmentPaths, sourceSha, sdkVersion, generatedAtUtc }) {
  validateIdentity(sourceSha, sdkVersion);
  const fragments = readFragments(fragmentPaths);
  const media = validateMediaStreaming(fragments.get("media-streaming.json"));
  const resolved = validateResolvedContent(fragments.get("resolved-content.json"));
  const webhook = validateWebhookClaim(fragments.get("webhook-claim.json"));
  if (resolved.databaseVersion !== webhook.databaseVersion) {
    fail("Capacity fragment PostgreSQL database identities disagree.");
  }
  if (typeof generatedAtUtc !== "string" || Number.isNaN(Date.parse(generatedAtUtc))) {
    fail("Capacity report generatedAtUtc must be an ISO-8601 timestamp.");
  }
  return {
    schema: "cmsify.capacity.v1",
    sourceSha,
    sdkVersion,
    databaseVersion: resolved.databaseVersion,
    generatedAtUtc,
    datasets: {
      mediaStreaming: { bytes: media.bytes },
      resolvedContent: resolved.datasetCounts,
      webhookClaim: { eligibleRows: webhook.eligibleRows, batchSize: webhook.batchSize },
    },
    measurements: {
      mediaStreaming: {
        sampleCount: media.sampleCount,
        timeToFirstByteMilliseconds: media.timeToFirstByteMilliseconds,
        totalDurationMilliseconds: media.totalDurationMilliseconds,
        maximumObservedReadRequestBytes: media.maximumObservedReadRequestBytes,
        maximumObservedWriteRequestBytes: media.maximumObservedWriteRequestBytes,
      },
      resolvedContent: {
        sampleCount: resolved.sampleCount,
        queryCounts: resolved.queryCounts,
        elapsedMilliseconds: resolved.elapsedMilliseconds,
        p50Milliseconds: fragments.get("resolved-content.json").p50Milliseconds,
        p95Milliseconds: fragments.get("resolved-content.json").p95Milliseconds,
        p99Milliseconds: fragments.get("resolved-content.json").p99Milliseconds,
      },
      webhookClaim: {
        sampleCount: webhook.sampleCount,
        commandCount: webhook.commandCount,
        samplesMilliseconds: webhook.samplesMilliseconds,
        p50Milliseconds: fragments.get("webhook-claim.json").p50Milliseconds,
        p95Milliseconds: fragments.get("webhook-claim.json").p95Milliseconds,
        p99Milliseconds: fragments.get("webhook-claim.json").p99Milliseconds,
        duplicateCount: webhook.duplicateCount,
        overclaimCount: webhook.overclaimCount,
      },
    },
    diagnosticBudgets: {
      mediaStreamingTimeToFirstByte: {
        actualMilliseconds: media.timeToFirstByteMilliseconds,
        thresholdMilliseconds: 500,
        passed: fragments.get("media-streaming.json").timeToFirstByteAtOrBelow500Milliseconds,
      },
      resolvedContentP95: {
        actualMilliseconds: fragments.get("resolved-content.json").p95Milliseconds,
        thresholdMilliseconds: 250,
        passed: fragments.get("resolved-content.json").p95AtOrBelow250Milliseconds,
      },
      resolvedContentP99: {
        actualMilliseconds: fragments.get("resolved-content.json").p99Milliseconds,
        thresholdMilliseconds: 500,
        passed: fragments.get("resolved-content.json").p99AtOrBelow500Milliseconds,
      },
      webhookClaimP95: {
        actualMilliseconds: fragments.get("webhook-claim.json").p95Milliseconds,
        thresholdMilliseconds: 250,
        passed: fragments.get("webhook-claim.json").p95AtOrBelow250Milliseconds,
      },
    },
    blockingInvariantsPassed: true,
  };
}

const defaultOutputOperations = {
  existsSync,
  mkdirSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
  temporaryPath(destination, purpose) {
    return path.join(
      path.dirname(destination),
      `.${path.basename(destination)}.${process.pid}.${randomUUID()}.${purpose}`,
    );
  },
};

export function replaceCapacityReport(destination, contents, operations = defaultOutputOperations) {
  if (operations.existsSync(destination) && !operations.statSync(destination).isFile()) {
    fail(`Capacity report output destination must be a file: ${destination}`);
  }
  operations.mkdirSync(path.dirname(destination), { recursive: true });
  const stage = operations.temporaryPath(destination, "stage");
  const backup = operations.temporaryPath(destination, "backup");
  let staged = false;
  let backedUp = false;
  let installed = false;
  try {
    operations.writeFileSync(stage, contents, { encoding: "utf8", flag: "wx" });
    staged = true;
    if (operations.existsSync(destination)) {
      operations.renameSync(destination, backup);
      backedUp = true;
    }
    operations.renameSync(stage, destination);
    staged = false;
    installed = true;
  } catch (primary) {
    const rollbackFailures = [];
    if (installed) {
      try {
        operations.rmSync(destination, { force: true });
        installed = false;
      } catch (error) {
        rollbackFailures.push(error);
      }
    }
    if (backedUp) {
      try {
        operations.renameSync(backup, destination);
        backedUp = false;
      } catch (error) {
        rollbackFailures.push(error);
      }
    }
    if (staged) {
      try {
        operations.rmSync(stage, { force: true });
        staged = false;
      } catch (error) {
        rollbackFailures.push(error);
      }
    }
    throw new AggregateError(
      [primary, ...rollbackFailures],
      `Capacity output transaction failed before commit: ${primary instanceof Error ? primary.message : String(primary)}${rollbackFailures.length > 0 ? `; rollback failures: ${rollbackFailures.map((error) => error instanceof Error ? error.message : String(error)).join("; ")}` : "; rollback completed"}`,
      { cause: primary },
    );
  }
  if (backedUp) {
    try {
      operations.rmSync(backup, { force: true });
    } catch (error) {
      throw new AggregateError(
        [error],
        `Capacity report committed, but backup cleanup failed: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  }
}

export function missedDiagnosticBudgetNames(report) {
  return Object.entries(report.diagnosticBudgets)
    .filter(([, budget]) => !budget.passed)
    .map(([name]) => name);
}

export function mergeCapacityReports({ fragmentPaths, sourceSha, sdkVersion, output, now = new Date() }) {
  validateOutputPath(output, fragmentPaths);
  const report = buildCapacityReport({
    fragmentPaths,
    sourceSha,
    sdkVersion,
    generatedAtUtc: now.toISOString(),
  });
  replaceCapacityReport(output, `${JSON.stringify(report, null, 2)}\n`);
  return report;
}

export function main(arguments_) {
  const options = parseArguments(arguments_);
  const report = mergeCapacityReports({
    fragmentPaths: options.fragments,
    sourceSha: options.sourceSha,
    sdkVersion: options.sdkVersion,
    output: options.output,
  });
  const missedBudgets = missedDiagnosticBudgetNames(report);
  if (missedBudgets.length > 0) {
    process.stderr.write(`::warning::Diagnostic capacity budgets missed: ${missedBudgets.join(", ")}\n`);
  }
  return report;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
