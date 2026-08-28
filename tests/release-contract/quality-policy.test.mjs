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
const testProjectPaths = [
  "tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj",
  "tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj",
  "tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj",
  "tests/Cmsify.Admin.Integration.Tests/Cmsify.Admin.Integration.Tests.csproj",
  "sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj",
];

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

test("standardizes every test project on the xUnit v3 host", () => {
  const centralPackages = readRepositoryFile("Directory.Packages.props");

  assert.equal(centralPackages.includes('<PackageVersion Include="xunit"'), false);
  assert.equal(
    centralPackages.includes('<PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />'),
    true);
  assert.equal(
    centralPackages.includes('<PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />'),
    true);
  assert.equal(
    centralPackages.includes('<PackageVersion Include="xunit.v3" Version="3.2.2" />'),
    true);

  for (const projectPath of testProjectPaths) {
    const project = readRepositoryFile(projectPath);

    assert.equal(project.includes("<OutputType>Exe</OutputType>"), true, projectPath);
    assert.equal(project.includes('<PackageReference Include="Microsoft.NET.Test.Sdk" />'), true, projectPath);
    assert.equal(project.includes('<PackageReference Include="xunit.runner.visualstudio"'), true, projectPath);
    assert.equal(project.includes('<PackageReference Include="xunit.v3" />'), true, projectPath);
    assert.equal(project.includes('<PackageReference Include="xunit"'), false, projectPath);
  }
});

test("treats Release warnings as errors without broadening suppression policy", () => {
  const directoryBuildProps = readRepositoryFile("Directory.Build.props");
  const buildPolicyPaths = getTrackedFiles(
    ":(glob)**/*.csproj",
    ":(glob)**/*.props",
    ":(glob)**/*.targets",
  );
  const noWarnEntries = buildPolicyPaths.flatMap((filePath) =>
    [...readRepositoryFile(filePath).matchAll(
      /<NoWarn\b([^>]*?)(?:\/\s*>|>([\s\S]*?)<\/NoWarn\s*>)/gi,
    )].map((match) => ({
      attributes: match[1].trim(),
      filePath,
      value: (match[2] ?? "").trim(),
    })));

  assert.equal(
    directoryBuildProps.includes(
      '<TreatWarningsAsErrors Condition="\'$(Configuration)\' == \'Release\'">true</TreatWarningsAsErrors>',
    ),
    true,
  );
  assert.deepEqual(noWarnEntries.map(({ filePath, value }) => `${filePath}:${value}`).sort(), [
    "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/SyntaxCircus.Cmsify.Client.DistributedCaching.csproj:$(NoWarn);1591",
    "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj:$(NoWarn);1591",
    "src/Cmsify.Api/Cmsify.Api.csproj:$(NoWarn);1591",
    "src/Cmsify.Contracts/Cmsify.Contracts.csproj:$(NoWarn);1591",
  ]);
  for (const { attributes, filePath } of noWarnEntries) {
    assert.equal(attributes, "", `${filePath}: NoWarn attributes are not allowed`);
  }

  for (const filePath of buildPolicyPaths) {
    const project = readRepositoryFile(filePath);

    assert.equal(
      /<WarningsNotAsErrors\b/i.test(project),
      false,
      `${filePath}: WarningsNotAsErrors is not allowed`,
    );
    assert.equal(
      /<Nullable\b[^>]*>\s*disable\s*<\/Nullable\s*>/i.test(project),
      false,
      `${filePath}: nullable disable policy is not allowed`,
    );
  }
});
