import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { parseYamlSubset } from "./yaml-subset.mjs";

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
const pinnedCheckout = "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683";
const pinnedSetupDotnet = "actions/setup-dotnet@d4c94342e560b34958eacfc5d055d21461ed1c5d";
const pullRequestCondition = "github.event_name == 'pull_request'";
const nonPullRequestCondition = "github.event_name != 'pull_request'";
const publicIndependentTestProjects = [
  "tests/Cmsify.Core.Tests/Cmsify.Core.Tests.csproj",
  "tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj",
  "tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj",
];

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

function workflowDocuments() {
  return Object.fromEntries(getTrackedFiles(".github/workflows/*.yml", ".github/workflows/*.yaml")
    .map((workflowPath) => [
      workflowPath,
      parseYamlSubset(readRepositoryFile(workflowPath), workflowPath),
    ]));
}

function stepLabel(step) {
  return step.name ?? step.uses ?? step.run;
}

function validatePullRequestWorkflow(workflow) {
  const steps = workflow.jobs?.test?.steps;
  assert.equal(Array.isArray(steps), true, "dotnet-test.yml: jobs.test.steps must be a sequence");
  assert.deepEqual(steps.map(stepLabel), [
    pinnedCheckout,
    pinnedSetupDotnet,
    "Restore public-independent PR dependencies",
    "Build public-independent PR binaries",
    "Run public-independent PR tests",
    "Report deferred package-dependent PR coverage",
    "Restore locked dependencies",
    "Build Release binaries",
    "Run full test suite",
    "Collect raw coverage",
    "Summarize coverage",
    "Publish coverage summary",
    "Upload raw coverage reports",
    "Upload coverage summary",
    "Run API capacity invariants",
    "Run Infrastructure capacity invariants",
    "Run .NET client capacity invariants",
  ]);
  assert.deepEqual(steps[1], {
    uses: pinnedSetupDotnet,
    with: { "global-json-file": "global.json" },
  });
  const expectedPrRestore = publicIndependentTestProjects
    .map((project) => `dotnet restore ${project} --locked-mode`)
    .join("\n");
  const expectedPrBuild = publicIndependentTestProjects
    .map((project) => `dotnet build ${project} --configuration Release --no-restore --no-incremental`)
    .join("\n");
  const expectedPrTest = publicIndependentTestProjects
    .map((project) => `dotnet test ${project} --configuration Release --no-build --verbosity minimal -p:DisableGitVersionTask=true`)
    .join("\n");
  assert.equal(steps[2].if, pullRequestCondition);
  assert.equal(steps[2].run.trim(), expectedPrRestore);
  assert.equal(steps[3].if, pullRequestCondition);
  assert.equal(steps[3].run.trim(), expectedPrBuild);
  assert.equal(steps[4].if, pullRequestCondition);
  assert.equal(steps[4].run.trim(), expectedPrTest);
  assert.equal(steps[5].if, pullRequestCondition);
  assert.match(steps[5].run, /Admin and \.NET client checks are deferred/i);
  for (const step of steps.slice(6)) assert.equal(step.if, nonPullRequestCondition, stepLabel(step));
  assert.equal(steps[6].run, "dotnet restore Cmsify.slnx --locked-mode");
  assert.equal(steps[7].run, "dotnet build Cmsify.slnx --configuration Release --no-restore --no-incremental");
  assert.equal(steps[8].run, "dotnet test Cmsify.slnx --configuration Release --no-build --verbosity minimal -p:DisableGitVersionTask=true");
  assert.equal(steps[9].run, 'dotnet test Cmsify.slnx --configuration Release --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/coverage --verbosity minimal');
  assert.equal(steps[10].run, "node scripts/quality/summarize-coverage.mjs --input artifacts/coverage --json artifacts/coverage/summary.json --markdown artifacts/coverage/summary.md");
  assert.equal(steps[11].run, 'cat artifacts/coverage/summary.md >> "$GITHUB_STEP_SUMMARY"');
  assert.deepEqual(steps[12], {
    name: "Upload raw coverage reports",
    if: nonPullRequestCondition,
    uses: pinnedUploadArtifact,
    with: {
      name: "dotnet-coverage-raw-${{ github.run_id }}-${{ github.run_attempt }}",
      path: "artifacts/coverage/**/coverage.cobertura.xml",
      "if-no-files-found": "error",
      "retention-days": 14,
    },
  });
  assert.deepEqual(steps[13], {
    name: "Upload coverage summary",
    if: nonPullRequestCondition,
    uses: pinnedUploadArtifact,
    with: {
      name: "dotnet-coverage-summary-${{ github.run_id }}-${{ github.run_attempt }}",
      path: "artifacts/coverage/summary.json\nartifacts/coverage/summary.md\n",
      "if-no-files-found": "error",
      "retention-days": 14,
    },
  });
  const capacityCommands = [
    "dotnet test tests/Cmsify.Api.Integration.Tests/Cmsify.Api.Integration.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
    "dotnet test sdk/dotnet/tests/SyntaxCircus.Cmsify.Client.Tests/SyntaxCircus.Cmsify.Client.Tests.csproj --configuration Release --no-build --filter Category=Capacity",
  ];
  assert.deepEqual(steps.slice(14).map((step) => step.run), capacityCommands);
  assert.equal(workflow.jobs.test.env, undefined);
  for (const step of steps.slice(14)) {
    assert.equal(step.env, undefined, `${step.name}: timing/report environment is forbidden`);
    assert.doesNotMatch(step.run, /CMSIFY_CAPACITY_|run-capacity\.mjs/);
  }
}

