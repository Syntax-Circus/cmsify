import { randomBytes } from "node:crypto";
import { isAbsolute, relative, resolve, sep } from "node:path";

const RUN_ID = /^[a-z0-9][a-z0-9-]{7,47}$/;
const UPGRADE_TEST_LABEL = "io.syntaxcircus.cmsify.upgrade-test";
const UPGRADE_RUN_LABEL = "io.syntaxcircus.cmsify.upgrade-run";

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function isContainedBy(parent, candidate) {
  const pathFromParent = relative(parent, candidate);
  return pathFromParent === "" || (!pathFromParent.startsWith(`..${sep}`) && pathFromParent !== ".." && !isAbsolute(pathFromParent));
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
  assert(RUN_ID.test(runId), "A safe run id must contain 8-48 lowercase letters, digits, or hyphens.");

  const resolvedRepositoryRoot = resolve(repositoryRoot);
  const diagnosticsRoot = resolve(resolvedRepositoryRoot, "artifacts", "upgrade-tests");
  const diagnosticsDirectory = resolve(diagnosticsRoot, runId);
  assert(isContainedBy(diagnosticsRoot, diagnosticsDirectory), "A safe run id must resolve below the repository-owned upgrade run root.");

  return Object.freeze({
    runId,
    projectName: runId,
    repositoryRoot: resolvedRepositoryRoot,
    diagnosticsDirectory,
    labels: Object.freeze({
      [UPGRADE_TEST_LABEL]: "true",
      [UPGRADE_RUN_LABEL]: runId,
    }),
  });
}

export const OWNERSHIP_LABELS = Object.freeze({
  upgradeTest: UPGRADE_TEST_LABEL,
  upgradeRun: UPGRADE_RUN_LABEL,
});
