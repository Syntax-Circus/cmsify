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
const pinnedUploadArtifact = "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02";

function assertOrdered(source, markers, sourceName) {
  let previous = -1;
  for (const marker of markers) {
    const current = source.indexOf(marker);
    assert.notEqual(current, -1, `${sourceName}: missing ${marker}`);
    assert.equal(current > previous, true, `${sourceName}: ${marker} is out of order`);
    previous = current;
  }
}

function solutionProjectPaths() {
  return [...readRepositoryFile("Cmsify.slnx").matchAll(/<Project Path="([^"]+)"/g)]
    .map((match) => match[1].replaceAll("\\", "/"));
}

function projectRestoreClosure(entryProject) {
  const solutionProjects = new Set(solutionProjectPaths());
  const closure = new Set();
  const visit = (projectPath) => {
    assert.equal(solutionProjects.has(projectPath), true, `${projectPath}: not in Cmsify.slnx`);
    if (closure.has(projectPath)) return;
    closure.add(projectPath);
    const project = readRepositoryFile(projectPath);
    for (const match of project.matchAll(/<ProjectReference Include="([^"]+)"/g)) {
      const referencedProject = path.posix.normalize(path.posix.join(
        path.posix.dirname(projectPath),
        match[1].replaceAll("\\", "/"),
      ));
      visit(referencedProject);
    }
  };
  visit(entryProject);
  return [...closure].sort();
}

function dependabotUpdates(source) {
  const entries = [];
  let current = null;
  for (const line of source.split(/\r?\n/)) {
    const ecosystem = /^  - package-ecosystem:\s*["']?([^"'\s]+)["']?\s*$/.exec(line);
    if (ecosystem) {
      current = { ecosystem: ecosystem[1], source: `${line}\n` };
      entries.push(current);
    } else if (current) {
      current.source += `${line}\n`;
    }
  }
  return entries;
}

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

