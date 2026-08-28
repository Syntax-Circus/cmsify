import { randomBytes } from "node:crypto";
import { copyFile, cp, mkdir, mkdtemp, rename as renameDirectory, rm } from "node:fs/promises";
import { basename, dirname, resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { verifyFixtureChecksums } from "./checksums.mjs";
import { loadExpectedData } from "./expected.mjs";
import { compareFixtureTrees, generateFixture, runWithCleanup } from "./fixture.mjs";
import { loadFixtureManifest, verifyFixtureGenerationProvenance } from "./manifest.mjs";
import { rehearse, validateCandidateInput } from "./rehearsal.mjs";
import {
  fetchDockerLinuxAmd64Descriptor,
  fetchPublishedStable01Releases,
  selectLatestPublishedStable01,
  validateReleaseCandidateVersion,
  verifyReleaseBaseline,
} from "./release-baseline.mjs";

const USAGE = [
  "Usage:",
  "  <verify-fixture|generate-fixture> --fixture <fixture-directory> [--check]",
  "  rehearse --fixture <fixture-directory> --candidate-image <ref> --candidate-version <semver> --candidate-source-sha <40hex>",
  "  verify-release-baseline --fixture <fixture-directory> --candidate-version <semver> [--github-token-env GITHUB_TOKEN]",
].join("\n");

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function parseArguments(arguments_, cwd = process.cwd()) {
  assert(Array.isArray(arguments_) && arguments_.length >= 1, USAGE);
  if (arguments_[0] === "rehearse") {
    assert(arguments_.length === 9, USAGE);
    const values = new Map();
    for (let index = 1; index < arguments_.length; index += 2) {
      const name = arguments_[index];
      const value = arguments_[index + 1];
      assert(["--fixture", "--candidate-image", "--candidate-version", "--candidate-source-sha"].includes(name) && !values.has(name), USAGE);
      assert(typeof value === "string" && value.length > 0 && !/[\r\n\0]/.test(value), USAGE);
      values.set(name, value);
    }
    assert(values.size === 4, USAGE);
    const candidateImage = values.get("--candidate-image");
    const candidateVersion = values.get("--candidate-version");
    const candidateSourceSha = values.get("--candidate-source-sha");
    validateCandidateInput({ candidateImage, candidateVersion, candidateSourceSha });
    return {
      command: "rehearse",
      fixtureDirectory: resolve(cwd, values.get("--fixture")),
      candidateImage,
      candidateVersion,
      candidateSourceSha,
    };
  }

  if (arguments_[0] === "verify-release-baseline") {
    assert(arguments_.length === 5 || arguments_.length === 7, USAGE);
    const values = new Map();
    for (let index = 1; index < arguments_.length; index += 2) {
      const name = arguments_[index];
      const value = arguments_[index + 1];
      assert(["--fixture", "--candidate-version", "--github-token-env"].includes(name) && !values.has(name), USAGE);
      assert(typeof value === "string" && value.length > 0 && !/[\r\n\0]/.test(value), USAGE);
      values.set(name, value);
    }
    assert(values.has("--fixture") && values.has("--candidate-version"), USAGE);
    const githubTokenEnvironment = values.get("--github-token-env");
    assert(githubTokenEnvironment === undefined || /^[A-Za-z_][A-Za-z0-9_]*$/.test(githubTokenEnvironment), USAGE);
    const candidateVersion = validateReleaseCandidateVersion(values.get("--candidate-version"));
    return {
      command: "verify-release-baseline",
      fixtureDirectory: resolve(cwd, values.get("--fixture")),
      candidateVersion,
      githubTokenEnvironment,
    };
  }

  assert(arguments_.length >= 3 && ["verify-fixture", "generate-fixture"].includes(arguments_[0]), USAGE);
  assert(arguments_[1] === "--fixture" && arguments_[2].length > 0, USAGE);
  const options = new Set(arguments_.slice(3));
  assert([...options].every((option) => option === "--check"), USAGE);
  assert(arguments_.slice(3).length === options.size, USAGE);
  assert(arguments_[0] === "generate-fixture" || options.size === 0, USAGE);
  return { command: arguments_[0], fixtureDirectory: resolve(cwd, arguments_[2]), check: options.has("--check") };
}

function sanitize(error, paths, { rehearsal = false } = {}) {
  const message = error instanceof Error ? error.message : "Fixture verification failed.";
  if (rehearsal) {
    if (message === USAGE || /^Usage:/i.test(message)) return USAGE;
    if (/cancel(?:led|ation)|aborted/i.test(message)) return "Upgrade rehearsal was cancelled.";
    if (/build metadata/i.test(message)) return "Candidate version build metadata is not accepted.";
    if (/valid SemVer/i.test(message)) return "Candidate version must be valid SemVer.";
    if (/candidate image reference is malformed/i.test(message)) return "Candidate image reference is malformed.";
    if (/candidate source SHA/i.test(message)) return "Candidate source SHA is invalid.";
    if (error?.name === "RehearsalFailure" && typeof error.message === "string" && error.message.length <= 256) return error.message;
    return "Upgrade rehearsal failed; diagnostic detail withheld.";
  }
  let sanitized = message;
  for (const [path, replacement] of paths) {
    if (typeof path !== "string" || path.length === 0) continue;
    const escapedDirectory = path.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    sanitized = sanitized.replace(new RegExp(escapedDirectory, "gi"), replacement);
  }
  return sanitized
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => line.replace(/[\r\n]/g, " "))
    .join("\n")
    .replace(/cmsify_[a-z0-9._-]{12,}/gi, "<redacted>")
    .replace(/(password|secret|token|encryption[_-]?key)\s*[=:]\s*[^\s,;]+/gi, "$1=<redacted>")
    .slice(0, 4_096);
}

