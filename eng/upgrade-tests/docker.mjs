import { mkdir, rm, writeFile } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";

import { assertTrustedRunScope, OWNERSHIP_LABELS } from "./paths.mjs";
import { ProcessFailure, runProcess } from "./process.mjs";

const COMPOSE_FILE = "tests/upgrade/compose.yml";
const SERVICE = /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/;
const MAX_DOCKER_LOG_TIMEOUT_MS = 30_000;
const MAX_DOCKER_TIMEOUT_MS = 120_000;

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function assertService(service) {
  assert(typeof service === "string" && SERVICE.test(service), "Docker service names must be canonical identifiers.");
}

function assertStringArray(values, name) {
  assert(Array.isArray(values) && values.every((value) => typeof value === "string"), `${name} must be an array of strings.`);
}

function lines(stdout) {
  return stdout.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
}

function labelsFor(scope) {
  return [
    `label=${OWNERSHIP_LABELS.upgradeTest}=true`,
    `label=${OWNERSHIP_LABELS.upgradeRun}=${scope.runId}`,
  ];
}

function resourceWasAlreadyRemoved(error) {
  return error instanceof ProcessFailure && /\b(no such (container|network|volume|object)|not found)\b/i.test(error.message);
}

function imageReference(image) {
  assert(image && typeof image === "object", "An immutable image is required.");
  assert(typeof image.repository === "string" && typeof image.digest === "string" && typeof image.platform === "string", "An immutable image must contain repository, digest, and platform.");
  return `${image.repository}@${image.digest}`;
}

function canonicalDockerHubReference(reference) {
  const [repository, digest] = reference.split("@", 2);
  if (!digest) return reference;
  let canonicalRepository = repository.startsWith("docker.io/") ? repository.slice("docker.io/".length) : repository;
  if (canonicalRepository.startsWith("library/")) canonicalRepository = canonicalRepository.slice("library/".length);
  return `${canonicalRepository}@${digest}`;
}

function isContainedBy(parent, candidate) {
  const pathFromParent = relative(parent, candidate);
  return pathFromParent === "" || (!pathFromParent.startsWith(`..${sep}`) && pathFromParent !== ".." && !isAbsolute(pathFromParent));
}

/**
 * @typedef {import("./paths.mjs").RunScope} RunScope
 */

/**
 * Creates a label-scoped Docker Compose harness for one upgrade run.
 * @param {RunScope} scope
 * @param {typeof runProcess} [executor]
 */
