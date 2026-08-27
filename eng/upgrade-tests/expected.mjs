import { readFile } from "node:fs/promises";
import { isAbsolute, relative, resolve } from "node:path";

export const REQUIRED_ASSERTIONS_BY_SCENARIO = Object.freeze({
  workspaces: Object.freeze(["primary-and-restricted-exist"]),
  permissions: Object.freeze(["editor-primary-write-grant", "reader-primary-resolve", "reader-restricted-hidden"]),
  templates: Object.freeze(["published-template-fields"]),
  components: Object.freeze(["inline-acyclic-snapshot"]),
  "choice-revisions": Object.freeze(["immutable-revisions", "published-choice-label-snapshot"]),
  "content-versions": Object.freeze(["draft-and-published-distinct"]),
  schedules: Object.freeze(["future-publish-at", "bounded-current-effective", "expired-effective-range", "expired-hidden"]),
  media: Object.freeze(["available-media-download", "historical-deleted-media-hidden", "candidate-deletion-boundary"]),
  webhooks: Object.freeze(["inert-endpoint", "terminal-delivery", "startup-state-stable"]),
  audit: Object.freeze(["linked-mutation"]),
  authentication: Object.freeze(["fixed-reader-token"]),
  provenance: Object.freeze(["baseline-manifest-binding", "package-provenance"]),
});

export const BASELINE_MIGRATIONS = Object.freeze([
  "20260517174817_InitialSchema",
  "20260517194907_AddUserSessions",
  "20260517222010_AddUserTheme",
  "20260518140420_AddWorkspaceAccessGrants",
  "20260519120338_AddContentVersions",
  "20260519230251_AddPickLists",
  "20260602135111_AddContentVersionEffectiveRanges",
  "20260820151206_AddComponentsAndPickListRevisions",
  "20260820172030_AddWebhookDeliveryLeases",
  "20260820172346_AddApiClientTokenIdentifiers",
  "20260821005219_AddPackageProvenanceToReusableModels",
]);

export const CANDIDATE_MIGRATIONS = Object.freeze([
  ...BASELINE_MIGRATIONS,
  "20260826135220_AddWebhookOutbox",
  "20260826215147_ExpandWebhookSecretCiphertext",
  "20260827135736_AddMediaLifecycleReconciliation",
]);

const EXPECTED_KEYS = ["schemaVersion", "fixtureClock", "ids", "relatedIds", "migrations", "authentication", "media", "content", "provenance", "timestamps", "candidate", "scenarios"];
const ID_KEYS = ["primaryWorkspace", "restrictedWorkspace", "adminUser", "editorUser", "readerClient", "template", "component", "choiceSet", "draftContent", "publishedContent", "scheduledContent", "expiredContent", "textMedia", "imageMedia", "webhook", "audit"];
const RELATED_ID_KEYS = ["templateVersion", "titleField", "choiceField", "componentField", "componentVersion", "componentTextField", "componentChoiceField", "choiceRevisionOne", "choiceRevisionTwo", "publishedVersion", "expiredVersion", "webhookDelivery"];
const AUTHENTICATION_KEYS = ["readerToken", "readerTokenIdentifier", "adminEmail", "adminPassword"];
const MEDIA_KEYS = ["text", "image"];
const MEDIA_ITEM_KEYS = ["storageKey", "fixturePath", "fileName", "contentType", "sizeBytes", "sha256", "lifecycle"];
const LIFECYCLE_KEYS = ["historicalIsDeleted", "historicalDeletedAt", "historicalVisible", "candidateBlobState", "candidateDeletionIntentReason"];
const CONTENT_KEYS = ["publishedChoiceValue", "publishedChoiceLabel", "currentChoiceLabel", "scheduledPublishAt", "currentEffectiveStartAt", "currentEffectiveEndAt", "expiredEffectiveStartAt", "expiredEffectiveEndAt"];
const PROVENANCE_KEYS = ["baselineVersion", "sourceSha", "apiImageDigest", "packageNamespace", "packageId", "packageVersion"];
const TIMESTAMP_KEYS = [
  "workspaceCreatedAt", "workspaceUpdatedAt", "choiceRevisionOneCreatedAt", "choiceRevisionTwoCreatedAt",
  "componentCreatedAt", "componentUpdatedAt", "templateCreatedAt", "templateUpdatedAt",
  "draftContentCreatedAt", "publishedContentCreatedAt", "publishedContentUpdatedAt",
  "scheduledContentCreatedAt", "scheduledContentUpdatedAt", "expiredContentCreatedAt", "expiredContentUpdatedAt",
  "textMediaCreatedAt", "imageMediaCreatedAt", "imageMediaUpdatedAt", "readerClientCreatedAt",
  "webhookCreatedAt", "webhookDeliveryCreatedAt", "webhookDeliveryLastAttemptAt", "auditTimestamp",
];
const CANDIDATE_KEYS = ["migrations", "storageProvider", "legacyWebhookKeyBase64", "legacyWebhookSecretSha256"];
const SCENARIO_KEYS = ["id", "assertions"];
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const SHA256 = /^[0-9a-f]{64}$/;
const UTC_TIMESTAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$/;