function validateSetupDotnetStep(step, context) {
  assert.equal(step.uses?.startsWith("actions/setup-dotnet@"), true, `${context}: setup-dotnet action`);
  assert.equal(typeof step.with, "object", `${context}: setup-dotnet with mapping`);
  assert.equal(Object.hasOwn(step.with, "dotnet-version"), false, `${context}: dotnet-version is forbidden`);
  assert.equal(typeof step.with["global-json-file"], "string", `${context}: setup-dotnet owns global-json-file`);
}

function validateDotnetSetupAndRestorePolicy(documents) {
  const setups = [];
  const restores = [];
  for (const [workflowPath, workflow] of Object.entries(documents)) {
    assert.equal(typeof workflow.jobs, "object", `${workflowPath}: jobs mapping`);
    for (const [jobName, job] of Object.entries(workflow.jobs)) {
      const steps = job.steps ?? [];
      assert.equal(Array.isArray(steps), true, `${workflowPath}:${jobName}: steps sequence`);
      const dotnetRunIndexes = [];
      const setupIndexes = [];
      for (const [stepIndex, step] of steps.entries()) {
        if (step.uses?.startsWith("actions/setup-dotnet@")) {
          setupIndexes.push(stepIndex);
          validateSetupDotnetStep(step, `${workflowPath}:${jobName}`);
          setups.push(`${workflowPath}:${jobName}:${step.with["global-json-file"]}`);
        }
        if (typeof step.run === "string") {
          const commands = step.run.split("\n").map((line) => line.trim()).filter(Boolean);
          if (commands.some((command) => /(?:^|\s)dotnet(?:\s|$)/.test(command))) dotnetRunIndexes.push(stepIndex);
          for (const command of commands.filter((command) => command.startsWith("dotnet restore Cmsify.slnx"))) {
            restores.push(`${workflowPath}:${jobName}:${command}`);
          }
        }
      }
      if (dotnetRunIndexes.length > 0) {
        assert.equal(setupIndexes.length, 1, `${workflowPath}:${jobName}: exactly one setup-dotnet step`);
        assert.equal(setupIndexes[0] < Math.min(...dotnetRunIndexes), true, `${workflowPath}:${jobName}: setup-dotnet precedes dotnet commands`);
      }
    }
  }
  assert.deepEqual(setups.sort(), [
    ".github/workflows/admin-accessibility.yml:axe:global.json",
    ".github/workflows/capacity-trends.yml:capacity-trends:global.json",
    ".github/workflows/dotnet-test.yml:test:global.json",
    ".github/workflows/openapi-contract.yml:contract:global.json",
    ".github/workflows/publish-cmsify.yml:build:global.json",
    ".github/workflows/publish-cmsify.yml:dotnet-consumer:source/global.json",
    ".github/workflows/publish-cmsify.yml:promote:global.json",
    ".github/workflows/typescript-sdk.yml:sdk:global.json",
  ]);
  assert.deepEqual(restores.sort(), [
    ".github/workflows/admin-accessibility.yml:axe:dotnet restore Cmsify.slnx --locked-mode",
    ".github/workflows/capacity-trends.yml:capacity-trends:dotnet restore Cmsify.slnx --locked-mode",
    ".github/workflows/dotnet-test.yml:test:dotnet restore Cmsify.slnx --locked-mode",
    ".github/workflows/publish-cmsify.yml:build:dotnet restore Cmsify.slnx --locked-mode",
  ]);
}

function javaScriptTokens(source, sourceName) {
  const tokens = [];
  for (let index = 0; index < source.length;) {
    if (/\s/.test(source[index])) {
      index += 1;
      continue;
    }
    if (source.startsWith("//", index)) {
      index = source.indexOf("\n", index + 2);
      if (index === -1) break;
      continue;
    }
    if (source.startsWith("/*", index)) {
      const end = source.indexOf("*/", index + 2);
      assert.notEqual(end, -1, `${sourceName}: unterminated block comment`);
      index = end + 2;
      continue;
    }
    if (["'", '"'].includes(source[index])) {
      const quote = source[index];
      let value = "";
      let closed = false;
      index += 1;
      while (index < source.length) {
        if (source[index] === "\\") {
          assert.equal(index + 1 < source.length, true, `${sourceName}: unterminated string escape`);
          value += source[index + 1];
          index += 2;
        } else if (source[index] === quote) {
          index += 1;
          closed = true;
          break;
        } else {
          value += source[index];
          index += 1;
        }
      }
      assert.equal(closed, true, `${sourceName}: unterminated string literal`);
      tokens.push({ type: "string", value });
      continue;
    }
    if (source[index] === "`") {
      let escaped = false;
      let closed = false;
      index += 1;
      while (index < source.length) {
        if (escaped) escaped = false;
        else if (source[index] === "\\") escaped = true;
        else if (source[index] === "`") {
          index += 1;
          closed = true;
          break;
        }
        index += 1;
      }
      assert.equal(closed, true, `${sourceName}: unterminated template literal`);
      tokens.push({ type: "template" });
      continue;
    }
    const identifier = /^[A-Za-z_$][A-Za-z0-9_$]*/.exec(source.slice(index));
    if (identifier !== null) {
      tokens.push({ type: "identifier", value: identifier[0] });
      index += identifier[0].length;
      continue;
    }
    tokens.push({ type: "punctuation", value: source[index] });
    index += 1;
  }
  return tokens;
}