export function createDockerHarness(scope, executor = runProcess) {
  assertTrustedRunScope(scope);
  assert(typeof executor === "function", "A process executor is required.");

  const environmentDirectory = resolve(scope.repositoryRoot, "tests", "upgrade", ".runs");
  const environmentFile = resolve(environmentDirectory, `${scope.runId}.env`);
  const composePrefix = [
    "compose",
    "--project-name", scope.projectName,
    "--file", COMPOSE_FILE,
    "--env-file", environmentFile,
  ];

  function assertSafeEnvironmentFile() {
    assertTrustedRunScope(scope);
    const ownedDirectory = resolve(scope.repositoryRoot, "tests", "upgrade", ".runs");
    const ownedFile = resolve(ownedDirectory, `${scope.runId}.env`);
    assert(environmentDirectory === ownedDirectory && environmentFile === ownedFile && isContainedBy(ownedDirectory, ownedFile), "A trusted safe run scope has an unowned environment file path.");
  }

  async function ensureRunFiles() {
    assertSafeEnvironmentFile();
    await Promise.all([
      mkdir(scope.diagnosticsDirectory, { recursive: true }),
      mkdir(environmentDirectory, { recursive: true }),
    ]);
    try {
      await writeFile(environmentFile, [
        `CMSIFY_UPGRADE_RUN_ID=${scope.runId}`,
        "CMSIFY_UPGRADE_TEST_LABEL=true",
        "",
      ].join("\n"), { encoding: "utf8", mode: 0o600, flag: "wx" });
    } catch (error) {
      if (error?.code !== "EEXIST") throw error;
    }
  }

  async function execute(command, args, phase, timeoutMs = MAX_DOCKER_TIMEOUT_MS) {
    return executor(command, args, {
      cwd: scope.repositoryRoot,
      timeoutMs,
      phase,
    });
  }

  async function compose(args, phase, timeoutMs) {
    await ensureRunFiles();
    return execute("docker", [...composePrefix, ...args], phase, timeoutMs);
  }

  async function inspectLabels(resourceType, resourceId) {
    const inspectArgs = resourceType === "container"
      ? ["container", "inspect", "--format", "{{json .Config.Labels}}", resourceId]
      : [resourceType, "inspect", "--format", "{{json .Labels}}", resourceId];
    try {
      const result = await execute("docker", inspectArgs, "docker-cleanup-inspect");
      const parsed = JSON.parse(result.stdout.trim());
      assert(parsed && typeof parsed === "object" && !Array.isArray(parsed), `Discovered Docker resource ${resourceId} did not return labels.`);
      assert(parsed[OWNERSHIP_LABELS.upgradeTest] === "true" && parsed[OWNERSHIP_LABELS.upgradeRun] === scope.runId, `Discovered Docker resource ${resourceId} lacks the required ownership labels.`);
      return true;
    } catch (error) {
      if (resourceWasAlreadyRemoved(error)) return false;
      throw error;
    }
  }

  async function discover(commandArgs, phase) {
    const filters = labelsFor(scope);
    const result = await execute("docker", [...commandArgs, "--filter", filters[0], "--filter", filters[1]], phase);
    return lines(result.stdout);
  }

  async function removeResources(ids, resourceType, removeArgs, phase) {
    for (const resourceId of ids) {
      if (!await inspectLabels(resourceType, resourceId)) continue;
      try {
        await execute("docker", [...removeArgs, resourceId], phase);
      } catch (error) {
        if (!resourceWasAlreadyRemoved(error)) throw error;
      }
    }
  }

  async function logs() {
    const result = await compose(["logs", "--no-color"], "docker-compose-logs", MAX_DOCKER_LOG_TIMEOUT_MS);
    await mkdir(scope.diagnosticsDirectory, { recursive: true });
    await writeFile(resolve(scope.diagnosticsDirectory, "docker-compose.log"), `${result.stdout}${result.stderr}`, "utf8");
    return result;
  }

  return Object.freeze({
    environmentFile,

    async up(services) {
      assertStringArray(services, "Docker services");
      services.forEach(assertService);
      return compose(["up", "--detach", ...services], "docker-compose-up");
    },

    async stop(service) {
      assertService(service);
      return compose(["stop", service], "docker-compose-stop");
    },

    async start(service) {
      assertService(service);
      return compose(["start", service], "docker-compose-start");
    },

    async exec(service, args) {
      assertService(service);
      assertStringArray(args, "Docker exec arguments");
      return compose(["exec", "--no-TTY", service, ...args], "docker-compose-exec");
    },

    logs,

    async inspectImage(image) {
      const reference = imageReference(image);
      const result = await execute("docker", ["image", "inspect", "--format", "{{json .}}", reference], "docker-image-inspect");
      let inspected;
      try {
        inspected = JSON.parse(result.stdout.trim());
      } catch {
        throw new Error(`Docker image inspection did not return JSON for ${image.repository}.`);
      }
      assert(inspected?.Os === "linux" && inspected?.Architecture === "amd64", `Docker image ${image.repository} is not linux/amd64.`);
      const canonicalReference = canonicalDockerHubReference(reference);
      assert(Array.isArray(inspected.RepoDigests) && inspected.RepoDigests.some((value) => canonicalDockerHubReference(value) === canonicalReference), `Docker image ${image.repository} does not contain the required repository digest.`);
      return inspected;
    },

    async copyFrom(service, source, destination) {
      assertService(service);
      assert(typeof source === "string" && source.length > 0 && typeof destination === "string" && destination.length > 0, "Docker copy paths must be non-empty strings.");
      return compose(["cp", `${service}:${source}`, destination], "docker-compose-copy-from");
    },

    async copyTo(service, source, destination) {
      assertService(service);
      assert(typeof source === "string" && source.length > 0 && typeof destination === "string" && destination.length > 0, "Docker copy paths must be non-empty strings.");
      return compose(["cp", source, `${service}:${destination}`], "docker-compose-copy-to");
    },

    async cleanup() {
      const failures = [];
      const collectFailure = async (operation) => {
        try {
          return await operation();
        } catch (error) {
          failures.push(error);
          return undefined;
        }
      };
      try {
        await collectFailure(logs);
        const [containers = [], networks = [], volumes = []] = await Promise.all([
          collectFailure(() => discover(["ps", "--all", "--quiet"], "docker-cleanup-discover-containers")),
          collectFailure(() => discover(["network", "ls", "--quiet"], "docker-cleanup-discover-networks")),
          collectFailure(() => discover(["volume", "ls", "--quiet"], "docker-cleanup-discover-volumes")),
        ]);
        await collectFailure(() => removeResources(containers, "container", ["rm", "--force"], "docker-cleanup-remove-container"));
        await collectFailure(() => removeResources(networks, "network", ["network", "rm"], "docker-cleanup-remove-network"));
        await collectFailure(() => removeResources(volumes, "volume", ["volume", "rm"], "docker-cleanup-remove-volume"));
      } finally {
        await collectFailure(async () => {
          assertSafeEnvironmentFile();
          await rm(environmentFile, { force: true });
        });
      }
      if (failures.length === 1) throw failures[0];
      if (failures.length > 1) {
        throw new AggregateError(failures, "Docker cleanup failed.");
      }
    },
  });
}