function fail(message) {
  throw new Error(`Invalid expected fixture data: ${message}`);
}

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function assert(condition, message) {
  if (!condition) fail(message);
}

function assertExactKeys(value, expectedKeys, name) {
  assert(isPlainObject(value), `${name} must be an object.`);
  const actual = Object.keys(value).sort();
  const expected = [...expectedKeys].sort();
  assert(actual.length === expected.length && actual.every((key, index) => key === expected[index]), `${name} has unknown or missing properties.`);
}

function assertNonEmptyString(value, name) {
  assert(typeof value === "string" && value.length > 0, `${name} must be a non-empty string.`);
}

function assertTimestamp(value, name, { nullable = false } = {}) {
  if (nullable && value === null) return;
  assert(typeof value === "string" && UTC_TIMESTAMP.test(value) && Number.isFinite(Date.parse(value)), `${name} must be a UTC timestamp.`);
}

function assertCanonicalFixturePath(value, fixtureDirectory, name) {
  assertNonEmptyString(value, name);
  assert(!value.includes("\\") && !isAbsolute(value) && value.split("/").every((part) => part && part !== "." && part !== ".."), `${name} must be a canonical fixture-relative path.`);
  const pathFromFixture = relative(resolve(fixtureDirectory), resolve(fixtureDirectory, value));
  assert(pathFromFixture !== ".." && !pathFromFixture.startsWith("../") && !pathFromFixture.startsWith("..\\") && !isAbsolute(pathFromFixture), `${name} escapes the fixture directory.`);
}

function assertExactStringArray(actual, expected, name) {
  assert(Array.isArray(actual), `${name} must be an array.`);
  assert(actual.length === expected.length && actual.every((value, index) => value === expected[index]), `${name} must contain the exact required values in order.`);
}

function validateIds(value, keys, name) {
  assertExactKeys(value, keys, name);
  for (const key of keys) assert(typeof value[key] === "string" && UUID.test(value[key]), `${name}.${key} must be a canonical UUID.`);
  assert(new Set(Object.values(value)).size === keys.length, `${name} UUIDs must be unique.`);
}

function validateMediaItem(value, fixtureDirectory, name) {
  assertExactKeys(value, MEDIA_ITEM_KEYS, name);
  assertNonEmptyString(value.storageKey, `${name}.storageKey`);
  assertCanonicalFixturePath(value.fixturePath, fixtureDirectory, `${name}.fixturePath`);
  assertNonEmptyString(value.fileName, `${name}.fileName`);
  assertNonEmptyString(value.contentType, `${name}.contentType`);
  assert(Number.isSafeInteger(value.sizeBytes) && value.sizeBytes > 0, `${name}.sizeBytes must be a positive integer.`);
  assert(typeof value.sha256 === "string" && SHA256.test(value.sha256), `${name}.sha256 must be a lowercase SHA-256 digest.`);
  assertExactKeys(value.lifecycle, LIFECYCLE_KEYS, `${name}.lifecycle`);
  assert(typeof value.lifecycle.historicalIsDeleted === "boolean", `${name}.lifecycle.historicalIsDeleted must be boolean.`);
  assertTimestamp(value.lifecycle.historicalDeletedAt, `${name}.lifecycle.historicalDeletedAt`, { nullable: true });
  assert(typeof value.lifecycle.historicalVisible === "boolean", `${name}.lifecycle.historicalVisible must be boolean.`);
  assert(["Available", "DeletePending"].includes(value.lifecycle.candidateBlobState), `${name}.lifecycle.candidateBlobState is unsupported.`);
  assert(value.lifecycle.candidateDeletionIntentReason === null || value.lifecycle.candidateDeletionIntentReason === "migration_deleted", `${name}.lifecycle.candidateDeletionIntentReason is unsupported.`);
}