function dotnetRunArgumentLists(source, sourceName) {
  const tokens = javaScriptTokens(source, sourceName);
  const calls = [];
  for (let index = 0; index < tokens.length - 5; index += 1) {
    if (tokens[index].type !== "identifier" || tokens[index].value !== "run"
      || tokens[index - 1]?.value === "." || tokens[index + 1]?.value !== "("
      || tokens[index + 2]?.type !== "string" || tokens[index + 2].value !== "dotnet"
      || tokens[index + 3]?.value !== "," || tokens[index + 4]?.value !== "[") continue;

    const argumentsList = [];
    let argumentIndex = index + 5;
    let expectArgument = true;
    while (argumentIndex < tokens.length && tokens[argumentIndex].value !== "]") {
      const token = tokens[argumentIndex];
      if (expectArgument) {
        assert.equal(
          ["string", "identifier"].includes(token.type),
          true,
          `${sourceName}: unsupported dotnet argument token`,
        );
        argumentsList.push(token.type === "string" ? token.value : { identifier: token.value });
      } else {
        assert.equal(token.value, ",", `${sourceName}: dotnet arguments must be comma-separated`);
      }
      expectArgument = !expectArgument;
      argumentIndex += 1;
    }
    assert.equal(argumentIndex < tokens.length, true, `${sourceName}: unterminated dotnet argument list`);
    assert.equal(expectArgument, false, `${sourceName}: trailing comma in dotnet argument list`);
    calls.push(argumentsList);
  }
  return calls;
}

function validateOpenApiWrapperBuild(openApiSource) {
  const buildCalls = dotnetRunArgumentLists(openApiSource, "scripts/openapi.mjs")
    .filter((argumentsList) => argumentsList[0] === "build"
      && argumentsList.some((argument) => argument?.identifier === "apiProject"));
  assert.equal(buildCalls.length, 1, "scripts/openapi.mjs: exactly one API repository build");
  assert.deepEqual(buildCalls[0], [
    "build",
    { identifier: "apiProject" },
    "--configuration",
    "Release",
    "--no-restore",
    "--nologo",
  ], "scripts/openapi.mjs: API build must consume a prior restore");
}

function shellCommandSegments(command) {
  const segments = [];
  let words = [];
  let word = "";
  let quote = null;
  let escaped = false;
  const pushWord = () => {
    if (word.length > 0) words.push(word);
    word = "";
  };
  const pushSegment = () => {
    pushWord();
    if (words.length > 0) segments.push(words);
    words = [];
  };

  for (let index = 0; index < command.length; index += 1) {
    const character = command[index];
    if (escaped) {
      word += character;
      escaped = false;
    } else if (quote !== null) {
      if (character === quote) quote = null;
      else if (character === "\\" && quote === '"') escaped = true;
      else word += character;
    } else if (["'", '"'].includes(character)) {
      quote = character;
    } else if (character === "#" && word.length === 0) {
      break;
    } else if (/\s/.test(character)) {
      pushWord();
    } else if ([";", "|", "&"].includes(character)) {
      pushSegment();
      if (command[index + 1] === character) index += 1;
    } else if (character === "\\") {
      escaped = true;
    } else {
      word += character;
    }
  }
  pushSegment();
  return segments;
}

function commandStart(words) {
  let index = 0;
  if (words[index] === "env") index += 1;
  while (/^[A-Za-z_][A-Za-z0-9_]*=/.test(words[index] ?? "")) index += 1;
  return index;
}

function normalizedWorkflowDirectory(directory) {
  const normalized = path.posix.normalize((directory || ".").replaceAll("\\", "/"));
  return normalized === "./" ? "." : normalized;
}

function invokesOpenApiBuildWrapper(words, workingDirectory) {
  const commandIndex = commandStart(words);
  const executable = words[commandIndex];
  if (executable === "node") {
    const script = words[commandIndex + 1];
    const operation = words[commandIndex + 2];
    return typeof script === "string"
      && normalizedWorkflowDirectory(path.posix.join(workingDirectory, script)) === "scripts/openapi.mjs"
      && ["check", "update", "export"].includes(operation);
  }
  if (executable !== "npm") return false;

  let prefix = workingDirectory;
  let index = commandIndex + 1;
  while (index < words.length && !["run", "run-script"].includes(words[index])) {
    if (["--prefix", "-C"].includes(words[index]) && typeof words[index + 1] === "string") {
      prefix = path.posix.join(workingDirectory, words[index + 1]);
      index += 2;
    } else if (words[index].startsWith("--prefix=")) {
      prefix = path.posix.join(workingDirectory, words[index].slice("--prefix=".length));
      index += 1;
    } else {
      index += 1;
    }
  }
  const script = words[index + 1];
  return normalizedWorkflowDirectory(prefix) === "sdk/typescript"
    && ["generate", "generate:check"].includes(script);
}

function isRootLockedOpenApiRestore(words, workingDirectory) {
  const commandIndex = commandStart(words);
  return normalizedWorkflowDirectory(workingDirectory) === "."
    && words.length === commandIndex + 4
    && words[commandIndex] === "dotnet"
    && words[commandIndex + 1] === "restore"
    && ["Cmsify.slnx", "src/Cmsify.Api/Cmsify.Api.csproj"].includes(words[commandIndex + 2])
    && words[commandIndex + 3] === "--locked-mode";
}

function validateOpenApiWrapperRestorePolicy(documents) {
  for (const [workflowPath, workflow] of Object.entries(documents)) {
    for (const [jobName, job] of Object.entries(workflow.jobs ?? {})) {
      let lockedOpenApiRestoreSeen = false;
      for (const step of job.steps ?? []) {
        if (typeof step.run !== "string") continue;
        const workingDirectory = normalizedWorkflowDirectory(
          step["working-directory"]
            ?? job.defaults?.run?.["working-directory"]
            ?? workflow.defaults?.run?.["working-directory"]
            ?? ".",
        );
        const commandSegments = step.run.split("\n").flatMap(shellCommandSegments);
        for (const words of commandSegments) {
          if (isRootLockedOpenApiRestore(words, workingDirectory)) lockedOpenApiRestoreSeen = true;
          if (!invokesOpenApiBuildWrapper(words, workingDirectory)) continue;
          assert.equal(
            lockedOpenApiRestoreSeen,
            true,
            `${workflowPath}:${jobName}: OpenAPI wrapper requires a prior same-job root locked API restore`,
          );
        }
      }
    }
  }
}

