import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const readRepositoryFile = (relativePath) =>
  readFileSync(path.join(repositoryRoot, relativePath), "utf8");
const getTrackedFiles = (...pathspecs) =>
  execFileSync("git", ["ls-files", "--", ...pathspecs], {
    cwd: repositoryRoot,
    encoding: "utf8",
  })
    .split(/\r?\n/)
    .filter(Boolean);

test("pins the SDK used for locked solution restores", () => {
  const globalJson = JSON.parse(readRepositoryFile("global.json"));

  assert.deepEqual(globalJson.sdk, {
    version: "10.0.400",
    rollForward: "latestPatch",
    allowPrerelease: false,
  });
});

test("enables lock files and maintains one for every solution project", () => {
  const directoryBuildProps = readRepositoryFile("Directory.Build.props");
  const solution = readRepositoryFile("Cmsify.slnx");
  const projectPaths = [...solution.matchAll(/<Project Path="([^"]+)"/g)]
    .map((match) => match[1].replaceAll("\\", "/"));
  const expectedLockFiles = projectPaths.map((projectPath) =>
    path.posix.join(path.posix.dirname(projectPath), "packages.lock.json"));
  const lockFiles = getTrackedFiles(":(glob)**/packages.lock.json");

  assert.equal(
    directoryBuildProps.includes("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>"),
    true);
  assert.equal(projectPaths.length, 12);
  assert.equal(lockFiles.length, 12);
  assert.deepEqual(lockFiles.sort(), expectedLockFiles.sort());
});

test("does not track a local NuGet feed configuration", () => {
  const trackedNuGetConfigs = getTrackedFiles("*NuGet.Config", "*nuget.config");

  for (const configPath of trackedNuGetConfigs) {
    assert.equal(readRepositoryFile(configPath).includes("artifacts/local-nuget"), false);
  }
});
