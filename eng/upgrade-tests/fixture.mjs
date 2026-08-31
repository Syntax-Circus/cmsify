import { createHash } from "node:crypto";
import { lstat, mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { relative, resolve } from "node:path";

import { assertBaselineFixture, captureWebhookWorkerState } from "./assertions.mjs";
import { writeFixtureChecksums } from "./checksums.mjs";
import { createDockerHarness } from "./docker.mjs";
import { loadExpectedData } from "./expected.mjs";
import {
  FIXTURE_GENERATION_SCHEMA_VERSION,
  FIXTURE_GENERATOR_VERSION,
  FIXTURE_SEED_PATH,
  loadFixtureManifest,
} from "./manifest.mjs";
import { createRunScope } from "./paths.mjs";

const TEXT_MEDIA = Buffer.from("Cmsify v0.1.3 upgrade fixture\n", "utf8");
const PNG_MEDIA = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAE/wJ/lk8sAAAAAElFTkSuQmCC", "base64");
const UUID = /(?<![0-9a-f])[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}(?![0-9a-f])/gi;
const UUID_VALUE = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const FIXTURE_ENVIRONMENT = Object.freeze({
  POSTGRES_PASSWORD: "cmsify-fixture-postgres-only",
  MINIO_ROOT_PASSWORD: "cmsify-fixture-minio-only",
  CMSIFY_FIXTURE_ADMIN_PASSWORD: "Cmsify-fixture-admin-only-0.1.3!",
  CMSIFY_FIXTURE_ADMIN_PASSWORD_HASH: "fixture-only-existing-user-no-seed",
  CMSIFY_FIXTURE_LEGACY_KEY: "Q21zaWZ5IGZpeHR1cmUgbGVnYWN5IGtleSAwLjEuMyE=",
  CMSIFY_FIXTURE_LEGACY_KEY_BASE64: "Q21zaWZ5IGZpeHR1cmUgbGVnYWN5IGtleSAwLjEuMyE=",
  CMSIFY_FIXTURE_CANDIDATE_KEY_BASE64: "Q21zaWZ5IGZpeHR1cmUgY2FuZGlkYXRlIGtleSB2MSE=",
});

/** Writes only deterministic, source-derived fixture provenance through the checked-in generator. */
export async function writeFixtureGenerationMetadata({ repositoryRoot, fixtureDirectory }) {
  const seedSha256 = createHash("sha256").update(await readFile(resolve(repositoryRoot, FIXTURE_SEED_PATH))).digest("hex");
  const generation = {
    schemaVersion: FIXTURE_GENERATION_SCHEMA_VERSION,
    generatorVersion: FIXTURE_GENERATOR_VERSION,
    seed: { path: FIXTURE_SEED_PATH, sha256: seedSha256 },
  };
  const manifestPath = resolve(fixtureDirectory, "manifest.json");
  const expectedPath = resolve(fixtureDirectory, "expected.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  const expected = JSON.parse(await readFile(expectedPath, "utf8"));
  manifest.generation = generation;
  expected.provenance.generation = generation;
  const permissions = expected.scenarios.find(({ id }) => id === "permissions");
  assert(permissions, "Expected fixture data is missing the permissions scenario.");
  permissions.assertions = ["editor-primary-write-grant", "global-admin-restricted-read", "reader-primary-resolve", "reader-restricted-hidden"];
  await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  await writeFile(expectedPath, `${JSON.stringify(expected, null, 2)}\n`, "utf8");
  return Object.freeze(generation);
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function imageReference(image) {
  return `${image.repository}@${image.digest}`;
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function uuidPattern(value) {
  return new RegExp(`(?<![0-9a-f])${escapeRegex(value)}(?![0-9a-f])`, "gi");
}

function contentDerivedUuid(value) {
  const hexadecimal = createHash("sha256").update(value).digest("hex").slice(0, 32).split("");
  hexadecimal[12] = "4";
  hexadecimal[16] = "8";
  const joined = hexadecimal.join("");
  return `${joined.slice(0, 8)}-${joined.slice(8, 12)}-${joined.slice(12, 16)}-${joined.slice(16, 20)}-${joined.slice(20)}`;
}

const COPY_BLOCK = /(COPY "public"\."([^"]+)" \(([^\n]+)\) FROM stdin;\n)([\s\S]*?)(\\\.\n)/g;

function anonymousUuidDefinitions(dump, canonicalTargets) {
  const definitions = new Map();
  for (const match of dump.matchAll(COPY_BLOCK)) {
    const [, , table, columnText, body] = match;
    const columns = columnText.split(", ").map((column) => column.replaceAll('"', ""));
    const idIndex = columns.indexOf("id");
    if (idIndex < 0) continue;
    for (const row of body.split("\n").filter(Boolean)) {
      const values = row.split("\t");
      const source = values[idIndex]?.toLowerCase();
      if (!source || !UUID_VALUE.test(source) || canonicalTargets.has(source)) continue;
      const signatureValues = values.map((value, index) => index === idIndex ? "<canonical-id>" : value);
      const target = contentDerivedUuid(`${table}\n${columns.join("\t")}\n${signatureValues.join("\t")}`);
      if (definitions.has(source) && definitions.get(source) !== target) throw new Error(`Anonymous UUID ${source} has conflicting defining rows.`);
      definitions.set(source, target);
    }
  }
  return definitions;
}

function sortCopyRows(dump) {
  return dump.replace(COPY_BLOCK, (block, header, _table, _columns, body, footer) => {
    const rows = body.split("\n").filter(Boolean).sort();
    return `${header}${rows.length > 0 ? `${rows.join("\n")}\n` : ""}${footer}`;
  });
}

function normalizedRelativePath(root, file) {
  return relative(root, file).replaceAll("\\", "/");
}

async function fixtureFiles(root, directory = root) {
  const directoryStat = await lstat(directory);
  if (directoryStat.isSymbolicLink()) throw new Error(`Fixture tree contains a symbolic link: ${normalizedRelativePath(root, directory) || "."}.`);
  if (!directoryStat.isDirectory()) throw new Error(`Fixture tree root is not a directory: ${normalizedRelativePath(root, directory) || "."}.`);
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const path = resolve(directory, entry.name);
    const relativePath = normalizedRelativePath(root, path);
    if (entry.isSymbolicLink()) throw new Error(`Fixture tree contains a symbolic link: ${relativePath}.`);
    if (entry.isDirectory()) files.push(...await fixtureFiles(root, path));
    else if (entry.isFile()) files.push(normalizedRelativePath(root, path));
    else throw new Error(`Fixture tree contains an unsupported fixture entry: ${relativePath}.`);
  }
  return files.sort();
}

/** Runs work and preserves its failure ahead of any cleanup failure. */
export async function runWithCleanup(work, cleanup) {
  let result;
  let primaryFailure;
  try {
    result = await work();
  } catch (error) {
    primaryFailure = error;
  }

  try {
    await cleanup();
  } catch (cleanupFailure) {
    if (primaryFailure !== undefined) {
      const message = primaryFailure instanceof Error ? primaryFailure.message : "Fixture generation failed.";
      throw new AggregateError([primaryFailure, cleanupFailure], message, { cause: primaryFailure });
    }
    throw cleanupFailure;
  }

  if (primaryFailure !== undefined) throw primaryFailure;
  return result;
}

const FIXTURE_ASSERTION_CLEANUP_SQL = `
  DELETE FROM user_sessions;
  DELETE FROM audit_logs WHERE id <> :'canonical_audit_id';
  UPDATE api_clients SET last_used_at = NULL;
  UPDATE users SET last_login_at = CASE
    WHEN id = :'admin_user_id' THEN '2026-08-20T12:00:30Z'::timestamptz
    ELSE NULL
  END;
`;

export function fixtureAssertionCleanupCommand(adminUserId, canonicalAuditId) {
  assert(UUID_VALUE.test(adminUserId), "Fixture admin user ID must be a UUID.");
  assert(UUID_VALUE.test(canonicalAuditId), "Canonical fixture audit ID must be a UUID.");
  return Object.freeze({
    args: Object.freeze([
      "--set", `admin_user_id=${adminUserId}`,
      "--set", `canonical_audit_id=${canonicalAuditId}`,
      "--file=-",
    ]),
    stdin: FIXTURE_ASSERTION_CLEANUP_SQL,
  });
}

async function writeRunEnvironment(harness, scope, manifest) {
  await mkdir(resolve(scope.repositoryRoot, "tests", "upgrade", ".runs"), { recursive: true });
  const values = {
    CMSIFY_UPGRADE_RUN_ID: scope.runId,
    CMSIFY_UPGRADE_TEST_LABEL: "true",
    POSTGRES_IMAGE: imageReference(manifest.baseline.postgresImage),
    MINIO_IMAGE: imageReference(manifest.baseline.minioImage),
    BASELINE_API_IMAGE: imageReference(manifest.baseline.apiImage),
    CANDIDATE_API_IMAGE: "cmsify-upgrade-candidate:local",
    ...FIXTURE_ENVIRONMENT,
  };
  await writeFile(harness.environmentFile, `${Object.entries(values).map(([name, value]) => `${name}=${value}`).join("\n")}\n`, {
    encoding: "utf8",
    mode: 0o600,
    flag: "wx",
  });
}

async function waitForBaseline(harness) {
  const deadline = Date.now() + 120_000;
  let lastError;
  while (Date.now() < deadline) {
    try {
      await harness.exec("baseline-api", ["curl", "--silent", "--show-error", "--fail", "http://localhost:8080/health/ready"]);
      return;
    } catch (error) {
      lastError = error;
      await new Promise((resolvePromise) => setTimeout(resolvePromise, 1_000));
    }
  }
  throw new Error("Published baseline API did not become ready within 120 seconds.", { cause: lastError });
}

function parseIncludedResponse(stdout) {
  const normalized = stdout.replaceAll("\r\n", "\n");
  const separator = normalized.indexOf("\n\n");
  assert(separator >= 0, "Historical API response did not contain headers.");
  const headers = normalized.slice(0, separator);
  const bodyText = normalized.slice(separator + 2).trim();
  return { headers, body: bodyText.length === 0 ? undefined : JSON.parse(bodyText) };
}

async function apiRequest(harness, { method, path, token, body, form, headers = [] }) {
  const args = ["--silent", "--show-error", "--fail-with-body", "--include", "--request", method];
  if (token) args.push("--header", `Authorization: Bearer ${token}`);
  for (const header of headers) args.push("--header", header);
  if (body !== undefined) args.push("--header", "Content-Type: application/json", "--data", JSON.stringify(body));
  if (form) {
    for (const value of form) args.push("--form", value);
  }
  args.push(`http://localhost:8080${path}`);
  return parseIncludedResponse((await harness.exec("baseline-api", ["curl", ...args])).stdout);
}

function responseEtag(response) {
  const match = /^etag:\s*(.+)$/im.exec(response.headers);
  assert(match, "Historical API response omitted its ETag.");
  return match[1].trim();
}

function contentFields(fieldIds, suffix) {
  const nullable = { boolValue: null, mediaAssetId: null, fileAssetId: null, childContentItemId: null };
  return [
    { fieldId: fieldIds.title, order: 0, valueKind: "Text", textValue: `Fixture ${suffix}`, jsonValue: null, ...nullable },
    { fieldId: fieldIds.choice, order: 0, valueKind: "PickList", textValue: "alpha", jsonValue: null, ...nullable },
    { fieldId: fieldIds.component, order: 0, valueKind: "Component", textValue: null, jsonValue: { summary: `Inline ${suffix}`, accent: "alpha" }, ...nullable },
  ];
}

async function transitionContent(harness, workspaceId, contentId, token, publish) {
  await apiRequest(harness, { method: "POST", path: `/api/v1/workspaces/${workspaceId}/content/${contentId}/submit`, token });
  await apiRequest(harness, { method: "POST", path: `/api/v1/workspaces/${workspaceId}/content/${contentId}/approve`, token });
  return apiRequest(harness, { method: "POST", path: `/api/v1/workspaces/${workspaceId}/content/${contentId}/publish`, token, body: publish });
}

async function seedThroughPublishedApi(harness, expected, mediaPaths) {
  const login = await apiRequest(harness, {
    method: "POST",
    path: "/api/v1/auth/login",
    body: { email: "fixture-admin@example.test", password: FIXTURE_ENVIRONMENT.CMSIFY_FIXTURE_ADMIN_PASSWORD },
  });
  const token = login.body.token;
  const adminUser = login.body.user.id;

  const workspaceList = await apiRequest(harness, { method: "GET", path: "/api/v1/workspaces?offset=0&limit=50", token });
  const primaryWorkspace = workspaceList.body.items.find((item) => item.slug === "upgrade-fixture")?.id;
  assert(primaryWorkspace, "Published API did not seed the primary fixture workspace.");

  const restricted = await apiRequest(harness, {
    method: "POST", path: "/api/v1/workspaces", token,
    body: { name: "Restricted Fixture", slug: "restricted-fixture", description: "Synthetic denied-access workspace" },
  });
  const restrictedWorkspace = restricted.body.id;

  const editor = await apiRequest(harness, {
    method: "POST", path: "/api/v1/users", token,
    body: {
      email: "fixture-editor@example.test", displayName: "Fixture Editor", role: "Editor",
      temporaryPassword: "Cmsify-fixture-editor-only-0.1.3!", isSuperAdmin: false, timeZoneId: "UTC",
      workspaceAccesses: [{ workspaceId: primaryWorkspace, accessLevel: "Write" }],
    },
  });

  // v0.1.3 has no worker-disable switch and POST always creates an active endpoint.
  // Create and immediately deactivate it before any fixture operation can enqueue an event.
  const webhook = await apiRequest(harness, {
    method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/webhooks`, token,
    body: { name: "Fixture Webhook", url: "https://fixture-webhook.example.test/cmsify-upgrade-fixture", secret: "fixture-webhook-secret", events: ["content.published"] },
  });
  const webhookCurrent = await apiRequest(harness, { method: "GET", path: `/api/v1/workspaces/${primaryWorkspace}/webhooks/${webhook.body.endpoint.id}`, token });
  await apiRequest(harness, {
    method: "PUT", path: `/api/v1/workspaces/${primaryWorkspace}/webhooks/${webhook.body.endpoint.id}`, token,
    headers: [`If-Match: ${responseEtag(webhookCurrent)}`],
    body: { name: "Fixture Webhook", url: "https://fixture-webhook.example.test/cmsify-upgrade-fixture", isActive: false, events: ["content.published"] },
  });

  const choiceOne = await apiRequest(harness, {
    method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/picklists`, token,
    body: {
      name: "Fixture Choices", slug: "fixture-choices", description: "Immutable choice revision fixture",
      options: [{ label: "Alpha (original)", value: "alpha", order: 0 }, { label: "Beta", value: "beta", order: 1 }],
    },
  });
  const choiceSet = choiceOne.body.id;
  const choiceRevisionOne = choiceOne.body.currentRevisionId;

  const componentCreate = await apiRequest(harness, {
    method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/components`, token,
    body: { name: "Fixture Card", slug: "fixture-card", description: "Inline acyclic fixture component" },
  });
  const component = componentCreate.body.id;
  const componentFields = await apiRequest(harness, {
    method: "PUT", path: `/api/v1/workspaces/${primaryWorkspace}/components/${component}/versions/1/fields`, token,
    body: [
      { key: "summary", label: "Summary", helpText: null, order: 0, isRequired: true, minOccurrences: 1, maxOccurrences: 1, primitiveType: "Text", nestedComponentId: null, fieldConfig: null },
      { key: "accent", label: "Accent", helpText: null, order: 1, isRequired: true, minOccurrences: 1, maxOccurrences: 1, primitiveType: "PickList", nestedComponentId: null, fieldConfig: { picklistId: choiceSet, picklistRevisionId: choiceRevisionOne, multiple: false } },
    ],
  });
  const componentPublished = await apiRequest(harness, { method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/components/${component}/versions/1/publish`, token });

  const templateCreate = await apiRequest(harness, {
    method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/templates`, token,
    body: { name: "Fixture Article", slug: "fixture-article", description: "Primitive and inline component fixture" },
  });
  const template = templateCreate.body.id;
  const templateVersion = templateCreate.body.currentVersion.id;
  const baseField = { sectionId: null, helpText: null, isRequired: true, minOccurrences: 1, maxOccurrences: 1, isOpen: false, compositionMode: "Inline", templateId: null, allowedTypes: [] };
  const titleField = await apiRequest(harness, {
    method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/templates/${template}/versions/1/fields`, token,
    body: { ...baseField, key: "title", label: "Title", order: 0, primitiveType: "Text", fieldConfig: null, componentId: null },
  });
  const choiceField = await apiRequest(harness, {
    method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/templates/${template}/versions/1/fields`, token,
    body: { ...baseField, key: "choice", label: "Choice", order: 1, primitiveType: "PickList", fieldConfig: { picklistId: choiceSet, picklistRevisionId: choiceRevisionOne, multiple: false }, componentId: null },
  });
  const componentField = await apiRequest(harness, {
    method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/templates/${template}/versions/1/fields`, token,
    body: { ...baseField, key: "card", label: "Card", order: 2, primitiveType: null, fieldConfig: null, componentId: component },
  });
  await apiRequest(harness, { method: "PUT", path: `/api/v1/workspaces/${primaryWorkspace}/templates/${template}/versions/1/publish`, token });

  const fieldIds = { title: titleField.body.id, choice: choiceField.body.id, component: componentField.body.id };
  async function createContent(slug, suffix) {
    return apiRequest(harness, {
      method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/content`, token,
      body: { templateVersionId: templateVersion, slug, localeCode: "en-US", translationGroupId: null, tags: [], fields: contentFields(fieldIds, suffix) },
    });
  }
  const draft = await createContent("fixture-draft", "draft");
  const published = await createContent("fixture-published", "published");
  const scheduled = await createContent("fixture-scheduled", "scheduled");
  const expired = await createContent("fixture-expired", "expired");
  await transitionContent(harness, primaryWorkspace, published.body.id, token, { publishAt: null, effectiveStartAt: expected.content.currentEffectiveStartAt, effectiveEndAt: expected.content.currentEffectiveEndAt });
  await transitionContent(harness, primaryWorkspace, scheduled.body.id, token, { publishAt: expected.content.scheduledPublishAt, effectiveStartAt: null, effectiveEndAt: null });
  await transitionContent(harness, primaryWorkspace, expired.body.id, token, { publishAt: null, effectiveStartAt: expected.content.expiredEffectiveStartAt, effectiveEndAt: expected.content.expiredEffectiveEndAt });

  const choiceTwo = await apiRequest(harness, {
    method: "PUT", path: `/api/v1/workspaces/${primaryWorkspace}/picklists/${choiceSet}`, token,
    headers: [`If-Match: ${responseEtag(choiceOne)}`],
    body: {
      name: "Fixture Choices", slug: "fixture-choices", description: "Immutable choice revision fixture",
      options: [{ label: "Alpha (renamed)", value: "alpha", order: 0 }, { label: "Beta", value: "beta", order: 1 }],
    },
  });

  await Promise.all([
    harness.copyTo("baseline-api", mediaPaths.text, "/tmp/cmsify-fixture.txt"),
    harness.copyTo("baseline-api", mediaPaths.image, "/tmp/cmsify-pixel.png"),
  ]);
  const textMedia = await apiRequest(harness, { method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/media`, token, form: ["file=@/tmp/cmsify-fixture.txt;type=text/plain;filename=fixture.txt", "altText=Deterministic text fixture"] });
  const imageMedia = await apiRequest(harness, { method: "POST", path: `/api/v1/workspaces/${primaryWorkspace}/media`, token, form: ["file=@/tmp/cmsify-pixel.png;type=image/png;filename=pixel.png", "altText=Deterministic one pixel image"] });

  const reader = await apiRequest(harness, {
    method: "POST", path: "/api/v1/clients", token,
    body: { name: "Fixture Reader", description: "Synthetic least-privilege upgrade reader", role: "Reader", workspaceId: primaryWorkspace, expiresAt: null },
  });
  return {
    ids: {
      primaryWorkspace, restrictedWorkspace, adminUser, editorUser: editor.body.userId,
      readerClient: reader.body.client.id, template, component, choiceSet,
      draftContent: draft.body.id, publishedContent: published.body.id, scheduledContent: scheduled.body.id,
      expiredContent: expired.body.id, textMedia: textMedia.body.id, imageMedia: imageMedia.body.id,
      webhook: webhook.body.endpoint.id,
    },
    relatedIds: {
      templateVersion,
      titleField: titleField.body.id,
      choiceField: choiceField.body.id,
      componentField: componentField.body.id,
      componentVersion: componentPublished.body.currentVersion.id,
      componentTextField: componentFields.body.fields.find((field) => field.key === "summary").id,
      componentChoiceField: componentFields.body.fields.find((field) => field.key === "accent").id,
      choiceRevisionOne,
      choiceRevisionTwo: choiceTwo.body.currentRevisionId,
    },
  };
}

async function psql(harness, args, options = {}) {
  return harness.exec("postgres", ["psql", "--username", "cmsify", "--dbname", "cmsify", "--no-psqlrc", "--set", "ON_ERROR_STOP=1", ...args], options);
}

async function applyHistoricalSeed(harness, repositoryRoot, expected, observed) {
  const seedPath = resolve(repositoryRoot, "tests", "upgrade", "seed", "v0.1.3.sql");
  await harness.copyTo("postgres", seedPath, "/tmp/cmsify-v0.1.3.sql");
  const variables = {
    primary_workspace: observed.ids.primaryWorkspace,
    admin_user: observed.ids.adminUser,
    choice_set: observed.ids.choiceSet,
    component: observed.ids.component,
    template: observed.ids.template,
    draft_content: observed.ids.draftContent,
    published_content: observed.ids.publishedContent,
    scheduled_content: observed.ids.scheduledContent,
    expired_content: observed.ids.expiredContent,
    text_media: observed.ids.textMedia,
    image_media: observed.ids.imageMedia,
    reader_client: observed.ids.readerClient,
    reader_token: expected.authentication.readerToken,
    webhook: observed.ids.webhook,
  };
  const variableArgs = Object.entries(variables).flatMap(([name, value]) => ["--set", `${name}=${value}`]);
  await psql(harness, [...variableArgs, "--file", "/tmp/cmsify-v0.1.3.sql"]);
  const versionSql = "SELECT id FROM content_versions WHERE content_item_id = :'content_item_id' ORDER BY version_number LIMIT 1;";
  observed.relatedIds.publishedVersion = (await psql(harness, ["--tuples-only", "--no-align", "--set", `content_item_id=${observed.ids.publishedContent}`, "--file=-"], { stdin: versionSql })).stdout.trim();
  observed.relatedIds.expiredVersion = (await psql(harness, ["--tuples-only", "--no-align", "--set", `content_item_id=${observed.ids.expiredContent}`, "--file=-"], { stdin: versionSql })).stdout.trim();
  observed.relatedIds.webhookDelivery = expected.relatedIds.webhookDelivery;
  observed.ids.audit = expected.ids.audit;
}

async function uploadExactMedia(harness, expected, mediaPaths) {
  await harness.exec("minio", ["mc", "alias", "set", "fixture", "http://localhost:9000", "cmsify-fixture-access", FIXTURE_ENVIRONMENT.MINIO_ROOT_PASSWORD]);
  await harness.exec("minio", ["mc", "mb", "--ignore-existing", "fixture/cmsify-upgrade"]);
  await Promise.all([
    harness.copyTo("minio", mediaPaths.text, "/tmp/cmsify-fixture.txt"),
    harness.copyTo("minio", mediaPaths.image, "/tmp/cmsify-pixel.png"),
  ]);
  await harness.exec("minio", ["mc", "cp", "--attr", "Content-Type=text/plain", "/tmp/cmsify-fixture.txt", `fixture/cmsify-upgrade/${expected.media.text.storageKey}`]);
  await harness.exec("minio", ["mc", "cp", "--attr", "Content-Type=image/png", "/tmp/cmsify-pixel.png", `fixture/cmsify-upgrade/${expected.media.image.storageKey}`]);
}

export function canonicalizeFixtureDump(dump, observed, expected) {
  const sourceToTarget = new Map();
  const sourceNames = new Map();
  for (const group of ["ids", "relatedIds"]) {
    for (const [name, source] of Object.entries(observed[group])) {
      const target = expected[group][name];
      assert(typeof source === "string" && source.length > 0, `Observed UUID is absent for ${group}.${name}.`);
      assert(typeof target === "string" && target.length > 0, `Canonical UUID is absent for ${group}.${name}.`);
      if (source.toLowerCase() !== target.toLowerCase()) {
        sourceToTarget.set(source.toLowerCase(), target.toLowerCase());
        sourceNames.set(source.toLowerCase(), `${group}.${name}`);
      }
    }
  }

  const canonicalTargets = new Set([
    ...Object.values(expected.ids),
    ...Object.values(expected.relatedIds),
  ].map((value) => value.toLowerCase()));
  const found = [...new Set([...dump.matchAll(UUID)].map((match) => match[0].toLowerCase()))].sort();
  for (const [source] of sourceToTarget) assert(found.includes(source), `Observed UUID for ${sourceNames.get(source)} (${source}) is absent from the SQL dump.`);

  let canonical = dump;
  for (const [source, target] of sourceToTarget) {
    const pattern = uuidPattern(source);
    assert(pattern.test(canonical), `Observed UUID ${source} appears only in an unsafe substring.`);
    canonical = canonical.replace(uuidPattern(source), target);
    assert(!uuidPattern(source).test(canonical), `Observed UUID ${source} remains after canonicalization.`);
  }

  const anonymous = anonymousUuidDefinitions(canonical, canonicalTargets);
  for (const [source, target] of anonymous) {
    assert(!canonicalTargets.has(target), `Anonymous UUID ${source} collides with a canonical semantic UUID.`);
    canonicalTargets.add(target);
    canonical = canonical.replace(uuidPattern(source), target);
    assert(!uuidPattern(source).test(canonical), `Anonymous UUID ${source} remains after canonicalization.`);
  }

  const remaining = [...new Set([...canonical.matchAll(UUID)].map((match) => match[0].toLowerCase()))]
    .filter((value) => !canonicalTargets.has(value));
  assert(remaining.length === 0, "The SQL dump contains a UUID that was not safely canonicalized.");
  return sortCopyRows(canonical);
}

function normalizeDump(text) {
  const normalized = text.replaceAll("\r\n", "\n").replaceAll("\r", "\n");
  return `${normalized.split("\n")
    .filter((line) => !line.startsWith("-- Dumped from database version") && !line.startsWith("-- Dumped by pg_dump version"))
    .join("\n").replace(/\n+$/, "")}\n`;
}

async function exportDatabase(harness, fixtureDirectory, observed, expected) {
  await harness.exec("postgres", [
    "pg_dump", "--username", "cmsify", "--dbname", "cmsify", "--format=plain", "--no-owner", "--no-privileges",
    "--quote-all-identifiers", "--encoding=UTF8", "--restrict-key=cmsifyupgradefixturev013",
    "--file=/tmp/cmsify-v0.1.3-database.sql",
  ]);
  const destination = resolve(fixtureDirectory, "database.sql");
  await harness.copyFrom("postgres", "/tmp/cmsify-v0.1.3-database.sql", destination);
  const normalized = normalizeDump(await readFile(destination, "utf8"));
  await writeFile(destination, canonicalizeFixtureDump(normalized, observed, expected), "utf8");
}

async function writeMedia(fixtureDirectory, expected) {
  const text = resolve(fixtureDirectory, ...expected.media.text.fixturePath.split("/"));
  const image = resolve(fixtureDirectory, ...expected.media.image.fixturePath.split("/"));
  await Promise.all([mkdir(resolve(text, ".."), { recursive: true }), mkdir(resolve(image, ".."), { recursive: true })]);
  await Promise.all([writeFile(text, TEXT_MEDIA), writeFile(image, PNG_MEDIA)]);
  return { text, image };
}

/**
 * Generates a complete v0.1.3 fixture into a prepared directory.
 * @param {{repositoryRoot:string,fixtureDirectory:string,keepDiagnostics?:boolean}} input
 * @returns {Promise<object>}
 */
export async function generateFixture({ repositoryRoot, fixtureDirectory, keepDiagnostics = false }) {
  await writeFixtureGenerationMetadata({ repositoryRoot, fixtureDirectory });
  const manifest = loadFixtureManifest(fixtureDirectory);
  const expected = await loadExpectedData(fixtureDirectory, manifest);
  const scope = createRunScope(repositoryRoot);
  const harness = createDockerHarness(scope);
  return runWithCleanup(async () => {
    try {
      const mediaPaths = await writeMedia(fixtureDirectory, expected);
      await writeRunEnvironment(harness, scope, manifest);
      await harness.up(["postgres", "minio", "baseline-api"]);
      await Promise.all([
        harness.inspectImage(manifest.baseline.apiImage),
        harness.inspectImage(manifest.baseline.postgresImage),
        harness.inspectImage(manifest.baseline.minioImage),
      ]);
      await waitForBaseline(harness);
      await harness.exec("minio", ["mc", "alias", "set", "fixture", "http://localhost:9000", "cmsify-fixture-access", FIXTURE_ENVIRONMENT.MINIO_ROOT_PASSWORD]);
      await harness.exec("minio", ["mc", "mb", "--ignore-existing", "fixture/cmsify-upgrade"]);
      const observed = await seedThroughPublishedApi(harness, expected, mediaPaths);
      await harness.stop("baseline-api");
      await applyHistoricalSeed(harness, repositoryRoot, expected, observed);
      await uploadExactMedia(harness, expected, mediaPaths);
      const ids = { ...observed.ids, ...observed.relatedIds };
      const webhookWorkerStateBeforeStart = await captureWebhookWorkerState(harness, ids);
      await harness.start("baseline-api");
      await waitForBaseline(harness);
      const assertionResult = await assertBaselineFixture({ harness, expected, ids, webhookWorkerStateBeforeStart });
      await harness.stop("baseline-api");
      const cleanupCommand = fixtureAssertionCleanupCommand(observed.ids.adminUser, expected.ids.audit);
      await psql(harness, cleanupCommand.args, { stdin: cleanupCommand.stdin });
      await exportDatabase(harness, fixtureDirectory, observed, expected);
      await writeFixtureChecksums(fixtureDirectory, manifest.requiredFiles);
      return Object.freeze({
        fixtureDirectory,
        runId: scope.runId,
        diagnosticsDirectory: scope.diagnosticsDirectory,
        mediaAggregateSha256: assertionResult.mediaAggregateSha256,
        imageReferences: Object.freeze({
          api: imageReference(manifest.baseline.apiImage),
          postgres: imageReference(manifest.baseline.postgresImage),
          minio: imageReference(manifest.baseline.minioImage),
        }),
      });
    } catch (error) {
      try {
        await harness.logs();
      } catch {
        // The primary generation failure remains authoritative.
      }
      throw error;
    }
  }, async () => {
    await harness.cleanup();
    void keepDiagnostics;
  });
}

/**
 * Fails at the first ordinal path or byte difference between two fixture trees.
 * @param {string} firstDirectory
 * @param {string} secondDirectory
 * @returns {Promise<void>}
 */
export async function compareFixtureTrees(firstDirectory, secondDirectory) {
  const firstFiles = await fixtureFiles(firstDirectory);
  const secondFiles = await fixtureFiles(secondDirectory);
  const files = [...new Set([...firstFiles, ...secondFiles])].sort();
  for (const file of files) {
    if (!firstFiles.includes(file) || !secondFiles.includes(file)) throw new Error(`Fixture drift: ${file}.`);
    const [first, second] = await Promise.all([
      readFile(resolve(firstDirectory, file)),
      readFile(resolve(secondDirectory, file)),
    ]);
    if (!first.equals(second)) throw new Error(`Fixture drift: ${file}.`);
  }
}