export async function prepareGenerationDirectory(repositoryRoot, fixtureDirectory, prefix, { remove = rm } = {}) {
  const runDirectory = resolve(repositoryRoot, "tests", "upgrade", ".runs");
  await mkdir(runDirectory, { recursive: true });
  const output = await mkdtemp(resolve(runDirectory, prefix));
  let prepared = false;
  return runWithCleanup(async () => {
    await copyFile(resolve(fixtureDirectory, "manifest.json"), resolve(output, "manifest.json"));
    await copyFile(resolve(fixtureDirectory, "expected.json"), resolve(output, "expected.json"));
    prepared = true;
    return output;
  }, async () => {
    if (!prepared) await remove(output, { force: true, recursive: true });
  });
}

function siblingPath(fixtureDirectory, purpose) {
  const nonce = randomBytes(8).toString("hex");
  return resolve(dirname(fixtureDirectory), `.${basename(fixtureDirectory)}.${purpose}-${nonce}`);
}

async function removeFixtureSiblings(paths, remove) {
  const failures = [];
  for (const path of paths) {
    try {
      await remove(path, { force: true, recursive: true });
    } catch (error) {
      failures.push(error);
    }
  }
  if (failures.length === 1) throw failures[0];
  if (failures.length > 1) {
    const message = failures[0] instanceof Error ? failures[0].message : "Fixture sibling cleanup failed.";
    throw new AggregateError(failures, message, { cause: failures[0] });
  }
}

export async function installGeneratedFixture(generatedDirectory, fixtureDirectory, { rename = renameDirectory, remove = rm } = {}) {
  const replacement = siblingPath(fixtureDirectory, "replacement");
  const backup = siblingPath(fixtureDirectory, "backup");
  let backupExists = false;
  return runWithCleanup(async () => {
    await cp(generatedDirectory, replacement, { recursive: true, force: false, errorOnExist: true });
    const replacementManifest = loadFixtureManifest(replacement);
    await loadExpectedData(replacement, replacementManifest);
    await verifyFixtureChecksums(replacement, replacementManifest);

    await rename(fixtureDirectory, backup);
    backupExists = true;
    try {
      await rename(replacement, fixtureDirectory);
    } catch (replacementFailure) {
      try {
        await rename(backup, fixtureDirectory);
        backupExists = false;
      } catch (restoreFailure) {
        throw new AggregateError([replacementFailure, restoreFailure], replacementFailure.message, { cause: replacementFailure });
      }
      throw replacementFailure;
    }

    await remove(backup, { force: true, recursive: true });
    backupExists = false;
  }, async () => {
    const cleanupPaths = [replacement];
    if (!backupExists) cleanupPaths.push(backup);
    await removeFixtureSiblings(cleanupPaths, remove);
  });
}