const expectedReleaseConsumerRun = `(cd artifacts && sha256sum --check SHA256SUMS)
CONSUMER_ROOT="$RUNNER_TEMP/cmsify-dotnet-consumer"
LOCAL_SOURCE="$CONSUMER_ROOT/candidate-source"
mkdir -p "$LOCAL_SOURCE"
for package in SyntaxCircus.Cmsify.Contracts SyntaxCircus.Cmsify.Client SyntaxCircus.Cmsify.Client.DistributedCaching; do
  candidate="$GITHUB_WORKSPACE/artifacts/nuget/$package.$VERSION.nupkg"
  test -f "$candidate"
  cp "$candidate" "$LOCAL_SOURCE/"
done
test "$(find "$LOCAL_SOURCE" -maxdepth 1 -type f -name '*.nupkg' | wc -l)" -eq 3
cd "$CONSUMER_ROOT"
dotnet new console --framework net10.0 --no-restore
cat > NuGet.Config <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$LOCAL_SOURCE" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate">
      <package pattern="SyntaxCircus.Cmsify.Contracts" />
      <package pattern="SyntaxCircus.Cmsify.Client" />
      <package pattern="SyntaxCircus.Cmsify.Client.DistributedCaching" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.*" />
      <package pattern="Polly.*" />
      <package pattern="System.*" />
      <package pattern="SyntaxCircus.Http.Resilience" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF
for package in SyntaxCircus.Cmsify.Contracts SyntaxCircus.Cmsify.Client SyntaxCircus.Cmsify.Client.DistributedCaching; do
  dotnet add package "$package" --version "$VERSION" --no-restore
done
dotnet restore --configfile NuGet.Config --packages "$CONSUMER_ROOT/package-cache" --no-http-cache
dotnet build --configuration Release --no-restore
`;

function validateReleaseConsumer(workflow) {
  const steps = workflow.jobs?.["dotnet-consumer"]?.steps;
  assert.equal(Array.isArray(steps), true, "publish-cmsify.yml: dotnet-consumer steps");
  assert.equal(steps.length, 4);
  assert.deepEqual(steps[0], {
    uses: "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683",
    with: {
      ref: "${{ needs.resolve.outputs.source_sha }}",
      "fetch-depth": 1,
      "persist-credentials": false,
      path: "source",
    },
  });
  assert.deepEqual(steps[1], {
    uses: pinnedSetupDotnet,
    with: { "global-json-file": "source/global.json" },
  });
  assert.deepEqual(steps[2], {
    uses: "actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093",
    with: {
      name: "release-candidate-${{ needs.resolve.outputs.version }}-${{ needs.resolve.outputs.source_sha }}",
      path: "artifacts",
    },
  });
  assert.deepEqual(steps[3].env, { VERSION: "${{ needs.resolve.outputs.version }}" });
  assert.equal(steps[3].shell, "bash");
  assert.equal(steps[3].run, expectedReleaseConsumerRun);
  assert.doesNotMatch(steps[3].run, /(?:mkdir|cd)\s+(?:\.\/)?consumer\b|GITHUB_WORKSPACE\/source\/.*consumer/i);
}

function validateDependabotPolicy(dependabot) {
  assert.equal(dependabot.version, 2);
  assert.equal(Array.isArray(dependabot.updates), true);
  assert.equal(dependabot.updates.length, 4);
  const identities = [];
  for (const update of dependabot.updates) {
    const ecosystem = update["package-ecosystem"];
    if (Object.hasOwn(update, "open-pull-requests-limit")) {
      assert.fail(`${ecosystem}: open-pull-requests-limit is not an allowed key; updates cannot be disabled`);
    }
    const allowedKeys = ecosystem === "docker"
      ? ["directories", "groups", "package-ecosystem", "schedule"]
      : ["directory", "groups", "package-ecosystem", "schedule"];
    assert.deepEqual(Object.keys(update).sort(), allowedKeys, `${ecosystem}: exact allowed keys; ignore and allow are forbidden`);
    assert.deepEqual(update.schedule, { interval: "weekly" }, ecosystem);
    const groupNames = Object.keys(update.groups ?? {});
    assert.equal(groupNames.length, 1, `${ecosystem}: exactly one group`);
    assert.deepEqual(update.groups[groupNames[0]], {
      patterns: ["*"],
      "update-types": ["minor", "patch"],
    }, ecosystem);
    if (ecosystem === "docker") {
      assert.equal(update.directory, undefined);
      assert.deepEqual(update.directories, ["/src/Cmsify.Api", "/src/Cmsify.Admin"]);
      identities.push("docker:/src/Cmsify.Api,/src/Cmsify.Admin");
    } else {
      assert.equal(update.directories, undefined, `${ecosystem}: directories is Docker-only`);
      identities.push(`${ecosystem}:${update.directory}`);
    }
  }
  assert.deepEqual(identities.sort(), [
    "docker:/src/Cmsify.Api,/src/Cmsify.Admin",
    "github-actions:/",
    "npm:/sdk/typescript",
    "nuget:/",
  ]);
}