function validateScenarios(value, manifest) {
  assert(Array.isArray(value), "scenarios must be an array.");
  assert(value.length === manifest.requiredScenarios.length, "scenarios must provide exact required coverage.");
  const byId = new Map();
  for (const scenario of value) {
    assertExactKeys(scenario, SCENARIO_KEYS, "scenario");
    assert(typeof scenario.id === "string" && manifest.requiredScenarios.includes(scenario.id), "scenario id is unknown.");
    assert(!byId.has(scenario.id), "scenario ids must be unique.");
    const required = REQUIRED_ASSERTIONS_BY_SCENARIO[scenario.id];
    assert(Array.isArray(scenario.assertions) && scenario.assertions.length > 0, `${scenario.id} must contain assertion categories.`);
    const actual = [...scenario.assertions].sort();
    const expected = [...required].sort();
    assert(actual.length === expected.length && actual.every((category, index) => category === expected[index]), `${scenario.id} assertion categories must be exact, known, and complete.`);
    byId.set(scenario.id, scenario);
  }
  assert(manifest.requiredScenarios.every((scenario) => byId.has(scenario)), "scenarios must provide exact required coverage.");
}

function deepFreeze(value) {
  if (Array.isArray(value)) for (const item of value) deepFreeze(item);
  else if (isPlainObject(value)) for (const item of Object.values(value)) deepFreeze(item);
  return Object.freeze(value);
}

/**
 * Validates the exact v0.1.3 expected-data schema and binds it to its manifest.
 * @param {unknown} value
 * @param {import('./manifest.mjs').FixtureManifest} manifest
 * @param {string} fixtureDirectory
 */