async function generateCommand(repositoryRoot, fixtureDirectory, check) {
  const directories = [];
  try {
    const first = await prepareGenerationDirectory(repositoryRoot, fixtureDirectory, "fixture-first-");
    directories.push(first);
    const firstResult = await generateFixture({ repositoryRoot, fixtureDirectory: first, keepDiagnostics: false });
    if (!check) {
      await installGeneratedFixture(first, fixtureDirectory);
      process.stdout.write(`Fixture generated for v0.1.3 (${firstResult.mediaAggregateSha256}).\n`);
      return;
    }

    const second = await prepareGenerationDirectory(repositoryRoot, fixtureDirectory, "fixture-second-");
    directories.push(second);
    const secondResult = await generateFixture({ repositoryRoot, fixtureDirectory: second, keepDiagnostics: false });
    await compareFixtureTrees(first, second);
    await compareFixtureTrees(second, fixtureDirectory);
    process.stdout.write(`Fixture regeneration is byte-identical (${secondResult.mediaAggregateSha256}).\n`);
  } finally {
    await Promise.all(directories.map((directory) => rm(directory, { force: true, recursive: true })));
  }
}

/**
 * Runs an upgrade-fixture command and returns its process exit code.
 * @param {string[]} arguments_
 * @returns {Promise<number>}
 */
export async function main(arguments_, runtime = {}) {
  let fixtureDirectory;
  const cwd = resolve(runtime.cwd ?? process.cwd());
  const stdout = runtime.stdout ?? process.stdout;
  const stderr = runtime.stderr ?? process.stderr;
  try {
    const parsed = parseArguments(arguments_, cwd);
    fixtureDirectory = parsed.fixtureDirectory;
    if (parsed.command === "verify-release-baseline") {
      const environment = runtime.env ?? process.env;
      const githubToken = parsed.githubTokenEnvironment === undefined ? undefined : environment[parsed.githubTokenEnvironment];
      assert(parsed.githubTokenEnvironment === undefined || (typeof githubToken === "string" && githubToken.length > 0), `GitHub token environment variable ${parsed.githubTokenEnvironment} is not set.`);
      const manifest = loadFixtureManifest(fixtureDirectory);
      const fetchReleases = runtime.fetchPublishedStable01Releases ?? fetchPublishedStable01Releases;
      const fetchDescriptor = runtime.fetchDockerLinuxAmd64Descriptor ?? fetchDockerLinuxAmd64Descriptor;
      const releases = await fetchReleases({ githubToken, fetchImpl: runtime.fetchImpl });
      const latest = selectLatestPublishedStable01(releases, parsed.candidateVersion);
      const dockerDescriptor = await fetchDescriptor({
        repository: manifest.baseline.apiImage.repository,
        tag: latest.version,
        fetchImpl: runtime.fetchImpl,
      });
      const result = verifyReleaseBaseline({
        candidateVersion: parsed.candidateVersion,
        fixtureManifest: manifest,
        githubReleases: releases,
        dockerDescriptor,
      });
      stdout.write(`Release baseline verified for ${result.baselineVersion} (${result.sourceSha}, ${result.apiDigest}).\n`);
      return 0;
    }
    if (parsed.command === "rehearse") {
      const rehearseCommand = runtime.rehearse ?? rehearse;
      const controller = runtime.signal === undefined ? new AbortController() : undefined;
      const signal = runtime.signal ?? controller.signal;
      assert(signal instanceof AbortSignal, "Rehearsal cancellation signal must be an AbortSignal.");
      const cancel = () => controller.abort();
      if (controller) {
        process.once("SIGINT", cancel);
        process.once("SIGTERM", cancel);
      }
      try {
        const report = await rehearseCommand({
          repositoryRoot: cwd,
          fixtureDirectory,
          candidateImage: parsed.candidateImage,
          candidateVersion: parsed.candidateVersion,
          candidateSourceSha: parsed.candidateSourceSha,
          signal,
        });
        stdout.write(`Rehearsal passed for ${parsed.candidateVersion} (${report.runId}). Report: artifacts/upgrade-tests/${report.runId}/report.json.\n`);
        return 0;
      } finally {
        if (controller) {
          process.removeListener("SIGINT", cancel);
          process.removeListener("SIGTERM", cancel);
        }
      }
    }
    if (parsed.command === "generate-fixture") {
      await generateCommand(cwd, fixtureDirectory, parsed.check);
      return 0;
    }
    const manifest = loadFixtureManifest(fixtureDirectory);
    verifyFixtureGenerationProvenance(cwd, manifest);
    await loadExpectedData(fixtureDirectory, manifest);
    await verifyFixtureChecksums(fixtureDirectory, manifest);
    stdout.write(`Fixture verified for ${manifest.baseline.version}.\n`);
    return 0;
  } catch (error) {
    stderr.write(`${sanitize(error, [[fixtureDirectory, "<fixture>"], [cwd, "<repository>"]], { rehearsal: arguments_[0] === "rehearse" })}\n`);
    return 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then((exitCode) => {
    process.exitCode = exitCode;
  });
}
