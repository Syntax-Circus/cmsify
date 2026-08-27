import { copyFile, cp, mkdir, mkdtemp, readFile, rm } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { verifyFixtureChecksums } from "./checksums.mjs";
import { compareFixtureTrees, generateFixture } from "./fixture.mjs";
import { loadFixtureManifest, REQUIRED_SCENARIOS } from "./manifest.mjs";

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

async function loadExpectedScenarioIds(fixtureDirectory, expectedDataFile) {
  let expected;
  try {
    expected = JSON.parse(await readFile(resolve(fixtureDirectory, expectedDataFile), "utf8"));
  } catch {
    throw new Error("expected.json must contain valid JSON.");
  }
  assert(expected !== null && typeof expected === "object" && !Array.isArray(expected), "expected.json must be an object.");
  assert(Array.isArray(expected.scenarios), "expected.json must contain a scenarios array.");

  const scenarioIds = [];
  for (const scenario of expected.scenarios) {
    assert(scenario !== null && typeof scenario === "object" && !Array.isArray(scenario) && typeof scenario.id === "string", "expected.json scenarios must each contain an id.");
    scenarioIds.push(scenario.id);
  }
  assert(new Set(scenarioIds).size === scenarioIds.length, "expected.json scenario IDs must be unique.");
  assert(scenarioIds.length === REQUIRED_SCENARIOS.size && scenarioIds.every((id) => REQUIRED_SCENARIOS.has(id)), "expected.json scenario IDs must provide exact required coverage.");
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

async function prepareGenerationDirectory(repositoryRoot, fixtureDirectory, prefix) {
  const runDirectory = resolve(repositoryRoot, "tests", "upgrade", ".runs");
  await mkdir(runDirectory, { recursive: true });
  const output = await mkdtemp(resolve(runDirectory, prefix));
  await Promise.all([
    copyFile(resolve(fixtureDirectory, "manifest.json"), resolve(output, "manifest.json")),
    copyFile(resolve(fixtureDirectory, "expected.json"), resolve(output, "expected.json")),
  ]);
  return output;
}

async function installGeneratedFixture(generatedDirectory, fixtureDirectory) {
  await copyFile(resolve(generatedDirectory, "database.sql"), resolve(fixtureDirectory, "database.sql"));
  await rm(resolve(fixtureDirectory, "media"), { force: true, recursive: true });
  await cp(resolve(generatedDirectory, "media"), resolve(fixtureDirectory, "media"), { recursive: true, force: false, errorOnExist: true });
  await copyFile(resolve(generatedDirectory, "SHA256SUMS"), resolve(fixtureDirectory, "SHA256SUMS"));
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
    await loadExpectedScenarioIds(fixtureDirectory, manifest.expectedDataFile);
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