export function validateExpectedData(value, manifest, fixtureDirectory) {
  assert(typeof fixtureDirectory === "string" && fixtureDirectory.length > 0, "fixtureDirectory must be a non-empty path.");
  assertExactKeys(value, EXPECTED_KEYS, "expected data");
  assert(value.schemaVersion === 1, "schemaVersion must be 1.");
  assertTimestamp(value.fixtureClock, "fixtureClock");
  validateIds(value.ids, ID_KEYS, "ids");
  validateIds(value.relatedIds, RELATED_ID_KEYS, "relatedIds");
  assert(new Set([...Object.values(value.ids), ...Object.values(value.relatedIds)]).size === ID_KEYS.length + RELATED_ID_KEYS.length, "all fixture UUIDs must be unique.");
  assertExactStringArray(value.migrations, BASELINE_MIGRATIONS, "migrations");

  assertExactKeys(value.authentication, AUTHENTICATION_KEYS, "authentication");
  assert(typeof value.authentication.readerToken === "string" && value.authentication.readerToken.startsWith("cmsify_") && value.authentication.readerToken.length > 20, "authentication.readerToken must be a fixture API token.");
  assertNonEmptyString(value.authentication.readerTokenIdentifier, "authentication.readerTokenIdentifier");
  assert(typeof value.authentication.adminEmail === "string" && /^[^@\s]+@example\.test$/.test(value.authentication.adminEmail), "authentication.adminEmail must be a synthetic example.test address.");
  assert(typeof value.authentication.adminPassword === "string" && value.authentication.adminPassword.startsWith("Cmsify-fixture-") && value.authentication.adminPassword.length >= 20, "authentication.adminPassword must be an explicit fixture-only password.");

  assertExactKeys(value.media, MEDIA_KEYS, "media");
  validateMediaItem(value.media.text, fixtureDirectory, "media.text");
  validateMediaItem(value.media.image, fixtureDirectory, "media.image");
  assert(value.media.text.lifecycle.historicalIsDeleted === false && value.media.text.lifecycle.historicalVisible === true && value.media.text.lifecycle.historicalDeletedAt === null, "active text media must remain historically visible.");
  assert(value.media.text.lifecycle.candidateBlobState === "Available" && value.media.text.lifecycle.candidateDeletionIntentReason === null, "active text media must migrate to Available without deletion intent.");
  assert(value.media.image.lifecycle.historicalIsDeleted === true && value.media.image.lifecycle.historicalVisible === false && value.media.image.lifecycle.historicalDeletedAt !== null, "image media must define the historical deleted and hidden boundary.");
  assert(value.media.image.lifecycle.candidateBlobState === "DeletePending", "deleted media must migrate to DeletePending.");
  assert(value.media.image.lifecycle.candidateDeletionIntentReason === "migration_deleted", "deleted media must create the migration_deleted deletion intent.");
  for (const item of [value.media.text, value.media.image]) assert(manifest.requiredFiles.includes(item.fixturePath), `${item.fixturePath} must be listed in manifest.requiredFiles.`);

  assertExactKeys(value.content, CONTENT_KEYS, "content");
  for (const key of ["publishedChoiceValue", "publishedChoiceLabel", "currentChoiceLabel"]) assertNonEmptyString(value.content[key], `content.${key}`);
  for (const key of ["scheduledPublishAt", "currentEffectiveStartAt", "currentEffectiveEndAt", "expiredEffectiveStartAt", "expiredEffectiveEndAt"]) assertTimestamp(value.content[key], `content.${key}`);
  const clock = Date.parse(value.fixtureClock);
  assert(Date.parse(value.content.currentEffectiveStartAt) <= clock && clock < Date.parse(value.content.currentEffectiveEndAt), "current effective range must contain fixtureClock.");
  assert(Date.parse(value.content.expiredEffectiveStartAt) < Date.parse(value.content.expiredEffectiveEndAt) && Date.parse(value.content.expiredEffectiveEndAt) <= clock, "expired effective range must end no later than fixtureClock.");
  assert(Date.parse(value.content.scheduledPublishAt) > clock, "scheduledPublishAt must be after fixtureClock.");

  assertExactKeys(value.provenance, PROVENANCE_KEYS, "provenance");
  assert(value.provenance.baselineVersion === manifest.baseline.version, "provenance.baselineVersion must equal the manifest baseline version.");
  assert(value.provenance.sourceSha === manifest.baseline.sourceSha, "provenance.sourceSha must equal the manifest source SHA.");
  assert(value.provenance.apiImageDigest === manifest.baseline.apiImage.digest, "provenance.apiImageDigest must equal the manifest API image digest.");
  for (const key of ["packageNamespace", "packageId", "packageVersion"]) assertNonEmptyString(value.provenance[key], `provenance.${key}`);

  assertExactKeys(value.timestamps, TIMESTAMP_KEYS, "timestamps");
  for (const key of TIMESTAMP_KEYS) assertTimestamp(value.timestamps[key], `timestamps.${key}`);

  assertExactKeys(value.candidate, CANDIDATE_KEYS, "candidate");
  assertExactStringArray(value.candidate.migrations, CANDIDATE_MIGRATIONS, "candidate.migrations exact 14-migration boundary");
  assert(value.candidate.storageProvider === "s3", "candidate.storageProvider must be canonical s3.");
  let legacyKey;
  try {
    legacyKey = Buffer.from(value.candidate.legacyWebhookKeyBase64, "base64");
  } catch {
    legacyKey = Buffer.alloc(0);
  }
  assert(legacyKey.length === 32 && legacyKey.toString("base64") === value.candidate.legacyWebhookKeyBase64, "candidate.legacyWebhookKeyBase64 must be canonical Base64 for exactly 32 bytes.");
  assert(typeof value.candidate.legacyWebhookSecretSha256 === "string" && SHA256.test(value.candidate.legacyWebhookSecretSha256), "candidate.legacyWebhookSecretSha256 must be a lowercase SHA-256 digest.");
  validateScenarios(value.scenarios, manifest);

  return deepFreeze(JSON.parse(JSON.stringify(value)));
}

/** Loads and validates expected.json before orchestration creates Docker resources. */
export async function loadExpectedData(fixtureDirectory, manifest) {
  let value;
  try {
    value = JSON.parse(await readFile(resolve(fixtureDirectory, manifest.expectedDataFile), "utf8"));
  } catch {
    fail("expected.json must contain valid JSON.");
  }
  return validateExpectedData(value, manifest, fixtureDirectory);
}
