import { lstat, rm } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";

import { assertTrustedRunScope, OWNERSHIP_LABELS } from "./paths.mjs";
import { ProcessFailure, runProcess } from "./process.mjs";
import { assertPhysicalPath, ensureSafeDirectory, writeSafeAtomically } from "./safe-files.mjs";

const COMPOSE_FILE = "tests/upgrade/compose.yml";
const SERVICE = /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/;
const MAX_DOCKER_LOG_TIMEOUT_MS = 30_000;
const MAX_DOCKER_TIMEOUT_MS = 120_000;
const IMAGE_ID = /^sha256:[0-9a-f]{64}$/;
const SOURCE_SHA = /^[0-9a-f]{40}$/;
const ENVIRONMENT_NAME = /^[A-Z][A-Z0-9_]*$/;
const DIAGNOSTIC_SERVICES = Object.freeze(["postgres", "minio", "baseline-api", "candidate-api"]);
const MIGRATION_ID = /^\d{14}_[A-Za-z0-9_]{1,96}$/;
const MAX_DIAGNOSTIC_LINES = 32;
const MAX_DIAGNOSTIC_LINE_LENGTH = 160;

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function assertService(service) {
  assert(typeof service === "string" && SERVICE.test(service), "Docker service names must be canonical identifiers.");
}

function assertStringArray(values, name) {
  assert(Array.isArray(values) && values.every((value) => typeof value === "string"), `${name} must be an array of strings.`);
}

