import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const defaultRoot = resolve(fileURLToPath(new URL("../..", import.meta.url)));
const rootArgument = process.argv.indexOf("--root");
const repositoryRoot = rootArgument === -1 ? defaultRoot : resolve(process.argv[rootArgument + 1] ?? defaultRoot);
const errors = [];

function file(relativePath) {
  const path = resolve(repositoryRoot, relativePath);
  if (!existsSync(path)) {
    errors.push(`Missing required release file: ${relativePath}`);
    return "";
  }
  return readFileSync(path, "utf8");
}

function expect(condition, message) {
  if (!condition) errors.push(message);
}

function fixtureSupplyChainFiles(root) {
  const files = [];
  const visit = (directory) => {
    if (!existsSync(directory)) return;
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if ([".git", "artifacts", "bin", "node_modules", "obj"].includes(entry.name)) continue;
      const entryPath = resolve(directory, entry.name);
      if (entry.isDirectory()) visit(entryPath);
      else {
        const repositoryPath = relative(root, entryPath).replaceAll("\\", "/");
        if ((repositoryPath.startsWith(".github/workflows/") && /\.ya?ml$/i.test(repositoryPath))
          || /(?:^|\/)(?:docker-)?compose[^/]*\.ya?ml$/i.test(repositoryPath)
          || ["src/Cmsify.Api/Dockerfile", "src/Cmsify.Admin/Dockerfile"].includes(repositoryPath)) files.push(repositoryPath);
      }
    }
  };
  visit(root);
  return files.sort();
}

function supplyChainFiles(root) {
  try {
    return execFileSync("git", ["ls-files", "--",
      ":(glob).github/workflows/*.yml",
      ":(glob).github/workflows/*.yaml",
      ":(glob)**/compose*.yml",
      ":(glob)**/compose*.yaml",
      ":(glob)**/docker-compose*.yml",
      ":(glob)**/docker-compose*.yaml",
      "src/Cmsify.Api/Dockerfile",
      "src/Cmsify.Admin/Dockerfile",
    ], { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] })
      .split(/\r?\n/)
      .filter(Boolean)
      .sort();
  } catch {
    return fixtureSupplyChainFiles(root);
  }
}

function supplyChainError(relativePath, line, message) {
  return `${relativePath}:${line}: ${message}`;
}

function hasPinnedDigest(reference) {
  return /@sha256:[0-9a-f]{64}$/i.test(reference);
}

function hasVerifiedUpgradeComposeInput(relativePath, reference) {
  return relativePath === "tests/upgrade/compose.yml" && [
    "${POSTGRES_IMAGE}",
    "${MINIO_IMAGE}",
    "${BASELINE_API_IMAGE}",
    "${CANDIDATE_API_IMAGE:-cmsify-upgrade-candidate:local}",
  ].includes(reference);
}

function shellTokens(lines) {
  const tokens = [];
  let value = "";
  let tokenLine;
  let quote;
  const flush = () => {
    if (value !== "") tokens.push({ line: tokenLine, value });
    value = "";
    tokenLine = undefined;
  };
  for (const { line, text } of lines) {
    for (let index = 0; index < text.length; index += 1) {
      const character = text[index];
      if (quote !== undefined) {
        if (character === quote) quote = undefined;
        else value += character;
        continue;
      }
      if (character === "'" || character === '"') {
        if (value === "") tokenLine = line;
        quote = character;
      } else if (character === "&" && text[index + 1] === "&") {
        flush();
        tokens.push({ line, value: "&&" });
        index += 1;
      } else if (character === "|" && text[index + 1] === "|") {
        flush();
        tokens.push({ line, value: "||" });
        index += 1;
      } else if (character === ";") {
        flush();
        tokens.push({ line, value: ";" });
      } else if (/\s/.test(character)) {
        flush();
      } else {
        if (value === "") tokenLine = line;
        value += character;
      }
    }
    flush();
  }
  return tokens;
}

function workflowRunCommands(contents) {
  const lines = contents.replaceAll("\r\n", "\n").split("\n");
  const commands = [];
  for (let index = 0; index < lines.length; index += 1) {
    const run = /^(\s*)(?:-\s+)?run:\s*(.*)$/.exec(lines[index]);
    if (!run) continue;
    const indentation = run[1].length;
    const block = /^[>|][+-]?$/.test(run[2].trim());
    const commandLines = block ? [] : [{ line: index + 1, text: run[2] }];
    while (block && index + 1 < lines.length) {
      const next = lines[index + 1];
      if (next.trim() !== "" && next.match(/^\s*/)[0].length <= indentation) break;
      index += 1;
      commandLines.push({ line: index + 1, text: next.trim() });
    }
    if (run[2].trim().startsWith(">")) {
      let folded = [];
      for (const commandLine of commandLines) {
        if (commandLine.text === "") {
          if (folded.length > 0) commands.push(shellTokens(folded));
          folded = [];
        } else folded.push(commandLine);
      }
      if (folded.length > 0) commands.push(shellTokens(folded));
    } else {
      let literal = [];
      for (const commandLine of commandLines) {
        literal.push({ line: commandLine.line, text: commandLine.text.replace(/\\\s*$/, "") });
        if (!/\\\s*$/.test(commandLine.text)) {
          commands.push(shellTokens(literal));
          literal = [];
        }
      }
      if (literal.length > 0) commands.push(shellTokens(literal));
    }
  }
  return commands;
}