test("uses Sass modules and limits quieting to Bootstrap dependency diagnostics", () => {
  const sassFiles = getTrackedFiles("src/Cmsify.Admin/wwwroot/scss/**/*.scss");
  const sassCompiler = JSON.parse(readRepositoryFile("src/Cmsify.Admin/sasscompiler.json"));

  for (const sassFile of sassFiles) {
    const source = readRepositoryFile(sassFile);

    assert.equal(/@import\s+/i.test(source), false, `${sassFile}: @import is not allowed`);
    assert.equal(
      /(?<![-\w.])(?:red|green|blue|mix|adjust-color|scale-color|change-color|lighten|darken|saturate|desaturate|adjust-hue|transparentize|opacify)\s*\(/i.test(source),
      false,
      `${sassFile}: deprecated global color helpers are not allowed`,
    );
    assert.equal(/\bif\s*\(/i.test(source), false, `${sassFile}: legacy Sass if() is not allowed`);
  }

  assert.equal(
    sassCompiler.Arguments,
    "--style=compressed --load-path=wwwroot/lib/bootstrap/scss --quiet-deps",
  );
  assert.equal(/(?:^|\s)--quiet(?:\s|$)/.test(sassCompiler.Arguments), false);
});

test("runs the pull-request .NET quality gates in the required deterministic order", () => {
  const workflowPath = ".github/workflows/dotnet-test.yml";
  const workflow = readRepositoryFile(workflowPath);

  assert.match(workflow, /actions\/setup-dotnet@[^\s]+\s*\n\s+with:\s*\n\s+global-json-file:\s*global\.json/);
  assertOrdered(workflow, [
    "- uses: actions/checkout@",
    "- uses: actions/setup-dotnet@",
    "- name: Restore locked dependencies",
    "- name: Build Release binaries",
    "- name: Run full test suite",
    "- name: Collect raw coverage",
    "- name: Summarize coverage",
    "- name: Upload raw coverage reports",
    "- name: Upload coverage summary",
    "- name: Run API capacity invariants",
    "- name: Run Infrastructure capacity invariants",
    "- name: Run .NET client capacity invariants",
  ], workflowPath);

  assert.match(workflow, /run:\s*dotnet restore Cmsify\.slnx --locked-mode\s*$/m);
  assert.match(workflow, /run:\s*dotnet build Cmsify\.slnx --configuration Release --no-restore --no-incremental\s*$/m);
  assert.match(workflow, /run:\s*dotnet test Cmsify\.slnx --configuration Release --no-build --verbosity minimal(?:\s+-p:DisableGitVersionTask=true)?\s*$/m);
  assert.match(workflow, /run:\s*dotnet test Cmsify\.slnx --configuration Release --no-build --collect:["']XPlat Code Coverage["'] --results-directory artifacts\/coverage --verbosity minimal\s*$/m);
  assert.match(workflow, /run:\s*node scripts\/quality\/summarize-coverage\.mjs --input artifacts\/coverage --json artifacts\/coverage\/summary\.json --markdown artifacts\/coverage\/summary\.md\s*$/m);

  const uploadSteps = workflow.match(new RegExp(pinnedUploadArtifact, "g")) ?? [];
  assert.equal(uploadSteps.length, 2);
  assert.match(workflow, /name:\s*dotnet-coverage-raw-[^\n]+[\s\S]*?path:\s*artifacts\/coverage\/\*\*\/coverage\.cobertura\.xml/);
  assert.match(workflow, /name:\s*dotnet-coverage-summary-[^\n]+[\s\S]*?path:\s*\|\s*\n\s+artifacts\/coverage\/summary\.json\s*\n\s+artifacts\/coverage\/summary\.md/);

  const expectedCapacityCommands = [
    "dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
  ];
  for (const command of expectedCapacityCommands) {
    assert.equal(workflow.split(/\r?\n/).some((line) => line.trim() === `run: ${command}`), true, command);
  }
  assert.doesNotMatch(workflow, /CMSIFY_CAPACITY_(?:TIMING|REPORT_DIR)/);
  assert.doesNotMatch(workflow, /scripts\/quality\/run-capacity\.mjs/);
});

test("uses global.json and locked solution restore in every solution-restoring workflow", () => {
  const workflows = getTrackedFiles(".github/workflows/*.yml", ".github/workflows/*.yaml");
  const solutionRestoreWorkflows = workflows.filter((workflowPath) =>
    readRepositoryFile(workflowPath).includes("dotnet restore Cmsify.slnx"));

  assert.deepEqual(solutionRestoreWorkflows.sort(), [
    ".github/workflows/admin-accessibility.yml",
    ".github/workflows/capacity-trends.yml",
    ".github/workflows/dotnet-test.yml",
    ".github/workflows/publish-cmsify.yml",
  ]);
  const expectedSetupCounts = new Map([
    [".github/workflows/admin-accessibility.yml", 1],
    [".github/workflows/capacity-trends.yml", 1],
    [".github/workflows/dotnet-test.yml", 1],
    [".github/workflows/publish-cmsify.yml", 3],
  ]);
  for (const workflowPath of solutionRestoreWorkflows) {
    const workflow = readRepositoryFile(workflowPath);
    const restoreCommands = workflow.match(/^\s*(?:run:\s*)?dotnet restore Cmsify\.slnx.*$/gm) ?? [];
    const setupDotnetSteps = workflow.match(/actions\/setup-dotnet@/g) ?? [];
    const globalJsonInputs = workflow.match(/\bglobal-json-file:\s*global\.json\b/g) ?? [];

    assert.deepEqual(restoreCommands.map((command) => command.trim().replace(/^run:\s*/, "")), [
      "dotnet restore Cmsify.slnx --locked-mode",
    ], workflowPath);
    assert.equal(setupDotnetSteps.length, expectedSetupCounts.get(workflowPath), workflowPath);
    assert.equal(globalJsonInputs.length, setupDotnetSteps.length, workflowPath);
    assert.doesNotMatch(workflow, /dotnet-version:\s*["']?10\.0\.x/, workflowPath);
  }
  assert.match(
    readRepositoryFile(".github/workflows/admin-accessibility.yml"),
    /dotnet run --no-restore --no-launch-profile --project src\/Cmsify\.Admin\/Cmsify\.Admin\.csproj/,
  );
});

test("copies each Docker restore closure and its lock files before locked restore", () => {
  const containers = [
    { dockerfile: "src/Cmsify.Api/Dockerfile", project: "src/Cmsify.Api/Cmsify.Api.csproj" },
    { dockerfile: "src/Cmsify.Admin/Dockerfile", project: "src/Cmsify.Admin/Cmsify.Admin.csproj" },
  ];

  for (const { dockerfile, project } of containers) {
    const source = readRepositoryFile(dockerfile);
    assert.match(source, /^FROM mcr\.microsoft\.com\/dotnet\/sdk:10\.0\.400 AS build\s*$/m, dockerfile);
    const restoreMarker = `RUN dotnet restore "${project}" --locked-mode`;
    const restoreIndex = source.indexOf(restoreMarker);
    assert.notEqual(restoreIndex, -1, `${dockerfile}: locked restore command`);
    const preRestore = source.slice(0, restoreIndex);
    const copiedSources = [...preRestore.matchAll(/^COPY \["([^"]+)",\s*"[^"]+"\]\s*$/gm)]
      .map((match) => match[1]);
    const closure = projectRestoreClosure(project);
    const expectedBuildInputs = [
      "Directory.Build.props",
      "Directory.Build.targets",
      "Directory.Packages.props",
      ...closure.flatMap((projectPath) => [
        projectPath,
        path.posix.join(path.posix.dirname(projectPath), "packages.lock.json"),
      ]),
    ].sort();

    assert.deepEqual(copiedSources.sort(), expectedBuildInputs, dockerfile);
    assert.equal(source.indexOf("COPY . .") > restoreIndex, true, `${dockerfile}: source copied after restore`);
  }
});

test("configures four weekly Dependabot ecosystems with only minor and patch grouping", () => {
  const dependabot = readRepositoryFile(".github/dependabot.yml");
  const updates = dependabotUpdates(dependabot);
  const identities = updates.map(({ ecosystem, source }) => {
    const directory = /^    directory:\s*["']([^"']+)["']\s*$/m.exec(source)?.[1];
    return `${ecosystem}:${directory}`;
  });

  assert.match(dependabot, /^version:\s*2\s*$/m);
  assert.deepEqual(identities.sort(), [
    "docker:/",
    "github-actions:/",
    "npm:/sdk/typescript",
    "nuget:/",
  ]);
  assert.equal(updates.length, 4);
  for (const { ecosystem, source } of updates) {
    assert.match(source, /^    schedule:\s*\n      interval:\s*["']weekly["']\s*$/m, ecosystem);
    assert.match(
      source,
      /^    groups:\s*\n      [a-z0-9-]+:\s*\n        patterns:\s*\["\*"\]\s*\n        update-types:\s*\["minor", "patch"\]\s*$/m,
      ecosystem,
    );
    assert.doesNotMatch(source, /update-types:[^\n]*major|ignore:\s*[\s\S]*update-types:[^\n]*version-update:semver-major/i);
  }
});

test("does not automate Dependabot pull-request merging", () => {
  const workflows = getTrackedFiles(".github/workflows/*.yml", ".github/workflows/*.yaml");
  for (const workflowPath of workflows) {
    const workflow = readRepositoryFile(workflowPath);
    assert.doesNotMatch(
      workflow,
      /(?:dependabot[\s\S]{0,400}auto(?:-|\s*)merge|auto(?:-|\s*)merge[\s\S]{0,400}dependabot|gh\s+pr\s+merge\b|enablePullRequestAutoMerge|mergePullRequest)/i,
      workflowPath,
    );
  }
});