function assertLifecycleOptions(options) {
  assert(options && typeof options === "object" && !Array.isArray(options), "Docker lifecycle options must be an object.");
  assert(options.signal === undefined || options.signal instanceof AbortSignal, "Docker lifecycle signal must be an AbortSignal.");
  assert(options.redact === undefined || (Array.isArray(options.redact) && options.redact.every((value) => typeof value === "string")), "Docker lifecycle redactions must be strings.");
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

function diagnosticMarker(service, line) {
  if (service === "postgres") {
    if (/database system is ready to accept connections/i.test(line)) return "database system is ready to accept connections";
    if (/database system is shut down/i.test(line)) return "database system is shut down";
    if (/\bFATAL\b/i.test(line)) return "PostgreSQL fatal error observed.";
    return undefined;
  }
  if (service === "minio") {
    if (/\bAPI:\s*https?:\/\//i.test(line)) return "MinIO API endpoint announced.";
    if (/\bStatus:/i.test(line)) return "MinIO status marker observed.";
    if (/\b(?:ERROR|FATAL)\b/i.test(line)) return "MinIO error observed.";
    return undefined;
  }
  const migration = line.match(/Applying migration\s+['\"]?(\d{14}_[A-Za-z0-9_]{1,96})['\"]?/i)?.[1];
  if (migration && MIGRATION_ID.test(migration)) return `Applying migration ${migration}.`;
  if (/Application started\.?/i.test(line)) return "Application started.";
  if (/Now listening on:/i.test(line)) return "Now listening on HTTP.";
  const readiness = line.match(/\bGET\s+\S*\/health\/ready\S*[^\r\n]*?\b([1-5]\d\d)\b/i);
  if (readiness) return `GET /health/ready -> ${readiness[1]}.`;
  if (/Unhandled exception/i.test(line)) return "Unhandled exception observed.";
  if (/Application startup exception/i.test(line)) return "Application startup exception observed.";
  return undefined;
}

function allowListedServiceLogs(raw) {
  const services = Object.fromEntries(DIAGNOSTIC_SERVICES.map((service) => [service, { status: "captured", lines: [] }]));
  for (const rawLine of raw.split(/\r?\n/)) {
    const separator = rawLine.indexOf("|");
    if (separator < 0) continue;
    const prefix = rawLine.slice(0, separator).toLowerCase();
    const service = DIAGNOSTIC_SERVICES.find((candidate) => prefix.includes(candidate));
    if (!service || services[service].lines.length >= MAX_DIAGNOSTIC_LINES) continue;
    const marker = diagnosticMarker(service, rawLine.slice(separator + 1));
    if (!marker) continue;
    const bounded = marker.slice(0, MAX_DIAGNOSTIC_LINE_LENGTH);
    if (!services[service].lines.includes(bounded)) services[service].lines.push(bounded);
  }
  return services;
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
    await ensureSafeDirectory(scope.repositoryRoot, scope.diagnosticsDirectory);
    await ensureSafeDirectory(scope.repositoryRoot, environmentDirectory);
    try {
      await lstat(environmentFile);
      await assertPhysicalPath(scope.repositoryRoot, environmentFile, { leaf: "file" });
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
      await writeSafeAtomically(scope.repositoryRoot, environmentFile, [
        `CMSIFY_UPGRADE_RUN_ID=${scope.runId}`,
        "CMSIFY_UPGRADE_TEST_LABEL=true",
        "",
      ].join("\n"), { encoding: "utf8", mode: 0o600 });
    }
  }

  async function writeEnvironment(values) {
    assert(values && typeof values === "object" && !Array.isArray(values), "Docker run environment values must be an object.");
    const reserved = new Set(["CMSIFY_UPGRADE_RUN_ID", "CMSIFY_UPGRADE_TEST_LABEL"]);
    const entries = Object.entries(values);
    for (const [name, value] of entries) {
      assert(ENVIRONMENT_NAME.test(name) && !reserved.has(name), "Docker run environment names must be canonical and must not replace ownership values.");
      assert(typeof value === "string" && !/[\r\n\0]/.test(value), `Docker run environment value ${name} must be a single-line string.`);
    }
    assertSafeEnvironmentFile();
    await ensureSafeDirectory(scope.repositoryRoot, scope.diagnosticsDirectory);
    await ensureSafeDirectory(scope.repositoryRoot, environmentDirectory);
    const linesToWrite = [
      `CMSIFY_UPGRADE_RUN_ID=${scope.runId}`,
      "CMSIFY_UPGRADE_TEST_LABEL=true",
      ...entries.map(([name, value]) => `${name}=${value}`),
      "",
    ];
    await writeSafeAtomically(scope.repositoryRoot, environmentFile, linesToWrite.join("\n"), { encoding: "utf8", mode: 0o600 });
  }

  async function execute(command, args, phase, timeoutMs = MAX_DOCKER_TIMEOUT_MS, options = {}) {
    return executor(command, args, {
      cwd: scope.repositoryRoot,
      timeoutMs: options.timeoutMs ?? timeoutMs,
      phase,
      ...(options.signal ? { signal: options.signal } : {}),
      ...(options.redact ? { redact: options.redact } : {}),
      ...(options.stdoutEncoding ? { stdoutEncoding: options.stdoutEncoding } : {}),
      ...(options.stdin !== undefined ? { stdin: options.stdin } : {}),
    });
  }

  async function compose(args, phase, timeoutMs, options) {
    await ensureRunFiles();
    return execute("docker", [...composePrefix, ...args], phase, timeoutMs, options);
  }

  async function inspectDockerImage(reference, phase, options = {}) {
    const result = await execute("docker", ["image", "inspect", "--format", "{{json .}}", reference], phase, undefined, options);
    try {
      return JSON.parse(result.stdout.trim());
    } catch {
      throw new Error(`Docker image inspection did not return JSON for ${reference}.`);
    }
  }

  async function inspectLabels(resourceType, resourceId, options = {}) {
    const inspectArgs = resourceType === "container"
      ? ["container", "inspect", "--format", "{{json .Config.Labels}}", resourceId]
      : [resourceType, "inspect", "--format", "{{json .Labels}}", resourceId];
    try {
      const result = await execute("docker", inspectArgs, "docker-cleanup-inspect", undefined, options);
      const parsed = JSON.parse(result.stdout.trim());
      assert(parsed && typeof parsed === "object" && !Array.isArray(parsed), `Discovered Docker resource ${resourceId} did not return labels.`);
      assert(parsed[OWNERSHIP_LABELS.upgradeTest] === "true" && parsed[OWNERSHIP_LABELS.upgradeRun] === scope.runId, `Discovered Docker resource ${resourceId} lacks the required ownership labels.`);
      return true;
    } catch (error) {
      if (resourceWasAlreadyRemoved(error)) return false;
      throw error;
    }
  }

  async function inspectOwnedResource(resourceType, resourceId, options = {}) {
    const inspectArgs = resourceType === "container"
      ? ["container", "inspect", "--format", "{{json .}}", resourceId]
      : ["volume", "inspect", "--format", "{{json .}}", resourceId];
    try {
      const result = await execute("docker", inspectArgs, "docker-discard-state-inspect", undefined, options);
      const inspected = JSON.parse(result.stdout.trim());
      assert(inspected && typeof inspected === "object" && !Array.isArray(inspected), `Owned Docker ${resourceType} ${resourceId} did not return identity metadata.`);
      const labels = resourceType === "container" ? inspected.Config?.Labels : inspected.Labels;
      assert(labels && typeof labels === "object" && !Array.isArray(labels), `Owned Docker ${resourceType} ${resourceId} did not return labels.`);
      assert(labels[OWNERSHIP_LABELS.upgradeTest] === "true" && labels[OWNERSHIP_LABELS.upgradeRun] === scope.runId, `Owned Docker ${resourceType} ${resourceId} lacks the required ownership labels.`);
      if (resourceType === "container") {
        assert(typeof inspected.Id === "string" && inspected.Id.length > 0, `Owned Docker container ${resourceId} did not return a stable identity.`);
        return Object.freeze({ id: resourceId, identity: inspected.Id });
      }
      assert(inspected.Name === resourceId, `Owned Docker volume ${resourceId} returned a different name.`);
      assert(typeof inspected.CreatedAt === "string" && inspected.CreatedAt.length > 0, `Owned Docker volume ${resourceId} did not return creation identity.`);
      return Object.freeze({
        id: resourceId,
        identity: JSON.stringify([
          inspected.Name,
          inspected.Driver ?? null,
          inspected.Mountpoint ?? null,
          inspected.CreatedAt,
          inspected.Scope ?? null,
          inspected.Options ?? null,
        ]),
      });
    } catch (error) {
      if (resourceWasAlreadyRemoved(error)) return undefined;
      throw error;
    }
  }

  async function discover(commandArgs, phase, options = {}) {
    const filters = labelsFor(scope);
    const result = await execute("docker", [...commandArgs, "--filter", filters[0], "--filter", filters[1]], phase, undefined, options);
    return lines(result.stdout);
  }

  async function removeResources(ids, resourceType, removeArgs, phase, options = {}) {
    for (const resourceId of ids) {
      if (!await inspectLabels(resourceType, resourceId, options)) continue;
      try {
        await execute("docker", [...removeArgs, resourceId], phase, undefined, options);
      } catch (error) {
        if (!resourceWasAlreadyRemoved(error)) throw error;
      }
    }
  }

  async function dataResourceSnapshot(options) {
    const containerResult = await compose(["ps", "--all", "--quiet", "postgres", "minio"], "docker-discard-state-discover-containers", undefined, options);
    const containers = lines(containerResult.stdout);
    const volumes = [`${scope.projectName}_postgres-data`, `${scope.projectName}_minio-data`];
    const discoveredVolumes = (await discover(["volume", "ls", "--quiet"], "docker-discard-state-discover-volumes", options)).sort();
    const expectedVolumes = [...volumes].sort();
    assert(containers.length === 2 && new Set(containers).size === 2, "Owned data containers are missing or changed at the discard fence.");
    assert(discoveredVolumes.length === expectedVolumes.length && discoveredVolumes.every((volume, index) => volume === expectedVolumes[index]), "Owned data volumes are missing or changed at the discard fence.");
    const inspected = [];
    for (const container of containers) {
      const resource = await inspectOwnedResource("container", container, options);
      assert(resource, "An owned data container disappeared at the discard fence.");
      inspected.push(["container", resource]);
    }
    for (const volume of volumes) {
      const resource = await inspectOwnedResource("volume", volume, options);
      assert(resource, "An owned data volume disappeared at the discard fence.");
      inspected.push(["volume", resource]);
    }
    return Object.freeze({ containers: Object.freeze(containers), volumes: Object.freeze(volumes), inspected: Object.freeze(inspected) });
  }

  function sameDataResourceSnapshot(before, after) {
    return before.inspected.length === after.inspected.length
      && before.inspected.every(([type, resource]) => {
        const match = after.inspected.find(([nextType, next]) => type === nextType && resource.id === next.id);
        return match?.[1].identity === resource.identity;
      });
  }

  async function logs(options = {}) {
    assertLifecycleOptions(options);
    assert(options.resourcesStarted === undefined || typeof options.resourcesStarted === "boolean", "Docker diagnostic resource state must be boolean.");
    const result = options.resourcesStarted === false
      ? { stdout: "", stderr: "" }
      : await compose(["logs", "--no-color", "--tail", "200"], "docker-compose-logs", MAX_DOCKER_LOG_TIMEOUT_MS, options);
    const services = options.resourcesStarted === false
      ? Object.fromEntries(DIAGNOSTIC_SERVICES.map((service) => [service, { status: "unavailable", lines: [] }]))
      : allowListedServiceLogs(`${result.stdout}\n${result.stderr}`);
    let migrations = { status: "unavailable", count: 0, ids: [] };
    if (options.resourcesStarted !== false) {
      try {
        const migrationResult = await compose([
          "exec", "--interactive", "--no-TTY", "postgres",
          "psql", "--username", "cmsify", "--dbname", "cmsify", "--no-psqlrc",
          "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1", "--file=-",
        ], "docker-diagnostics-migrations", MAX_DOCKER_LOG_TIMEOUT_MS, {
          ...options,
          stdin: 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";',
        });
        const ids = lines(migrationResult.stdout).filter((value) => MIGRATION_ID.test(value)).slice(0, 64);
        migrations = { status: "captured", count: ids.length, ids };
      } catch {
        migrations = { status: "unavailable", count: 0, ids: [] };
      }
    }
    const summary = Object.freeze({
      status: options.resourcesStarted === false ? "unavailable" : "captured",
      stdoutBytes: Buffer.byteLength(result.stdout),
      stderrBytes: Buffer.byteLength(result.stderr),
    });
    const artifact = {
      schemaVersion: 1,
      status: summary.status,
      stdoutBytes: summary.stdoutBytes,
      stderrBytes: summary.stderrBytes,
      services,
      migrations,
    };
    await writeSafeAtomically(
      scope.repositoryRoot,
      resolve(scope.diagnosticsDirectory, "docker-diagnostics.json"),
      `${JSON.stringify(artifact, null, 2)}\n`,
      { encoding: "utf8", mode: 0o600 },
    );
    return summary;
  }

  return Object.freeze({
    environmentFile,

    writeEnvironment,

    async verifyPrerequisites(images, options = {}) {
      assert(images && typeof images === "object" && !Array.isArray(images), "Docker prerequisite images are required.");
      assertLifecycleOptions(options);
      const required = ["postgresImage", "minioImage", "baselineApiImage"];
      required.forEach((name) => assert(images[name] && typeof images[name] === "object", `Docker prerequisite ${name} is required.`));
      assert(typeof images.candidateImageId === "string" && IMAGE_ID.test(images.candidateImageId), "Docker prerequisite candidate image ID is required.");
      if (options.signal?.aborted) throw new Error("Docker prerequisite check was cancelled.");
      const runLabels = Object.entries(scope.labels).flatMap(([name, value]) => ["--label", `${name}=${value}`]);
      // Tool presence cannot be proven from image metadata. Probe the already-inspected immutable
      // image with --pull=never, no network, --rm, and both ownership labels; no run env is written.
      const probe = async (image, tool, args = ["--version"]) => execute("docker", [
        "run", "--rm", "--network", "none", "--pull", "never", ...runLabels,
        "--entrypoint", tool, image, ...args,
      ], `docker-prerequisite-${tool}`, undefined, options);
      try {
        await execute("docker", ["version", "--format", "{{json .Server.Version}}"], "docker-prerequisite-engine", undefined, options);
        await execute("docker", ["compose", "version", "--short"], "docker-prerequisite-compose", undefined, options);
        await execute("docker", ["compose", "--file", COMPOSE_FILE, "config", "--no-interpolate", "--quiet"], "docker-prerequisite-compose-config", undefined, options);
        const postgres = imageReference(images.postgresImage);
        for (const tool of ["pg_dump", "psql", "pg_restore"]) await probe(postgres, tool);
        const minio = imageReference(images.minioImage);
        for (const tool of ["mc", "curl"]) await probe(minio, tool);
        await probe(minio, "sha256sum", ["/dev/null"]);
        await probe(minio, "rm", ["-f", "/tmp/cmsify-prerequisite-nonexistent"]);
        await probe(imageReference(images.baselineApiImage), "curl");
        await probe(images.candidateImageId, "curl");
      } catch (error) {
        if (options.signal?.aborted || (error instanceof ProcessFailure && /:\s*aborted$/i.test(error.phase))) {
          throw new Error("Docker prerequisite check was cancelled.");
        }
        throw new Error("Docker prerequisite check failed.");
      }
      return Object.freeze({ status: "passed", mode: "immutable-image-nonpersistent-probes" });
    },

    async up(services, options = {}) {
      assertStringArray(services, "Docker services");
      services.forEach(assertService);
      assertLifecycleOptions(options);
      return compose(["up", "--detach", ...services], "docker-compose-up", undefined, options);
    },

    async stop(service, options = {}) {
      assertService(service);
      assertLifecycleOptions(options);
      return compose(["stop", service], "docker-compose-stop", undefined, options);
    },

    async start(service, options = {}) {
      assertService(service);
      assertLifecycleOptions(options);
      return compose(["start", service], "docker-compose-start", undefined, options);
    },

    async exec(service, args, options = {}) {
      assertService(service);
      assertStringArray(args, "Docker exec arguments");
      assert(options && typeof options === "object" && !Array.isArray(options), "Docker exec options must be an object.");
      assert(options.timeoutMs === undefined || (Number.isFinite(options.timeoutMs) && options.timeoutMs > 0), "Docker exec timeout must be positive.");
      assert(options.signal === undefined || options.signal instanceof AbortSignal, "Docker exec signal must be an AbortSignal.");
      assert(options.redact === undefined || (Array.isArray(options.redact) && options.redact.every((value) => typeof value === "string")), "Docker exec redactions must be strings.");
      assert(options.stdoutEncoding === undefined || ["utf8", "buffer"].includes(options.stdoutEncoding), "Docker exec stdoutEncoding must be utf8 or buffer.");
      assert(options.stdin === undefined || typeof options.stdin === "string", "Docker exec stdin must be a string.");
      return compose(["exec", ...(options.stdin === undefined ? [] : ["--interactive"]), "--no-TTY", service, ...args], "docker-compose-exec", undefined, options);
    },

    logs,

    async inspectImage(image, options = {}) {
      assertLifecycleOptions(options);
      const reference = imageReference(image);
      const inspected = await inspectDockerImage(reference, "docker-image-inspect", options);
      assert(inspected?.Os === "linux" && inspected?.Architecture === "amd64", `Docker image ${image.repository} is not linux/amd64.`);
      const canonicalReference = canonicalDockerHubReference(reference);
      assert(Array.isArray(inspected.RepoDigests) && inspected.RepoDigests.some((value) => canonicalDockerHubReference(value) === canonicalReference), `Docker image ${image.repository} does not contain the required repository digest.`);
      return inspected;
    },

    async inspectCandidateImage(reference, expected, options = {}) {
      assert(typeof reference === "string" && reference.length > 0 && !/[\r\n\0]/.test(reference), "Candidate image reference must be a non-empty single-line value.");
      assert(expected && typeof expected === "object" && !Array.isArray(expected), "Expected candidate identity is required.");
      assert(typeof expected.version === "string" && expected.version.length > 0 && !/[\r\n\0]/.test(expected.version), "Candidate version must be a non-empty single-line value.");
      assert(typeof expected.sourceSha === "string" && SOURCE_SHA.test(expected.sourceSha), "Candidate source SHA must be a full lowercase commit.");
      assertLifecycleOptions(options);
      const inspected = await inspectDockerImage(reference, "docker-candidate-image-inspect", options);
      assert(inspected?.Os === "linux" && inspected?.Architecture === "amd64", "Candidate image is not linux/amd64.");
      assert(typeof inspected.Id === "string" && IMAGE_ID.test(inspected.Id), "Candidate image ID is not a stable local SHA-256 identity.");
      const labels = inspected.Config?.Labels;
      assert(labels && typeof labels === "object" && !Array.isArray(labels), "Candidate image does not contain OCI labels.");
      assert(labels["org.opencontainers.image.version"] === expected.version, "Candidate OCI version label mismatch.");
      assert(labels["org.opencontainers.image.revision"] === expected.sourceSha, "Candidate OCI revision label mismatch.");
      return Object.freeze({
        reference,
        imageId: inspected.Id,
        platform: "linux/amd64",
        version: expected.version,
        sourceSha: expected.sourceSha,
        informationalVersion: `${expected.version}+${expected.sourceSha}`,
        labels: Object.freeze({
          "org.opencontainers.image.version": labels["org.opencontainers.image.version"],
          "org.opencontainers.image.revision": labels["org.opencontainers.image.revision"],
        }),
      });
    },

    async copyFrom(service, source, destination, options = {}) {
      assertService(service);
      assert(typeof source === "string" && source.length > 0 && typeof destination === "string" && destination.length > 0, "Docker copy paths must be non-empty strings.");
      assertLifecycleOptions(options);
      return compose(["cp", `${service}:${source}`, destination], "docker-compose-copy-from", undefined, options);
    },

    async copyTo(service, source, destination, options = {}) {
      assertService(service);
      assert(typeof source === "string" && source.length > 0 && typeof destination === "string" && destination.length > 0, "Docker copy paths must be non-empty strings.");
      assertLifecycleOptions(options);
      return compose(["cp", source, `${service}:${destination}`], "docker-compose-copy-to", undefined, options);
    },

    async discardDataVolumes(options = {}, finalFence) {
      assertLifecycleOptions(options);
      assert(finalFence === undefined || typeof finalFence === "function", "Docker discard final fence must be a function.");
      const beforeFence = await dataResourceSnapshot(options);
      if (finalFence) await finalFence();
      const afterFence = await dataResourceSnapshot(options);
      assert(sameDataResourceSnapshot(beforeFence, afterFence), "Owned data resource identity changed at the final discard fence.");
      for (const container of afterFence.containers) await execute("docker", ["rm", "--force", container], "docker-discard-state-remove-container", undefined, options);
      for (const volume of afterFence.volumes) await execute("docker", ["volume", "rm", volume], "docker-discard-state-remove-volume", undefined, options);
    },

    async cleanup(options = {}) {
      assertLifecycleOptions(options);
      assert(options.resourcesStarted === undefined || typeof options.resourcesStarted === "boolean", "Docker cleanup resource state must be boolean.");
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
        await collectFailure(() => logs(options));
        const [containers = [], networks = [], volumes = []] = await Promise.all([
          collectFailure(() => discover(["ps", "--all", "--quiet"], "docker-cleanup-discover-containers", options)),
          collectFailure(() => discover(["network", "ls", "--quiet"], "docker-cleanup-discover-networks", options)),
          collectFailure(() => discover(["volume", "ls", "--quiet"], "docker-cleanup-discover-volumes", options)),
        ]);
        await collectFailure(() => removeResources(containers, "container", ["rm", "--force"], "docker-cleanup-remove-container", options));
        await collectFailure(() => removeResources(networks, "network", ["network", "rm"], "docker-cleanup-remove-network", options));
        await collectFailure(() => removeResources(volumes, "volume", ["volume", "rm"], "docker-cleanup-remove-volume", options));
      } finally {
        await collectFailure(async () => {
          assertSafeEnvironmentFile();
          await assertPhysicalPath(scope.repositoryRoot, environmentFile, { leaf: "file", allowMissing: true });
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