function shellCommandSegments(tokens) {
  const commands = [];
  let command = [];
  for (const token of tokens) {
    if (["&&", "||", ";"].includes(token.value)) {
      if (command.length > 0) commands.push(command);
      command = [];
    } else command.push(token);
  }
  if (command.length > 0) commands.push(command);
  return commands;
}

function dockerCommandKind(tokens) {
  if (tokens[0]?.value !== "docker") return undefined;
  if (tokens[1]?.value === "run") return "run";
  if (tokens[1]?.value === "build" || (tokens[1]?.value === "buildx" && tokens[2]?.value === "build")) return "build";
  return undefined;
}

const dockerRunOptionsWithValues = new Set(["--add-host", "--annotation", "--cidfile", "--cpus", "--entrypoint", "--env", "--env-file", "--label", "--label-file", "--mount", "--name", "--network", "--platform", "--publish", "--runtime", "--user", "--volume", "--workdir", "-e", "-p", "-u", "-v", "-w"]);

function dockerRunImage(tokens) {
  if (dockerCommandKind(tokens) !== "run") return undefined;
  for (let cursor = 2; cursor < tokens.length; cursor += 1) {
    const option = tokens[cursor].value;
    if (!option.startsWith("-")) return tokens[cursor];
    if (!option.includes("=") && (dockerRunOptionsWithValues.has(option) || /^-[epuvw]$/.test(option))) cursor += 1;
  }
  return undefined;
}

function dockerBuildTags(tokens) {
  if (dockerCommandKind(tokens) !== "build") return [];
  const tags = [];
  for (let cursor = 1; cursor < tokens.length; cursor += 1) {
    const option = tokens[cursor].value;
    if ((option === "--tag" || option === "-t") && tokens[cursor + 1]) tags.push(tokens[++cursor].value);
    else if (option.startsWith("--tag=")) tags.push(option.slice("--tag=".length));
    else if (option.startsWith("-t") && option.length > 2) tags.push(option.slice(2));
  }
  return tags;
}

function workflowImageReferences(contents) {
  const builtImages = new Set();
  const references = [];
  for (const shellLine of workflowRunCommands(contents)) {
    for (const command of shellCommandSegments(shellLine)) {
      const image = dockerRunImage(command);
      if (image) references.push({ ...image, builtEarlier: builtImages.has(image.value) });
      for (const tag of dockerBuildTags(command)) builtImages.add(tag);
    }
  }
  return references;
}

