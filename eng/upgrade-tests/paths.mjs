import { randomBytes } from "node:crypto";
import { isAbsolute, relative, resolve, sep } from "node:path";

const RUN_ID = /^[a-z0-9][a-z0-9-]{7,47}$/;
const UPGRADE_TEST_LABEL = "io.syntaxcircus.cmsify.upgrade-test";
const UPGRADE_RUN_LABEL = "io.syntaxcircus.cmsify.upgrade-run";
const RUN_SCOPES = new WeakSet();

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function isContainedBy(parent, candidate) {
  const pathFromParent = relative(parent, candidate);
  return pathFromParent === "" || (!pathFromParent.startsWith(`..${sep}`) && pathFromParent !== ".." && !isAbsolute(pathFromParent));
}

function assertSafeRunId(runId) {
  assert(typeof runId === "string" && RUN_ID.test(runId), "A safe run id must contain 8-48 lowercase letters, digits, or hyphens.");
}

function createRunId() {
  return `cmsify-upgrade-${randomBytes(6).toString("hex")}`;
}

/**
 * @typedef {{
 *   runId:string,
 *   projectName:string,
 *   repositoryRoot:string,
 *   diagnosticsDirectory:string,
 *   labels:{"io.syntaxcircus.cmsify.upgrade-test":"true","io.syntaxcircus.cmsify.upgrade-run":string}
 * }} RunScope
 */

/**
 * Creates the names and filesystem paths owned by one upgrade rehearsal.
 * @param {string} repositoryRoot
 * @param {string} [requestedId]
 * @returns {RunScope}
 */
export function createRunScope(repositoryRoot, requestedId) {
  assert(typeof repositoryRoot === "string" && repositoryRoot.length > 0, "repositoryRoot must be a non-empty path.");
  assert(requestedId === undefined || typeof requestedId === "string", "A safe run id must be a string.");

  const runId = requestedId ?? createRunId();
  assertSafeRunId(runId);

  const resolvedRepositoryRoot = resolve(repositoryRoot);
  const diagnosticsRoot = resolve(resolvedRepositoryRoot, "artifacts", "upgrade-tests");
  const diagnosticsDirectory = resolve(diagnosticsRoot, runId);
  assert(isContainedBy(diagnosticsRoot, diagnosticsDirectory), "A safe run id must resolve below the repository-owned upgrade run root.");

  const scope = Object.freeze({
    runId,
    projectName: runId,
    repositoryRoot: resolvedRepositoryRoot,
    diagnosticsDirectory,
    labels: Object.freeze({
      [UPGRADE_TEST_LABEL]: "true",
      [UPGRADE_RUN_LABEL]: runId,
    }),
  });
  RUN_SCOPES.add(scope);
  return scope;
}

/**
 * Ensures a scope was created in this process and still represents only its repository-owned paths.
 * @param {unknown} scope
 * @returns {asserts scope is RunScope}
 */
export function assertTrustedRunScope(scope) {
  assert(scope !== null && typeof scope === "object" && RUN_SCOPES.has(scope), "A trusted safe run scope created by createRunScope is required.");
  assertSafeRunId(scope.runId);
  assert(scope.projectName === scope.runId, "A trusted safe run scope must use its run id as the project name.");
  assert(typeof scope.repositoryRoot === "string" && typeof scope.diagnosticsDirectory === "string", "A trusted safe run scope is incomplete.");

  const repositoryRoot = resolve(scope.repositoryRoot);
  const diagnosticsRoot = resolve(repositoryRoot, "artifacts", "upgrade-tests");
  const diagnosticsDirectory = resolve(diagnosticsRoot, scope.runId);
  assert(scope.repositoryRoot === repositoryRoot && scope.diagnosticsDirectory === diagnosticsDirectory && isContainedBy(diagnosticsRoot, diagnosticsDirectory), "A trusted safe run scope has unowned diagnostics paths.");
  assert(scope.labels?.[UPGRADE_TEST_LABEL] === "true" && scope.labels?.[UPGRADE_RUN_LABEL] === scope.runId, "A trusted safe run scope has invalid ownership labels.");
}

export const OWNERSHIP_LABELS = Object.freeze({
  upgradeTest: UPGRADE_TEST_LABEL,
  upgradeRun: UPGRADE_RUN_LABEL,
});