function parseDockerfile(source, sourceName) {
  const instructions = [];
  let logical = "";
  let startLine = null;
  const lines = source.replaceAll("\r\n", "\n").split("\n");
  for (const [lineIndex, rawLine] of lines.entries()) {
    const trimmed = rawLine.trim();
    if (logical === "" && (trimmed === "" || trimmed.startsWith("#"))) continue;
    if (startLine === null) startLine = lineIndex + 1;
    const continued = /\\\s*$/.test(trimmed);
    logical += `${logical === "" ? "" : " "}${trimmed.replace(/\\\s*$/, "").trim()}`;
    if (continued) continue;
    const match = /^([A-Za-z]+)\s+(.+)$/.exec(logical);
    assert.notEqual(match, null, `${sourceName}:${startLine}: malformed Docker instruction`);
    instructions.push({ instruction: match[1].toUpperCase(), arguments: match[2], line: startLine });
    logical = "";
    startLine = null;
  }
  assert.equal(logical, "", `${sourceName}: unterminated continuation`);
  return instructions;
}

function dockerCopy(instruction, sourceName) {
  assert.equal(instruction.instruction, "COPY");
  assert.equal(instruction.arguments.startsWith("["), true, `${sourceName}:${instruction.line}: restore COPY must use JSON form`);
  let values;
  try {
    values = JSON.parse(instruction.arguments);
  } catch {
    assert.fail(`${sourceName}:${instruction.line}: invalid JSON COPY`);
  }
  assert.equal(Array.isArray(values), true);
  assert.equal(values.length, 2, `${sourceName}:${instruction.line}: COPY requires one source and destination`);
  assert.equal(values.every((value) => typeof value === "string"), true);
  return { source: values[0], destination: values[1] };
}

function validateDockerfilePolicy(source, dockerfile, project) {
  const instructions = parseDockerfile(source, dockerfile);
  const buildFrom = instructions.findIndex(({ instruction, arguments: value }) =>
    instruction === "FROM" && /^mcr\.microsoft\.com\/dotnet\/sdk:10\.0\.400@sha256:[0-9a-f]{64} AS build$/i.test(value));
  assert.notEqual(buildFrom, -1, `${dockerfile}: exact SDK build stage`);
  const nextFrom = instructions.findIndex(({ instruction }, index) => index > buildFrom && instruction === "FROM");
  const buildStageEnd = nextFrom === -1 ? instructions.length : nextFrom;
  const buildStage = instructions.slice(buildFrom, buildStageEnd);
  const restoreCommand = `dotnet restore "${project}" --locked-mode`;
  const restoreIndex = buildStage.findIndex(({ instruction, arguments: value }, index) =>
    index > 0 && instruction === "RUN" && value === restoreCommand);
  assert.notEqual(restoreIndex, -1, `${dockerfile}: exact locked restore`);
  const sourceCopyIndex = buildStage.findIndex(({ instruction, arguments: value }, index) =>
    index > restoreIndex && instruction === "COPY" && value === ". .");
  assert.notEqual(sourceCopyIndex, -1, `${dockerfile}: source copy follows restore`);

  const actualCopies = buildStage.slice(1, restoreIndex)
    .filter(({ instruction }) => instruction === "COPY")
    .map((instruction) => dockerCopy(instruction, dockerfile))
    .sort((left, right) => left.source.localeCompare(right.source));
  const closure = projectRestoreClosure(project);
  const expectedCopies = [
    { source: "Directory.Build.props", destination: "./" },
    { source: "Directory.Build.targets", destination: "./" },
    { source: "Directory.Packages.props", destination: "./" },
    ...closure.flatMap((projectPath) => {
      const destination = `${path.posix.dirname(projectPath)}/`;
      return [
        { source: projectPath, destination },
        { source: path.posix.join(path.posix.dirname(projectPath), "packages.lock.json"), destination },
      ];
    }),
  ].sort((left, right) => left.source.localeCompare(right.source));
  assert.deepEqual(actualCopies, expectedCopies, dockerfile);
}

function nestedScalars(value, path = []) {
  if (typeof value === "string") return [{ path, value }];
  if (Array.isArray(value)) return value.flatMap((item, index) => nestedScalars(item, [...path, index]));
  if (value !== null && typeof value === "object") {
    return Object.entries(value).flatMap(([key, item]) => nestedScalars(item, [...path, key]));
  }
  return [];
}