export function validateRepositorySupplyChain(root) {
  const violations = [];
  for (const relativePath of supplyChainFiles(root)) {
    const contents = readFileSync(resolve(root, relativePath), "utf8");
    const isWorkflow = relativePath.startsWith(".github/workflows/");
    const stageAliases = new Set(["scratch"]);
    const workflowImages = isWorkflow ? workflowImageReferences(contents) : [];
    for (const [index, line] of contents.replaceAll("\r\n", "\n").split("\n").entries()) {
      const lineNumber = index + 1;
      const action = line.match(/^\s*(?:-\s+)?uses:\s*([^\s#]+)/);
      if (action && !action[1].startsWith("./") && !/^[^/\s]+\/[^@\s]+@[0-9a-f]{40}$/i.test(action[1])) {
        violations.push(supplyChainError(relativePath, lineNumber, `action reference must use owner/repository@40-hex-SHA: ${action[1]}`));
      } else if (action && !action[1].startsWith("./") && !/\s#\s+v\d+(?:\.\d+){0,2}(?:[-+][0-9A-Za-z.-]+)?\s*$/.test(line)) {
        violations.push(supplyChainError(relativePath, lineNumber, `action reference must include a version comment: ${action[1]}`));
      }

      const from = line.match(/^\s*FROM\s+([^\s]+)(?:\s+AS\s+([^\s]+))?/i);
      if (from) {
        const [, reference, alias] = from;
        if (!stageAliases.has(reference) && !hasPinnedDigest(reference)) {
          violations.push(supplyChainError(relativePath, lineNumber, `runtime image must use @sha256:<64 hex>: ${reference}`));
        }
        if (alias) stageAliases.add(alias);
      }

      const composeImage = line.match(/^\s*image:\s*(.*?)\s*(?:#.*)?$/);
      const imageReferences = [
        ...(composeImage ? [composeImage[1].trim().replaceAll(/^["']|["']$/g, "")] : []),
      ];
      for (const reference of imageReferences) {
        if (stageAliases.has(reference) || hasVerifiedUpgradeComposeInput(relativePath, reference)) continue;
        if (!hasPinnedDigest(reference)) {
          violations.push(supplyChainError(relativePath, lineNumber, `runtime image must use @sha256:<64 hex>: ${reference}`));
        }
      }
    }
    for (const { line, value, builtEarlier } of workflowImages) {
      if (value.startsWith("$") || builtEarlier) continue;
      if (!hasPinnedDigest(value)) {
        violations.push(supplyChainError(relativePath, line, `runtime image must use @sha256:<64 hex>: ${value}`));
      }
    }
  }
  return violations;
}

function projectMetadata(relativePath) {
  const contents = file(relativePath);
  expect(/<TargetFramework>net10\.0<\/TargetFramework>/i.test(contents), `${relativePath} must support .NET 10 only.`);
  expect(/<PackageLicenseExpression>MIT<\/PackageLicenseExpression>/i.test(contents), `${relativePath} must declare the MIT package license.`);
}

const sourceLicense = file("LICENSE");
expect(/GNU AFFERO GENERAL PUBLIC LICENSE/i.test(sourceLicense), "Repository/server source must remain AGPL-3.0-or-later.");

const sourceVersion = file("Directory.Build.props");
expect(/<Version[^>]*>0\.0\.0-local<\/Version>/i.test(sourceVersion), "Source builds must use the non-publishable 0.0.0-local version.");
expect(/<IsPackable[^>]*CmsifyReleaseBuild[^>]*>false<\/IsPackable>/i.test(sourceVersion) && /RequireCmsifyReleaseInputs/i.test(sourceVersion), "Source .NET packages must be non-packable unless validated release inputs explicitly enable packing.");

const packageJson = file("sdk/typescript/package.json");
try {
  const typeScriptPackage = JSON.parse(packageJson);
  expect(typeScriptPackage.license === "MIT", "@cmsify/client must declare the MIT license.");
  expect(typeScriptPackage.version === "0.0.0-local", "@cmsify/client source version must be 0.0.0-local.");
  expect(typeScriptPackage.private === true, "@cmsify/client source package must be private until the validated release build overrides it.");
  expect(typeScriptPackage.repository?.type === "git" && typeScriptPackage.repository?.url === "git+https://github.com/Syntax-Circus/cmsify.git" && typeScriptPackage.repository?.directory === "sdk/typescript", "@cmsify/client repository must use canonical Syntax-Circus GitHub identity and sdk/typescript directory for trusted publishing provenance.");
  expect(/^>=20(?:\.0\.0)?$/.test(typeScriptPackage.engines?.node ?? ""), "@cmsify/client must require Node 20 or later.");
} catch {
  errors.push("sdk/typescript/package.json must be valid JSON.");
}
try {
  const typeScriptLock = JSON.parse(file("sdk/typescript/package-lock.json"));
  const rootPackage = typeScriptLock.packages?.[""];
  expect(rootPackage?.repository?.type === "git" && rootPackage?.repository?.url === "git+https://github.com/Syntax-Circus/cmsify.git" && rootPackage?.repository?.directory === "sdk/typescript", "sdk/typescript/package-lock.json repository must use canonical Syntax-Circus GitHub identity and sdk/typescript directory.");
} catch {
  errors.push("sdk/typescript/package-lock.json must be valid JSON.");
}
expect(/MIT License/i.test(file("sdk/typescript/LICENSE")), "@cmsify/client archive must include the MIT license text.");

for (const project of [
  "src/Cmsify.Contracts/Cmsify.Contracts.csproj",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client/SyntaxCircus.Cmsify.Client.csproj",
  "sdk/dotnet/src/SyntaxCircus.Cmsify.Client.DistributedCaching/SyntaxCircus.Cmsify.Client.DistributedCaching.csproj",
]) projectMetadata(project);

for (const [relativePath, kind, title] of [
  ["src/Cmsify.Api/Dockerfile", "api", "API"],
  ["src/Cmsify.Admin/Dockerfile", "admin", "Admin"],
]) {
  const dockerfile = file(relativePath);
  expect(dockerfile.includes(`org.opencontainers.image.ref.name="syntaxcircus/cmsify-${kind}:\${BUILD_VERSION}"`), `${title} Dockerfile ref.name label must bind its exact qualified image identity.`);
}

const workflowPath = ".github/workflows/publish-cmsify.yml";
const workflow = file(workflowPath);
const upgradeWorkflowPath = ".github/workflows/upgrade-rollback.yml";
const upgradeWorkflow = file(upgradeWorkflowPath);
expect(!existsSync(resolve(repositoryRoot, ".github/workflows/npm-publish-cmsify-client.yml")), "A separate npm publication workflow is forbidden; promotion must be unified.");
expect(/\bpush:\s*\n\s+tags:/m.test(workflow) && !/\bbranches:/m.test(workflow), "Release workflow must be tag-only; branch builds never publish or tag.");
expect(/node scripts\/release\/validate-release-tag\.mjs\s+"?\$\{\{\s*github\.ref_name\s*\}\}"?/i.test(workflow) || /validate-release-tag\.mjs/i.test(workflow), "Release workflow must validate the vX.Y.Z or vX.Y.Z-prerelease tag.");
expect(/validate-release-tag\.mjs[^\n]*--require-changelog/i.test(workflow), "Reviewed tag promotion must require an exact dated changelog entry.");
expect(/source_sha/i.test(workflow) && /needs\.resolve\.outputs\.source_sha/i.test(workflow), "Release workflow must carry one resolved immutable source SHA into build and promotion.");
expect(/is_prerelease/i.test(workflow) && /npm_channel/i.test(workflow), "Release workflow must derive prerelease state and npm channel from validated SemVer.");
expect(/npm pkg delete private[\s\S]*npm pkg set version=/s.test(workflow), "Release npm candidate must remove private rather than serializing it as a string.");
expect(/npm pkg set version="\$VERSION" gitHead="\$SOURCE_SHA"[\s\S]*npm pack --pack-destination/s.test(workflow), "Release npm candidate gitHead must equal resolved SOURCE_SHA before its sole npm pack.");
const nugetPackCommands = [...workflow.matchAll(/dotnet pack[^\n]+/g)].map((match) => match[0]);
expect(nugetPackCommands.length === 3 && nugetPackCommands.every((command) => command.includes('-p:RepositoryCommit="$SOURCE_SHA"') && command.includes("-p:IncludeSymbols=false")), "All three NuGet candidates must bind RepositoryCommit to SOURCE_SHA and suppress symbol packages explicitly.");

for (const match of workflow.matchAll(/^\s*-?\s*uses:\s*([^\s#]+)/gm)) {
  expect(/@[0-9a-f]{40}$/i.test(match[1]), `Release action must be pinned by immutable SHA: ${match[1]}`);
}
for (const match of upgradeWorkflow.matchAll(/^\s*-?\s*uses:\s*([^\s#]+)/gm)) {
  expect(/@[0-9a-f]{40}$/i.test(match[1]), `Upgrade workflow action must be pinned by immutable SHA: ${match[1]}`);
}

for (const job of ["resolve", "build", "upgrade-rollback", "certify", "promote"]) {
  expect(new RegExp(`^\\s{2}${job}:`, "m").test(workflow), `Release workflow must include the ${job} job.`);
}

expect(/workflow_dispatch:/i.test(upgradeWorkflow) && /push:\s*\n\s+branches:\s*\[main\]/i.test(upgradeWorkflow) && /pull_request:/i.test(upgradeWorkflow), "Dedicated upgrade workflow must run on manual dispatch, main pushes, and pull requests.");
for (const requiredPath of [
  "src/Cmsify.Infrastructure/**",
  "src/Cmsify.Api/**",
  "src/Cmsify.Core/**/Media*.cs",
  "eng/upgrade-tests/**",
  "tests/upgrade/**",
  "**/Dockerfile",
  "**/compose*.yml",
  "**/compose*.yaml",
  "**/docker-compose*.yml",
  "**/docker-compose*.yaml",
  ".github/workflows/publish-cmsify.yml",
  ".github/workflows/upgrade-rollback.yml",
]) {
  const occurrences = upgradeWorkflow.split(`\"${requiredPath}\"`).length - 1;
  expect(occurrences >= 2, `Upgrade workflow path triggers must include ${requiredPath} for main pushes and pull requests.`);
}
expect(/actions\/setup-node@[0-9a-f]{40}[\s\S]*node-version:\s*"22"/s.test(upgradeWorkflow), "Dedicated upgrade workflow must use SHA-pinned Node 22 setup.");
expect(/docker\/setup-buildx-action@[0-9a-f]{40}[\s\S]*driver:\s*docker-container/s.test(upgradeWorkflow), "Dedicated upgrade workflow must use a SHA-pinned docker-container Buildx builder.");
const dedicatedBuildCommands = [...upgradeWorkflow.matchAll(/docker buildx build[^\n]+/g)].map((match) => match[0]);
expect(dedicatedBuildCommands.length === 1 && /--platform linux\/amd64/.test(dedicatedBuildCommands[0]) && /--provenance=false/.test(dedicatedBuildCommands[0]) && /--cache-from type=gha/.test(dedicatedBuildCommands[0]) && /--cache-to type=gha,mode=max/.test(dedicatedBuildCommands[0]) && /--file src\/Cmsify\.Api\/Dockerfile/.test(dedicatedBuildCommands[0]) && /--load/.test(dedicatedBuildCommands[0]), "Dedicated upgrade workflow must build and load one cached linux/amd64 candidate from the production API Dockerfile.");
expect(/CANDIDATE_VERSION:\s*1\.0\.0-ci\.\$\{\{ github\.run_number \}\}/.test(upgradeWorkflow) && /SOURCE_SHA:\s*\$\{\{ github\.sha \}\}/.test(upgradeWorkflow), "Dedicated upgrade candidate must bind version 1.0.0-ci.<run_number> and the source SHA.");
expect(/BUILD_VERSION=\$CANDIDATE_VERSION[\s\S]*BUILD_INFORMATIONAL_VERSION=\$CANDIDATE_VERSION\+\$SOURCE_SHA[\s\S]*BUILD_SOURCE_REVISION=\$SOURCE_SHA/s.test(upgradeWorkflow), "Dedicated upgrade candidate labels must bind version and source SHA.");
expect(/node eng\/upgrade-tests\/cli\.mjs verify-fixture --fixture tests\/upgrade\/fixtures\/v0\.1\.3/.test(upgradeWorkflow), "Dedicated upgrade workflow must verify the checked-in fixture.");
const deterministicFixtureCheck = upgradeWorkflow.indexOf("node eng/upgrade-tests/cli.mjs generate-fixture --fixture tests/upgrade/fixtures/v0.1.3 --check");
const dedicatedRehearsal = upgradeWorkflow.indexOf("node --test tests/upgrade/integration/rehearsal.test.mjs");
expect(deterministicFixtureCheck >= 0 && dedicatedRehearsal > deterministicFixtureCheck && /CMSIFY_UPGRADE_TEST:\s*"1"/.test(upgradeWorkflow), "Deterministic fixture checking must complete before the opt-in full rehearsal.");
expect(/if:\s*failure\(\)[\s\S]*actions\/upload-artifact@[0-9a-f]{40}[\s\S]*path:\s*artifacts\/upgrade-tests\/\*\*/s.test(upgradeWorkflow), "Dedicated upgrade workflow must upload sanitized diagnostics on failure.");

expect(/build:[\s\S]*dotnet pack[\s\S]*npm pack[\s\S]*docker buildx build[\s\S]*verify-release-artifacts\.mjs[\s\S]*upload-artifact/s.test(workflow), "The build job must build candidate NuGet, npm, and OCI artifacts once, verify them, and upload one candidate artifact.");
const ociBuildCommands = [...workflow.matchAll(/docker buildx build[^\n]+/g)].map((match) => match[0]);
expect(ociBuildCommands.length === 2 && ociBuildCommands.every((command) => command.includes("--platform linux/amd64") && command.includes("--provenance=false")), "Each OCI candidate build must use --provenance=false to expose one exact single linux/amd64 manifest descriptor; release provenance is attached after candidate certification.");
expect(/docker\/setup-buildx-action@[0-9a-f]{40}[\s\S]*driver:\s*docker-container/s.test(workflow), "OCI candidates require a SHA-pinned docker-container Buildx builder.");
expect(/anchore\/sbom-action\/download-syft@[0-9a-f]{40}[\s\S]*syft-version:/s.test(workflow), "Candidate SBOM generation must explicitly provision a pinned SBOM tool.");
expect(/docker run -d[\s\S]*health\/live[\s\S]*curl[\s\S]*18081/s.test(workflow), "OCI candidate verification must start the exact API/Admin images and probe health/static behavior.");
const smoke = workflow.match(/- name: Smoke exact OCI candidates[\s\S]*?(?=^\s{6}- name:|^\s{6}- uses:|^\s{6}- run:)/m)?.[0] ?? "";
const firstSmokeResource = smoke.indexOf("docker network create cmsify-smoke");
const cleanupRegistration = smoke.indexOf("trap cleanup EXIT");
const firstContainer = smoke.indexOf("docker run -d --name cmsify-postgres-smoke");
const beforeCleanup = firstSmokeResource >= 0 && cleanupRegistration > firstSmokeResource ? smoke.slice(firstSmokeResource + "docker network create cmsify-smoke".length, cleanupRegistration) : "";
expect(firstSmokeResource >= 0 && cleanupRegistration > firstSmokeResource && cleanupRegistration < firstContainer && !/docker\s+(?:run|network create)\b/.test(beforeCleanup), "OCI smoke cleanup must be registered immediately after first resource creation and before any container is created.");
for (const container of ["cmsify-postgres-smoke", "cmsify-api-smoke", "cmsify-admin-smoke"]) {
  const runLine = smoke.match(new RegExp(`^\\s*docker run[^\\n]*--name ${container}[^\\n]*$`, "m"))?.[0] ?? "";
  expect(runLine.includes("docker run -d --name") && !runLine.includes("--rm"), `OCI smoke ${container} must omit --rm so failure logs remain available.`);
}
expect(/cleanup\(\)[\s\S]*status=\$\?[\s\S]*docker logs cmsify-api-smoke[\s\S]*docker logs cmsify-admin-smoke[\s\S]*docker logs cmsify-postgres-smoke[\s\S]*docker rm -f cmsify-api-smoke cmsify-admin-smoke cmsify-postgres-smoke[\s\S]*docker network rm cmsify-smoke/s.test(smoke), "OCI smoke failure must show logs and clean every container and network on every exit.");
const firstContainerRemoval = smoke.indexOf("docker rm -f");
const lastFailureLog = Math.max(...["cmsify-api-smoke", "cmsify-admin-smoke", "cmsify-postgres-smoke"].map((container) => smoke.indexOf(`docker logs ${container}`)));
expect(lastFailureLog >= 0 && firstContainerRemoval > lastFailureLog, "OCI smoke failure logs must be collected before any container remove operation.");
expect(/for attempt in \{1\.\.30\}; do[\s\S]*pg_isready[\s\S]*test "\$postgres_ready" = true/s.test(smoke), "PostgreSQL readiness must be bounded and fail closed.");
expect(/candidates_ready=false\s+for attempt in \{1\.\.30\}; do[\s\S]*--connect-timeout 2 --max-time 5[\s\S]*test "\$candidates_ready" = true/s.test(smoke), "API/Admin readiness must use bounded attempts and request timeouts.");
expect(/127\.0\.0\.1:18081\/[\s\S]*grep -Fq '<title>Cmsify Admin<\/title>'/s.test(smoke), "Admin smoke probe must assert Cmsify-specific content after HTTP success.");
expect(/cmsify-api\.metadata\.json[\s\S]*containerimage\.descriptor[\s\S]*size:[\s\S]*mediaType:[\s\S]*platform:[\s\S]*release-manifest\.json/s.test(workflow), "Candidate manifest must bind OCI descriptor digest, size, media type, and platform before certification.");
expect(/finalize-spdx\.mjs --artifacts artifacts --version "\$VERSION" --source-sha "\$SOURCE_SHA"/s.test(workflow) && existsSync(resolve(repositoryRoot, "scripts/release/finalize-spdx.mjs")), "All four SPDX documents must receive stable exact document/source/package identities before certification.");
expect(/dotnet-consumer:[\s\S]*setup-dotnet[\s\S]*download-artifact[\s\S]*dotnet new console[\s\S]*SyntaxCircus\.Cmsify\.Contracts[\s\S]*SyntaxCircus\.Cmsify\.Client[\s\S]*SyntaxCircus\.Cmsify\.Client\.DistributedCaching/s.test(workflow), "Release workflow must install all three candidate packages into a clean .NET 10 consumer.");
expect(/node-consumer:[\s\S]*matrix:[\s\S]*node-version:\s*\["20", "22"\][\s\S]*download-artifact[\s\S]*CMSIFY_CLIENT_TARBALL=[\s\S]*npm run test:consumer/s.test(workflow), "Release workflow must install the candidate through the reused clean Node 20/22 consumer check.");
expect(/certify:[\s\S]*download-artifact[\s\S]*attest-build-provenance/s.test(workflow), "The certify job must attest the downloaded immutable candidate.");
expect(/promote:[\s\S]*environment:\s*release[\s\S]*download-artifact[\s\S]*git ls-remote[\s\S]*sha256sum --check[\s\S]*NuGet\/login@[0-9a-f]{40}[\s\S]*oras cp[\s\S]*oras manifest fetch[\s\S]*dotnet nuget push[\s\S]*npm publish[\s\S]*gh release create/s.test(workflow), "Protected promotion must revalidate the tag, promote certified OCI descriptors, and publish only the certified packages.");

function jobBody(name) {
  const start = workflow.search(new RegExp(`^  ${name}:`, "m"));
  if (start === -1) return "";
  const afterStart = workflow.slice(start + 1);
  const nextJob = afterStart.search(/^  [A-Za-z0-9_-]+:/m);
  return nextJob === -1 ? workflow.slice(start) : workflow.slice(start, start + 1 + nextJob);
}

function normalizedCondition(value) {
  return value.trim()
    .replace(/^(["'])|(["'])$/g, "")
    .replace(/^\$\{\{\s*/, "")
    .replace(/\s*\}\}$/, "")
    .trim();
}

function jobConditionRequiresSuccess(job) {
  const steps = job.search(/^    steps:/m);
  const preamble = steps === -1 ? job : job.slice(0, steps);
  const condition = preamble.match(/^    if:\s*(.+?)\s*$/m)?.[1];
  return condition === undefined || normalizedCondition(condition) === "success()";
}

function stepConditions(job) {
  return [...job.matchAll(/^(?:      - |        )if:\s*(.+?)\s*$/gm)].map((match) => normalizedCondition(match[1]));
}

function continueOnErrorIsDisabled(job) {
  return [...job.matchAll(/^\s+(?:-\s+)?continue-on-error:\s*(.+?)\s*$/gm)]
    .every((match) => normalizedCondition(match[1]) === "false");
}

const releaseUpgrade = jobBody("upgrade-rollback");
expect(/needs:\s*\[resolve, build\]/.test(releaseUpgrade), "Release upgrade job must consume resolve and build outputs.");
expect(/actions\/download-artifact@[0-9a-f]{40}[\s\S]*name:\s*release-candidate-\$\{\{ needs\.resolve\.outputs\.version \}\}-\$\{\{ needs\.resolve\.outputs\.source_sha \}\}[\s\S]*path:\s*artifacts/s.test(releaseUpgrade), "Release upgrade job must download the single exact build candidate artifact.");
expect(/docker load --input artifacts\/oci\/cmsify-api\.oci\.tar/.test(releaseUpgrade), "Release upgrade job must load the exact built OCI archive.");
expect(/verify-release-baseline --fixture tests\/upgrade\/fixtures\/v0\.1\.3 --candidate-version "\$VERSION" --github-token-env GITHUB_TOKEN/.test(releaseUpgrade), "Release upgrade job must enforce the moving baseline before rehearsal.");
expect(/CANDIDATE_IMAGE:\s*syntaxcircus\/cmsify-api:\$\{\{ needs\.resolve\.outputs\.version \}\}/.test(releaseUpgrade), "Release upgrade job must bind CANDIDATE_IMAGE to the exact versioned image loaded from the OCI archive.");
expect(/cli\.mjs rehearse[^\n]*--candidate-image "\$CANDIDATE_IMAGE"[^\n]*--candidate-version "\$VERSION"[^\n]*--candidate-source-sha "\$SOURCE_SHA"/.test(releaseUpgrade), "Release upgrade rehearsal must use the exact loaded candidate through $CANDIDATE_IMAGE.");
expect(/FIXTURE_MANIFEST="tests\/upgrade\/fixtures\/v0\.1\.3\/manifest\.json"/.test(releaseUpgrade), "Release upgrade prerequisite images must be derived from the verified fixture manifest.");
for (const [variable, manifestPath, description] of [
  ["BASELINE_API_IMAGE", ".baseline.apiImage", "baseline API"],
  ["POSTGRES_IMAGE", ".baseline.postgresImage", "PostgreSQL"],
  ["MINIO_IMAGE", ".baseline.minioImage", "MinIO"],
]) {
  const binding = releaseUpgrade.split(/\r?\n/).find((line) => line.includes(`${variable}=`));
  expect(binding?.includes(manifestPath) && binding.includes(".repository") && binding.includes(".digest") && binding.includes("$FIXTURE_MANIFEST"), `Release upgrade must use a manifest-derived ${description} digest reference.`);
  const exactPull = `docker pull --platform linux/amd64 "$${variable}"`;
  expect(releaseUpgrade.split(exactPull).length === 2, `Release upgrade must pull the exact ${description} image once with explicit linux/amd64.`);
}
const releaseLoad = releaseUpgrade.indexOf("docker load --input artifacts/oci/cmsify-api.oci.tar");
const releaseBaseline = releaseUpgrade.indexOf("verify-release-baseline");
const releaseFixture = releaseUpgrade.indexOf("verify-fixture");
const releaseBaselinePull = releaseUpgrade.indexOf('docker pull --platform linux/amd64 "$BASELINE_API_IMAGE"');
const releasePostgresPull = releaseUpgrade.indexOf('docker pull --platform linux/amd64 "$POSTGRES_IMAGE"');
const releaseMinioPull = releaseUpgrade.indexOf('docker pull --platform linux/amd64 "$MINIO_IMAGE"');
const releaseRehearsal = releaseUpgrade.indexOf("cli.mjs rehearse");
expect(releaseBaseline >= 0 && releaseFixture > releaseBaseline && releaseBaselinePull > releaseFixture && releasePostgresPull > releaseBaselinePull && releaseMinioPull > releasePostgresPull && releaseLoad > releaseMinioPull && releaseRehearsal > releaseLoad, "Release upgrade job must verify the moving baseline and fixture, pull every exact linux/amd64 prerequisite, load the candidate archive, then rehearse that image.");
const loadedCandidateWindow = releaseLoad >= 0 && releaseRehearsal > releaseLoad ? releaseUpgrade.slice(releaseLoad, releaseRehearsal) : "";
expect(!/^\s*docker\s+(?:image\s+)?pull\b/im.test(loadedCandidateWindow), "Release upgrade job must not pull between exact archive load and rehearsal.");
expect(!/^\s*docker\s+(?:image\s+)?tag\b/im.test(loadedCandidateWindow), "Release upgrade job must not re-tag between exact archive load and rehearsal.");
expect(!/\b(docker buildx build|docker build|dotnet pack|npm pack)\b/i.test(releaseUpgrade), "Release upgrade job must not rebuild the candidate.");
expect(jobConditionRequiresSuccess(releaseUpgrade), "The upgrade-rollback job condition must require successful dependencies so the release gate fails closed.");
expect(continueOnErrorIsDisabled(releaseUpgrade), "The upgrade-rollback job and steps must not enable continue-on-error; the gate must fail closed.");
const releaseUpgradeStepConditions = stepConditions(releaseUpgrade);
expect(releaseUpgradeStepConditions.filter((condition) => condition === "failure()").length === 1 && releaseUpgradeStepConditions.every((condition) => condition === "success()" || condition === "failure()"), "Upgrade-rollback step conditions must be limited to normal success or the one failure diagnostics upload.");
expect(/if:\s*failure\(\)[\s\S]*actions\/upload-artifact@[0-9a-f]{40}[\s\S]*path:\s*artifacts\/upgrade-tests\/\*\*/s.test(releaseUpgrade), "Release upgrade job must upload sanitized diagnostics on failure.");

const certification = jobBody("certify");
expect(/needs:\s*\[[^\]]*upgrade-rollback[^\]]*\]/.test(certification), "The certify job must depend on upgrade-rollback.");
expect(jobConditionRequiresSuccess(certification), "The certify job condition must require success of the upgrade gate.");
expect(continueOnErrorIsDisabled(certification), "The certify job and steps must not enable continue-on-error; certification must fail closed.");
expect(stepConditions(certification).every((condition) => condition === "success()"), "Certify step conditions must require normal success after the upgrade gate.");
const promotion = jobBody("promote");
expect(/needs:\s*\[[^\]]*certify[^\]]*\]/.test(promotion), "Promotion must depend on certify so the upgrade gate cannot be bypassed.");
expect(jobConditionRequiresSuccess(promotion), "The promotion job condition must require success of certify.");
expect(continueOnErrorIsDisabled(promotion), "The promotion job and steps must not enable continue-on-error; publication must fail closed.");
expect(stepConditions(promotion).every((condition) => condition === "success()"), "Promotion step conditions must require normal success after certify.");
expect(!/\b(dotnet pack|npm pack|npm run build|docker buildx build|docker build)\b/i.test(promotion), "Promotion must not rebuild mutable artifacts.");
expect(!/--skip-duplicate|NUGET_API_KEY\s*:\s*\$\{\{\s*secrets\./i.test(promotion), "NuGet promotion must use the short-lived OIDC key and reject pre-existing package versions.");
expect(/id-token:\s*write[\s\S]*registry-url:\s*https:\/\/registry\.npmjs\.org[\s\S]*npm@11\.11\.0[\s\S]*--provenance[\s\S]*--tag "\$NPM_CHANNEL"/s.test(promotion), "npm trusted publishing must have OIDC, registry configuration, supported npm, provenance, and a prerelease-safe tag.");
expect(/--prerelease/.test(promotion), "GitHub Release promotion must mark SemVer prereleases as prereleases.");
expect(!/docker push/i.test(promotion) && /oras cp --from-oci-layout-path[\s\S]*oras manifest fetch --descriptor[\s\S]*test "\$API_REMOTE" = "\$API_EXPECTED"/s.test(promotion), "OCI promotion must copy certified descriptors and compare remote digests without mutable docker push.");
expect(/refs\/tags\/\$GITHUB_REF_NAME\^\{\}[\s\S]*refs\/tags\/\$GITHUB_REF_NAME[\s\S]*REMOTE_SHA/s.test(promotion), "Promotion must peel annotated tags and safely fall back to lightweight tags.");
expect(/case "\$http_code" in 404\) ;; 200\) exit 1 ;; \*\)/s.test(promotion) && !/case "\$http_code" in[^\n]*404\|200/.test(promotion), "NuGet preflight must accept only explicit HTTP 404 absence and fail closed for all other responses.");
expect(/oras manifest fetch --descriptor --oci-layout-path\s+\S+\s+"syntaxcircus\/cmsify-api:\$VERSION"/s.test(promotion) && /oras cp --from-oci-layout-path\s+\S+\s+"syntaxcircus\/cmsify-api:\$VERSION"\s+"docker\.io\/syntaxcircus\/cmsify-api:\$VERSION"/s.test(promotion), "Promotion must use exact ORAS 1.3 local OCI-layout path syntax before descriptor-preserving copy.");
expect(!/oras manifest fetch[^\n]*--oci-layout(?:\s|=)[^\n]*--oci-layout-path|oras manifest fetch[^\n]*--oci-layout-path[^\n]*--oci-layout(?:\s|=)/.test(promotion), "ORAS manifest fetch must reject combined --oci-layout and --oci-layout-path syntax.");
expect(!/oras cp[^\n]*--from-oci-layout(?:\s|=)[^\n]*--from-oci-layout-path|oras cp[^\n]*--from-oci-layout-path[^\n]*--from-oci-layout(?:\s|=)/.test(promotion), "ORAS cp must reject combined --from-oci-layout and --from-oci-layout-path syntax.");
expect(/auth\.docker\.io\/token\?service=registry\.docker\.io&scope=repository:\$image:pull,push[\s\S]*jq -er \.token[\s\S]*Authorization: Bearer \$bearer/s.test(promotion), "Docker Hub preflight must obtain and use a promotion-credential scoped Bearer token.");
for (const mediaType of ["application/vnd.oci.image.manifest.v1+json", "application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.v2+json", "application/vnd.docker.distribution.manifest.list.v2+json"]) {
  expect(promotion.includes(mediaType), "Docker Hub preflight Accept header must include all four manifest media types.");
}
expect(/status=.*curl[\s\S]*case "\$status" in 404\) ;; \*\)/s.test(promotion) && !/case "\$status" in[^\n]*(?:200|401|429|5\d\d)[^\n]*\) ;;/s.test(promotion), "Docker Hub manifest absence preflight must accept only HTTP 404.");
expect(/registry\.npmjs\.org\/@cmsify%2Fclient\/\$VERSION[\s\S]*case "\$npm_status" in 404\) ;; \*\)/s.test(promotion), "npm exact-version preflight must accept only explicit HTTP 404 absence.");
const ociEquality = promotion.indexOf('test "$API_REMOTE" = "$API_EXPECTED"');
const nugetPublish = promotion.indexOf("dotnet nuget push");
const npmPublish = promotion.indexOf("npm publish");
expect(ociEquality >= 0 && nugetPublish > ociEquality && npmPublish > ociEquality, "OCI remote digest equality must complete before irreversible NuGet and npm publication.");
expect(/sudo install|GITHUB_PATH/.test(promotion), "Pinned ORAS installation must use a verified writable tool path.");

const branchWorkflow = file(".github/workflows/dotnet-test.yml");
expect(/pull_request:/i.test(branchWorkflow) && /verify-release-contract\.mjs/i.test(branchWorkflow) && /tests\/release-contract/i.test(branchWorkflow), "Branch/PR validation must execute the release-contract verifier and tests.");
expect(/node --test tests\/upgrade\/unit\/\*\.test\.mjs tests\/release-contract\/\*\.test\.mjs/.test(branchWorkflow), "Branch validation must execute fast upgrade unit tests and all release-contract tests.");
expect(/node eng\/upgrade-tests\/cli\.mjs verify-fixture --fixture tests\/upgrade\/fixtures\/v0\.1\.3/.test(branchWorkflow), "Branch validation must verify the checked-in upgrade fixture.");
expect(!/generate-fixture/.test(branchWorkflow), "Branch validation must not regenerate the Docker-backed fixture.");

errors.push(...validateRepositorySupplyChain(repositoryRoot));

if (errors.length > 0) {
  process.stderr.write(`${errors.join("\n")}\n`);
  process.exitCode = 1;
} else {
  process.stdout.write(`Release contract verified for ${repositoryRoot}.\n`);
}
