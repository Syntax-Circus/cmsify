import { randomBytes } from "node:crypto";
import { copyFile, cp, mkdir, mkdtemp, rename as renameDirectory, rm } from "node:fs/promises";
import { basename, dirname, resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { verifyFixtureChecksums } from "./checksums.mjs";
import { loadExpectedData } from "./expected.mjs";
import { compareFixtureTrees, generateFixture } from "./fixture.mjs";
import { loadFixtureManifest } from "./manifest.mjs";

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function parseArguments(arguments_) {
  const usage = "Usage: <verify-fixture|generate-fixture> --fixture <fixture-directory> [--check].";
  assert(arguments_.length >= 3 && ["verify-fixture", "generate-fixture"].includes(arguments_[0]), usage);
  assert(arguments_[1] === "--fixture" && arguments_[2].length > 0, usage);
  const options = new Set(arguments_.slice(3));
  assert([...options].every((option) => option === "--check"), usage);
  assert(arguments_.slice(3).length === options.size, usage);
  assert(arguments_[0] === "generate-fixture" || options.size === 0, usage);
  return { command: arguments_[0], fixtureDirectory: resolve(arguments_[2]), check: options.has("--check") };
}

function sanitize(error, fixtureDirectory) {
  const message = error instanceof Error ? error.message : "Fixture verification failed.";
  const escapedDirectory = fixtureDirectory.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return message
    .split(/\r?\n/)
    .filter(Boolean)
    .map((line) => line.replace(new RegExp(escapedDirectory, "gi"), "<fixture>"))
    .map((line) => line.replace(/[\r\n]/g, " "))
    .join("\n");
}

export async function prepareGenerationDirectory(repositoryRoot, fixtureDirectory, prefix) {
  const runDirectory = resolve(repositoryRoot, "tests", "upgrade", ".runs");
  await mkdir(runDirectory, { recursive: true });
  const output = await mkdtemp(resolve(runDirectory, prefix));
  try {
    await copyFile(resolve(fixtureDirectory, "manifest.json"), resolve(output, "manifest.json"));
    await copyFile(resolve(fixtureDirectory, "expected.json"), resolve(output, "expected.json"));
    return output;
  } catch (error) {
    await rm(output, { force: true, recursive: true });
    throw error;
  }
}

function siblingPath(fixtureDirectory, purpose) {
  const nonce = randomBytes(8).toString("hex");
  return resolve(dirname(fixtureDirectory), `.${basename(fixtureDirectory)}.${purpose}-${nonce}`);
}

export async function installGeneratedFixture(generatedDirectory, fixtureDirectory, { rename = renameDirectory } = {}) {
  const replacement = siblingPath(fixtureDirectory, "replacement");
  const backup = siblingPath(fixtureDirectory, "backup");
  let backupExists = false;
  try {
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

    await rm(backup, { force: true, recursive: true });
    backupExists = false;
  } finally {
    await rm(replacement, { force: true, recursive: true });
    if (!backupExists) await rm(backup, { force: true, recursive: true });
  }
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
export async function main(arguments_) {
  let fixtureDirectory;
  try {
    const parsed = parseArguments(arguments_);
    fixtureDirectory = parsed.fixtureDirectory;
    if (parsed.command === "generate-fixture") {
      await generateCommand(resolve(process.cwd()), fixtureDirectory, parsed.check);
      return 0;
    }
    const manifest = loadFixtureManifest(fixtureDirectory);
    await loadExpectedData(fixtureDirectory, manifest);
    await verifyFixtureChecksums(fixtureDirectory, manifest);
    process.stdout.write(`Fixture verified for ${manifest.baseline.version}.\n`);
    return 0;
  } catch (error) {
    process.stderr.write(`${sanitize(error, fixtureDirectory ?? process.cwd())}\n`);
    return 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then((exitCode) => {
    process.exitCode = exitCode;
  });
}