function mergeCapability(value) {
  if (/(?:^|\/)(?:auto-?merge(?:-action)?|merge-pull-request|enable-pull-request-auto-?merge)(?:@|\/|$)/i.test(value)) {
    return "merge-capable action";
  }
  if (/\b(?:github|octokit)(?:\.rest)?\.pulls\.merge\s*\(/i.test(value)) return "pulls.merge API";
  if (/\b(?:enablePullRequestAutoMerge|mergePullRequest)\b/.test(value)) return "GraphQL merge API";
  if (/(?:^|[\n;&|]\s*)gh\s+pr\s+merge\b/im.test(value)) return "gh pr merge command";
  if (/\bgh\s+api\b[^\n]*\bpulls\/[^\s/]+\/merge\b/i.test(value)) return "GitHub merge REST command";
  if (/\bcurl\b[^\n]*\b(?:POST|PUT)\b[^\n]*\/pulls\/[^\s/]+\/merge\b/i.test(value)) return "GitHub merge REST command";
  return null;
}

function pullRequestTrigger(workflow) {
  if (typeof workflow.on === "string") return ["pull_request", "pull_request_target"].includes(workflow.on);
  if (Array.isArray(workflow.on)) return workflow.on.some((trigger) => ["pull_request", "pull_request_target"].includes(trigger));
  return workflow.on !== null && typeof workflow.on === "object"
    && (Object.hasOwn(workflow.on, "pull_request") || Object.hasOwn(workflow.on, "pull_request_target"));
}

function grantsWrite(permissions) {
  if (permissions === "write-all") return true;
  return permissions !== null && typeof permissions === "object"
    && Object.values(permissions).some((permission) => permission === "write");
}

function validateNoDependabotAutoMerge(documents) {
  for (const [workflowPath, workflow] of Object.entries(documents)) {
    for (const [jobName, job] of Object.entries(workflow.jobs ?? {})) {
      const jobScalars = nestedScalars(job, ["jobs", jobName]);
      const mergeEntry = jobScalars.find(({ value }) => mergeCapability(value) !== null);
      const dependabotTriggered = pullRequestTrigger(workflow)
        && jobScalars.some(({ value }) => /\bdependabot(?:\[bot\])?\b/i.test(value));
      const writeCapable = grantsWrite(job.permissions ?? workflow.permissions);
      assert.equal(
        dependabotTriggered && writeCapable && mergeEntry !== undefined,
        false,
        `${workflowPath}:${jobName}: Dependabot-triggered write-capable merge job`,
      );
    }
    for (const { path: scalarPath, value } of nestedScalars(workflow)) {
      const capability = mergeCapability(value);
      assert.equal(
        capability,
        null,
        `${workflowPath}:${scalarPath.join(".")}: merge-capable scalar (${capability})`,
      );
    }
  }
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

test("strict YAML subset parser returns nested workflow objects and rejects unsupported syntax", () => {
  const fixture = parseYamlSubset(`jobs:
  build:
    steps:
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - name: Run
        env:
          FLAG: "true"
        run: |
          dotnet --version
`, "fixture.yml");
  assert.deepEqual(fixture, {
    jobs: {
      build: {
        steps: [
          { uses: "actions/setup-dotnet@v4", with: { "global-json-file": "global.json" } },
          { name: "Run", env: { FLAG: "true" }, run: "dotnet --version\n" },
        ],
      },
    },
  });
  assert.throws(
    () => parseYamlSubset("jobs:\n  build:\n    steps:\n      - uses: one\n        uses: two\n", "duplicate.yml"),
    /duplicate mapping key uses/,
  );
  assert.throws(() => parseYamlSubset("jobs:\n\tbuild: {}\n", "tabs.yml"), /tabs are not supported/);
  assert.throws(() => parseYamlSubset("jobs: &jobs\n", "anchor.yml"), /unsupported YAML construct/);
  assert.throws(() => parseYamlSubset("jobs: [build\n", "flow.yml"), /unterminated flow sequence/);
  assert.deepEqual(parseYamlSubset("x: 'a''b'\n", "single-quote.yml"), { x: "a'b" });
  assert.deepEqual(parseYamlSubset("x: a#b\n", "plain-hash.yml"), { x: "a#b" });
  assert.deepEqual(parseYamlSubset("x: a # trailing comment\n", "plain-comment.yml"), { x: "a" });
  assert.deepEqual(parseYamlSubset("x: a 'b # trailing comment\n", "plain-quote-comment.yml"), { x: "a 'b" });
  assert.deepEqual(
    parseYamlSubset("x: https://example.test/a#fragment\n", "plain-url.yml"),
    { x: "https://example.test/a#fragment" },
  );
  assert.throws(
    () => parseYamlSubset("x: 'a' junk 'b'\n", "single-quote-junk.yml"),
    /invalid single-quoted scalar/,
  );
  assert.throws(() => parseYamlSubset("x: a: b\n", "plain-colon.yml"), /invalid plain scalar/);
  assert.throws(() => parseYamlSubset("x: a:\n", "plain-colon-end.yml"), /invalid plain scalar/);
});

test("models the pull-request .NET quality job with exact step-local commands and artifacts", () => {
  validatePullRequestWorkflow(parseYamlSubset(
    readRepositoryFile(".github/workflows/dotnet-test.yml"),
    ".github/workflows/dotnet-test.yml",
  ));
});

test("associates every tracked setup-dotnet step with global.json and every solution restore with locked mode", () => {
  validateDotnetSetupAndRestorePolicy(workflowDocuments());
});

test("restores the locked repository graph before wrapper-induced OpenAPI builds", () => {
  const documents = workflowDocuments();
  validateOpenApiWrapperBuild(readRepositoryFile("scripts/openapi.mjs"));
  validateOpenApiWrapperRestorePolicy(documents);

  for (const [workflowPath, jobName] of [
    [".github/workflows/openapi-contract.yml", "contract"],
    [".github/workflows/typescript-sdk.yml", "sdk"],
  ]) {
    const commands = documents[workflowPath].jobs[jobName].steps
      .flatMap((step) => typeof step.run === "string" ? step.run.split("\n").map((line) => line.trim()) : []);
    assert.equal(
      commands.includes("dotnet restore src/Cmsify.Api/Cmsify.Api.csproj --locked-mode"),
      true,
      `${workflowPath}:${jobName}: PR-safe API restore`,
    );
    assert.equal(commands.includes("dotnet restore Cmsify.slnx --locked-mode"), false);
  }

  const withoutNoRestore = readRepositoryFile("scripts/openapi.mjs").replace(', "--no-restore"', "");
  assert.throws(() => validateOpenApiWrapperBuild(withoutNoRestore), /must consume a prior restore/);

  for (const [workflowPath, jobName] of [
    [".github/workflows/openapi-contract.yml", "contract"],
    [".github/workflows/typescript-sdk.yml", "sdk"],
    [".github/workflows/publish-cmsify.yml", "build"],
  ]) {
    const mutation = structuredClone(documents);
    for (const step of mutation[workflowPath].jobs[jobName].steps) {
      if (typeof step.run === "string") {
        step.run = step.run.replace(
          /^\s*dotnet restore (?:Cmsify\.slnx|src\/Cmsify\.Api\/Cmsify\.Api\.csproj) --locked-mode\s*\n?/m,
          "",
        );
      }
    }
    assert.throws(
      () => validateOpenApiWrapperRestorePolicy(mutation),
      new RegExp(`${workflowPath.replaceAll(".", "\\.")}:${jobName}: OpenAPI wrapper`),
    );
  }
});

test("defers package-dependent Admin accessibility on pull requests without hiding the main-branch gate", () => {
  const workflow = parseYamlSubset(
    readRepositoryFile(".github/workflows/admin-accessibility.yml"),
    ".github/workflows/admin-accessibility.yml",
  );
  const steps = workflow.jobs.axe.steps;
  const deferred = steps.find((step) => step.name === "Report deferred PR accessibility");
  assert.notEqual(deferred, undefined);
  assert.equal(deferred.if, pullRequestCondition);
  assert.match(deferred.run, /SyntaxCircus\.Http\.Resilience.*public/i);

  for (const stepName of [
    "Restore and build Admin source",
    "Install locked accessibility harness",
    "Run Admin source",
    "Certify login accessibility",
  ]) {
    const step = steps.find((candidate) => candidate.name === stepName);
    assert.notEqual(step, undefined, stepName);
    assert.equal(step.if, nonPullRequestCondition, stepName);
  }
});

test("recognizes supported OpenAPI wrapper command and working-directory variants", () => {
  const validDocuments = {
    ".github/workflows/variants.yml": {
      defaults: { run: { "working-directory": "sdk/typescript" } },
      jobs: {
        inheritedNpm: {
          steps: [
            {
              "working-directory": ".",
              run: "dotnet restore Cmsify.slnx --locked-mode",
            },
            { run: "npm run generate:check -- --verbose" },
          ],
        },
        prefixedNpm: {
          defaults: { run: { "working-directory": "." } },
          steps: [
            { run: "dotnet restore Cmsify.slnx --locked-mode" },
            { run: "npm --prefix sdk/typescript run generate:check" },
          ],
        },
        relativeNode: {
          defaults: { run: { "working-directory": "sdk/typescript" } },
          steps: [
            { "working-directory": "./", run: "dotnet restore Cmsify.slnx --locked-mode" },
            { run: "node ../../scripts/openapi.mjs update" },
          ],
        },
      },
    },
  };
  assert.doesNotThrow(() => validateOpenApiWrapperRestorePolicy(validDocuments));

  for (const jobName of ["inheritedNpm", "prefixedNpm", "relativeNode"]) {
    const withoutRestore = structuredClone(validDocuments);
    withoutRestore[".github/workflows/variants.yml"].jobs[jobName].steps.shift();
    assert.throws(
      () => validateOpenApiWrapperRestorePolicy(withoutRestore),
      new RegExp(`variants\\.yml:${jobName}: OpenAPI wrapper`),
    );
  }
});

test("ignores comments, echoed text, and unrelated package scripts", () => {
  assert.doesNotThrow(() => validateOpenApiWrapperRestorePolicy({
    ".github/workflows/non-wrappers.yml": {
      jobs: {
        examples: {
          steps: [{
            run: `# npm run generate:check
echo "node scripts/openapi.mjs check"
npm run generate:check
node other/scripts/openapi.mjs export`,
          }],
        },
      },
    },
  }));
});

test("parses the real OpenAPI build call without comment or formatting false results", () => {
  const source = readRepositoryFile("scripts/openapi.mjs");
  const equivalentFormatting = source.replace(
    'run("dotnet", ["build", apiProject, "--configuration", "Release", "--no-restore", "--nologo"]);',
    "run ( 'dotnet' , [ 'build', apiProject, '--configuration', 'Release', '--no-restore', '--nologo' ] );",
  );
  assert.notEqual(equivalentFormatting, source);
  assert.doesNotThrow(() => validateOpenApiWrapperBuild(equivalentFormatting));

  const commentedOutBuild = source.replace(
    '  run("dotnet", ["build", apiProject, "--configuration", "Release", "--no-restore", "--nologo"]);',
    '  // run("dotnet", ["build", apiProject, "--configuration", "Release", "--no-restore", "--nologo"]);',
  );
  assert.notEqual(commentedOutBuild, source);
  assert.throws(
    () => validateOpenApiWrapperBuild(commentedOutBuild),
    /exactly one API repository build/,
  );
});

test("isolates the release consumer from the checked-out repository policy ancestry", () => {
  validateReleaseConsumer(parseYamlSubset(
    readRepositoryFile(".github/workflows/publish-cmsify.yml"),
    ".github/workflows/publish-cmsify.yml",
  ));
});

test("models Docker instructions as exact source-destination copies before locked restore", () => {
  for (const { dockerfile, project } of [
    { dockerfile: "src/Cmsify.Api/Dockerfile", project: "src/Cmsify.Api/Cmsify.Api.csproj" },
    { dockerfile: "src/Cmsify.Admin/Dockerfile", project: "src/Cmsify.Admin/Cmsify.Admin.csproj" },
  ]) {
    validateDockerfilePolicy(readRepositoryFile(dockerfile), dockerfile, project);
  }
});

test("models four enabled weekly Dependabot ecosystems with real Docker directories", () => {
  validateDependabotPolicy(parseYamlSubset(
    readRepositoryFile(".github/dependabot.yml"),
    ".github/dependabot.yml",
  ));
});

test("does not automate Dependabot pull-request merging", () => {
  validateNoDependabotAutoMerge(workflowDocuments());
});

test("semantic policies reject cross-object and review-regression mutations", () => {
  const pullRequestWorkflow = parseYamlSubset(
    readRepositoryFile(".github/workflows/dotnet-test.yml"),
    ".github/workflows/dotnet-test.yml",
  );
  const movedStep = structuredClone(pullRequestWorkflow);
  movedStep.jobs["release-contract"].steps.push(movedStep.jobs.test.steps.splice(9, 1)[0]);
  assert.throws(() => validatePullRequestWorkflow(movedStep), /Upload coverage summary|deep-equal|Expected values/);

  const orphanedInput = {
    uses: "actions/setup-dotnet@v4",
    with: {},
  };
  const unrelatedCheckout = { uses: "actions/checkout@v4", with: { "global-json-file": "global.json" } };
  assert.equal(unrelatedCheckout.with["global-json-file"], "global.json");
  assert.throws(() => validateSetupDotnetStep(orphanedInput, "orphaned.yml:build"), /owns global-json-file/);

  const validDependabot = parseYamlSubset(readRepositoryFile(".github/dependabot.yml"), ".github/dependabot.yml");
  const dockerUpdate = validDependabot.updates.find((update) => update["package-ecosystem"] === "docker");
  delete dockerUpdate.directory;
  dockerUpdate.directories = ["/src/Cmsify.Api", "/src/Cmsify.Admin"];
  validDependabot.updates[0]["open-pull-requests-limit"] = 0;
  assert.throws(() => validateDependabotPolicy(validDependabot), /cannot be disabled/);

  const apiDockerfile = readRepositoryFile("src/Cmsify.Api/Dockerfile");
  const wrongDestination = apiDockerfile.replace(
    'COPY ["src/Cmsify.Api/packages.lock.json", "src/Cmsify.Api/"]',
    'COPY ["src/Cmsify.Api/packages.lock.json", "src/Cmsify.Core/"]',
  );
  assert.notEqual(wrongDestination, apiDockerfile);
  assert.throws(
    () => validateDockerfilePolicy(wrongDestination, "mutated-api.Dockerfile", "src/Cmsify.Api/Cmsify.Api.csproj"),
    /mutated-api\.Dockerfile/,
  );

  const validRelease = parseYamlSubset(readRepositoryFile(".github/workflows/publish-cmsify.yml"), ".github/workflows/publish-cmsify.yml");
  const consumerSteps = validRelease.jobs["dotnet-consumer"].steps;
  consumerSteps[0].with.path = "source";
  consumerSteps[1].with = { "global-json-file": "source/global.json" };
  consumerSteps[3].run = expectedReleaseConsumerRun.replace(
    '$RUNNER_TEMP/cmsify-dotnet-consumer',
    '$GITHUB_WORKSPACE/source/consumer',
  );
  assert.throws(() => validateReleaseConsumer(validRelease), /Expected values|GITHUB_WORKSPACE/);
});

test("Dependabot policy rejects ignored major and all-package updates", () => {
  const dependabot = parseYamlSubset(readRepositoryFile(".github/dependabot.yml"), ".github/dependabot.yml");
  const ignoredAllUpdates = structuredClone(dependabot);
  ignoredAllUpdates.updates[0].ignore = [{ "dependency-name": "*" }];
  assert.throws(() => validateDependabotPolicy(ignoredAllUpdates), /allowed keys|ignore/);

  const ignoredMajors = structuredClone(dependabot);
  ignoredMajors.updates[0].ignore = [{
    "dependency-name": "*",
    "update-types": ["version-update:semver-major"],
  }];
  assert.throws(() => validateDependabotPolicy(ignoredMajors), /allowed keys|ignore/);
});

test("auto-merge policy recursively rejects a Dependabot-triggered write-capable github-script merge", () => {
  const mergeWorkflow = {
    ".github/workflows/mutated-auto-merge.yml": {
      on: { pull_request_target: { types: ["opened", "synchronize"] } },
      permissions: { contents: "write", "pull-requests": "write" },
      jobs: {
        merge: {
          if: "github.actor == 'dependabot[bot]'",
          steps: [{
            uses: "actions/github-script@pinned",
            with: { script: "await github.rest.pulls.merge({ owner, repo, pull_number });" },
          }],
        },
      },
    },
  };
  assert.throws(
    () => validateNoDependabotAutoMerge(mergeWorkflow),
    /Dependabot-triggered write-capable merge job|merge-capable scalar/,
  );
  assert.doesNotThrow(() => validateNoDependabotAutoMerge({
    ".github/workflows/ordinary-words.yml": {
      on: "pull_request",
      permissions: { contents: "read" },
      jobs: {
        check: {
          steps: [{
            name: "Merge coverage summaries",
            env: { NOTE: "Dependabot updates remain individual pull requests" },
            run: "echo checked",
          }],
        },
      },
    },
  }));
});

test("Docker policy rejects restore inputs moved outside the selected SDK stage", () => {
  const dockerfile = readRepositoryFile("src/Cmsify.Api/Dockerfile");
  const wrongStage = dockerfile.replace(
    "FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build",
    "FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build\nFROM scratch AS misplaced-restore",
  );
  assert.notEqual(wrongStage, dockerfile);
  assert.throws(
    () => validateDockerfilePolicy(wrongStage, "wrong-stage.Dockerfile", "src/Cmsify.Api/Cmsify.Api.csproj"),
    /wrong-stage\.Dockerfile/,
  );
});
