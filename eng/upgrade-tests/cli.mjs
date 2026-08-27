import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

import { verifyFixtureChecksums } from "./checksums.mjs";
import { loadFixtureManifest, REQUIRED_SCENARIOS } from "./manifest.mjs";

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function parseVerifyFixtureArguments(arguments_) {
  assert(arguments_.length === 3 && arguments_[0] === "verify-fixture" && arguments_[1] === "--fixture", "Usage: verify-fixture --fixture <fixture-directory>.");
  assert(arguments_[2].length > 0, "Usage: verify-fixture --fixture <fixture-directory>.");
  return resolve(arguments_[2]);
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

/**
 * Runs an upgrade-fixture command and returns its process exit code.
 * @param {string[]} arguments_
 * @returns {Promise<number>}
 */
export async function main(arguments_) {
  let fixtureDirectory;
  try {
    fixtureDirectory = parseVerifyFixtureArguments(arguments_);
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
