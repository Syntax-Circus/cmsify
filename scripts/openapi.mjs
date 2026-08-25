import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sdkRoot = resolve(repositoryRoot, "sdk/typescript");
const apiProject = resolve(repositoryRoot, "src/Cmsify.Api/Cmsify.Api.csproj");
const apiAssembly = resolve(repositoryRoot, "src/Cmsify.Api/bin/Release/net10.0/Cmsify.Api.dll");
const generator = resolve(sdkRoot, "scripts/generate.mjs");
const generatedFiles = ["schema.ts", "client.ts"];

function options(argumentsList) {
  const result = new Map();
  for (let index = 0; index < argumentsList.length; index += 1) {
    if (argumentsList[index].startsWith("--")) {
      result.set(argumentsList[index], argumentsList[index + 1]);
    }
  }
  return result;
}

function run(command, argumentsList, environment = {}) {
  execFileSync(command, argumentsList, {
    cwd: repositoryRoot,
    stdio: "inherit",
    env: { ...process.env, ...environment },
  });
}

function normalizedFile(path) {
  return readFileSync(path, "utf8").replace(/\r\n/g, "\n");
}

function exportLiveDocument(output) {
  mkdirSync(dirname(output), { recursive: true });
  run("dotnet", ["build", apiProject, "--configuration", "Release", "--nologo"]);
  run("dotnet", ["tool", "restore"]);
  run("dotnet", ["tool", "run", "swagger", "tofile", "--output", output, apiAssembly, "v1"], {
    ASPNETCORE_ENVIRONMENT: "Production",
    ASPNETCORE_CONTENTROOT: dirname(apiProject),
    Api__OpenApiExport: "true",
    TrustedProxy__RequireTrustedProxiesInProduction: "false",
  });
}

function generate(input, outputDirectory) {
  run(process.execPath, [generator, "--input", input, "--output-dir", outputDirectory]);
}

function compareGenerated(expectedDirectory, actualDirectory) {
  return generatedFiles.some(file =>
    !existsSync(resolve(expectedDirectory, file))
    || normalizedFile(resolve(expectedDirectory, file)) !== normalizedFile(resolve(actualDirectory, file)));
}

function resolveLiveDocument(commandOptions, temporaryDirectory) {
  const suppliedDocument = commandOptions.get("--live-document");
  if (suppliedDocument) {
    return resolve(suppliedDocument);
  }

  const output = resolve(temporaryDirectory, "live-openapi.json");
  exportLiveDocument(output);
  return output;
}

function check(commandOptions) {
  const temporaryDirectory = mkdtempSync(resolve(tmpdir(), "cmsify-openapi-check-"));
  try {
    const snapshot = resolve(commandOptions.get("--snapshot") ?? resolve(sdkRoot, "openapi.snapshot.json"));
    const trackedGenerated = resolve(commandOptions.get("--generated-dir") ?? resolve(sdkRoot, "src/generated"));
    const liveDocument = resolveLiveDocument(commandOptions, temporaryDirectory);
    const generatedDirectory = resolve(temporaryDirectory, "generated");
    const failures = [];

    if (normalizedFile(liveDocument) !== normalizedFile(snapshot)) {
      failures.push("Live OpenAPI differs from the checked-in snapshot. Run `node scripts/openapi.mjs update`.");
    }

    generate(liveDocument, generatedDirectory);
    if (compareGenerated(generatedDirectory, trackedGenerated)) {
      failures.push("Generated TypeScript output differs from tracked output. Run `node scripts/openapi.mjs update`.");
    }

    if (failures.length > 0) {
      throw new Error(failures.join("\n"));
    }
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
}

function update(commandOptions) {
  const temporaryDirectory = mkdtempSync(resolve(tmpdir(), "cmsify-openapi-update-"));
  try {
    const trackedSnapshot = resolve(sdkRoot, "openapi.snapshot.json");
    const trackedGeneratedDirectory = resolve(sdkRoot, "src/generated");
    const snapshotOption = commandOptions.get("--snapshot");
    const generatedDirectoryOption = commandOptions.get("--generated-dir");
    const snapshot = resolve(snapshotOption ?? trackedSnapshot);
    const generatedDirectory = resolve(generatedDirectoryOption ?? trackedGeneratedDirectory);
    if (commandOptions.has("--live-document")
      && (!snapshotOption
        || !generatedDirectoryOption
        || snapshot === trackedSnapshot
        || generatedDirectory === trackedGeneratedDirectory)) {
      throw new Error("--live-document is only allowed when both --snapshot and --generated-dir target test fixtures.");
    }
    const liveDocument = resolveLiveDocument(commandOptions, temporaryDirectory);
    const stagedGeneratedDirectory = resolve(temporaryDirectory, "generated");
    generate(liveDocument, stagedGeneratedDirectory);

    mkdirSync(generatedDirectory, { recursive: true });
    for (const file of generatedFiles) {
      copyFileSync(resolve(stagedGeneratedDirectory, file), resolve(generatedDirectory, file));
    }
    copyFileSync(liveDocument, snapshot);
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true });
  }
}

const [command, ...argumentList] = process.argv.slice(2);
const commandOptions = options(argumentList);
if (command === "export") {
  const output = commandOptions.get("--output");
  if (!output) {
    throw new Error("Usage: node scripts/openapi.mjs export --output <openapi.json>");
  }
  exportLiveDocument(resolve(output));
} else if (command === "check") {
  check(commandOptions);
} else if (command === "update") {
  update(commandOptions);
} else {
  throw new Error("Usage: node scripts/openapi.mjs <export|check|update>");
}
