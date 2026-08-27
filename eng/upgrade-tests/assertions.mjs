import { createDecipheriv, createHash } from "node:crypto";

import { createDockerHttpAdapter, requestBytes, requestJson } from "./http.mjs";

function ensure(condition, message) {
  if (!condition) throw new Error(message);
}

function idsOf(context) {
  return context.ids ?? { ...context.expected.ids, ...context.expected.relatedIds };
}

function asItems(body, name) {
  ensure(body && Array.isArray(body.items), `${name} did not return a paged item collection`);
  return body.items;
}

function historicallyUnpagedItems(context, body, name) {
  if (context.phase !== "candidate" && Array.isArray(body)) return body;
  return asItems(body, name);
}

function itemById(items, id, name) {
  const item = items.find((candidate) => candidate?.id === id);
  ensure(item, `${name} omitted expected id ${id}`);
  return item;
}

function exactArray(actual, expected, message) {
  ensure(Array.isArray(actual) && actual.length === expected.length && actual.every((value, index) => value === expected[index]), message);
}

function sameInstant(actual, expected) {
  if (typeof actual !== "string" || typeof expected !== "string") return false;
  const actualMilliseconds = Date.parse(actual);
  const expectedMilliseconds = Date.parse(expected);
  return Number.isFinite(actualMilliseconds)
    && Number.isFinite(expectedMilliseconds)
    && actualMilliseconds === expectedMilliseconds;
}

function absoluteUrl(context, path) {
  ensure(typeof context.apiBaseUrl === "string" && context.apiBaseUrl.length > 0, "API base URL is required");
  return new URL(path, context.apiBaseUrl.endsWith("/") ? context.apiBaseUrl : `${context.apiBaseUrl}/`).toString();
}

function httpOf(context) {
  return context.http ?? { requestJson, requestBytes };
}

async function httpJson(context, path, options = {}) {
  const adapter = httpOf(context);
  ensure(typeof adapter.requestJson === "function", "the HTTP adapter does not support JSON requests");
  return adapter.requestJson({
    url: absoluteUrl(context, path),
    method: options.method ?? "GET",
    token: Object.hasOwn(options, "token") ? options.token : context.token,
    body: options.body,
    headers: options.headers,
    expectedStatuses: new Set(options.expectedStatuses ?? [200]),
    signal: context.signal,
  });
}

async function httpBytes(context, path, options = {}) {
  const adapter = httpOf(context);
  ensure(typeof adapter.requestBytes === "function", "the HTTP adapter does not support byte requests");
  return adapter.requestBytes({
    url: absoluteUrl(context, path),
    method: options.method ?? "GET",
    token: Object.hasOwn(options, "token") ? options.token : context.token,
    expectedStatuses: new Set(options.expectedStatuses ?? [200]),
    signal: context.signal,
  });
}

function psqlVariableArguments(parameters) {
  ensure(parameters && typeof parameters === "object" && !Array.isArray(parameters), "SQL parameters must be an object");
  const args = [];
  for (const [name, value] of Object.entries(parameters).sort(([left], [right]) => left.localeCompare(right))) {
    ensure(/^[a-z][a-z0-9_]*$/.test(name), "SQL parameter names must be canonical lower-case identifiers");
    ensure(["string", "number", "boolean"].includes(typeof value) && String(value).length > 0 && !String(value).includes("\0"), `SQL parameter ${name} has an unsupported value`);
    args.push("--set", `${name}=${value}`);
  }
  return args;
}

export function createDockerSqlAdapter(docker) {
  ensure(docker && typeof docker.exec === "function", "a Docker SQL adapter is required");
  async function scalar(statement, parameters = {}) {
    ensure(typeof statement === "string" && statement.length > 0, "a constant SQL statement is required");
    const variableArguments = psqlVariableArguments(parameters);
    try {
      const result = await docker.exec("postgres", [
        "psql", "--username", "cmsify", "--dbname", "cmsify",
        "--no-psqlrc", "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1",
        ...variableArguments,
        "--file=-",
      ], { stdin: statement });
      return result.stdout.trim();
    } catch {
      throw new Error("PostgreSQL invariant query failed; row values and query text were withheld");
    }
  }
  return Object.freeze({
    scalar,
    async json(statement, parameters = {}) {
      const value = await scalar(statement, parameters);
      try {
        return JSON.parse(value);
      } catch {
        throw new Error("PostgreSQL invariant query did not return valid JSON; row values were withheld");
      }
    },
  });
}

function sqlOf(context) {
  return context.sql ?? createDockerSqlAdapter(context.docker);
}

async function scalar(context, statement, parameters = {}) {
  const adapter = sqlOf(context);
  ensure(typeof adapter.scalar === "function", "a SQL scalar adapter is required");
  return adapter.scalar(statement, parameters);
}

async function jsonRows(context, statement, parameters = {}) {
  const adapter = sqlOf(context);
  ensure(typeof adapter.json === "function", "a SQL JSON adapter is required");
  return adapter.json(statement, parameters);
}

async function immutableHistorySnapshot(context) {
  const ids = idsOf(context);
  const adapter = sqlOf(context);
  ensure(typeof adapter.json === "function", "a SQL JSON adapter is required");
  const snapshot = await adapter.json(`
    SELECT jsonb_build_object(
      'componentVersionCount', (SELECT count(*) FROM component_versions WHERE component_id = :'component_id'),
      'componentFieldCount', (SELECT count(*) FROM component_fields WHERE component_version_id = :'component_version_id'),
      'choiceRevisionCount', (SELECT count(*) FROM pick_list_revisions WHERE pick_list_id = :'choice_set_id'),
      'contentVersionCount', (SELECT count(*) FROM content_versions WHERE content_item_id = :'published_content_id'),
      'originalChoiceLabel', (SELECT label FROM pick_list_revision_options WHERE pick_list_revision_id = :'choice_revision_one_id' AND value = 'alpha'),
      'currentChoiceLabel', (SELECT label FROM pick_list_options WHERE pick_list_id = :'choice_set_id' AND value = 'alpha'),
      'publishedChoiceLabel', (SELECT display_label FROM content_version_field_values WHERE content_version_id = :'published_version_id' AND field_id = :'choice_field_id')
    )::text;
  `, {
    component_id: ids.component,
    component_version_id: ids.componentVersion,
    choice_set_id: ids.choiceSet,
    published_content_id: ids.publishedContent,
    choice_revision_one_id: ids.choiceRevisionOne,
    published_version_id: ids.publishedVersion,
    choice_field_id: ids.choiceField,
  });
  ensure(snapshot?.componentVersionCount === 1 && snapshot?.componentFieldCount === 2, "existing immutable component revision history changed");
  ensure(snapshot?.choiceRevisionCount === 2, "existing immutable choice revision history changed");
  ensure(snapshot?.contentVersionCount === 1, "existing immutable published content history changed");
  ensure(snapshot?.originalChoiceLabel === context.expected.content.publishedChoiceLabel, "existing original choice label changed");
  ensure(snapshot?.currentChoiceLabel === context.expected.content.currentChoiceLabel, "existing current choice label changed");
  ensure(snapshot?.publishedChoiceLabel === context.expected.content.publishedChoiceLabel, "existing published choice label snapshot changed");
  return snapshot;
}

async function expectSqlCount(context, statement, parameters, expected, message) {
  ensure(await scalar(context, statement, parameters) === String(expected), message);
}

function dockerStorageAdapter(docker) {
  ensure(docker && typeof docker.exec === "function", "a Docker storage adapter is required");
  return Object.freeze({
    async sha256(storageKey, assetId) {
      const destination = `/tmp/cmsify-invariant-${assetId}`;
      try {
        await docker.exec("minio", ["mc", "cp", `fixture/cmsify-upgrade/${storageKey}`, destination]);
        const result = await docker.exec("minio", ["sha256sum", destination]);
        return result.stdout.trim().split(/\s+/, 1)[0];
      } catch {
        throw new Error(`Stored media hash failed for asset ${assetId}; object bytes and storage credentials were withheld`);
      }
    },
  });
}

function storageOf(context) {
  return context.storage ?? dockerStorageAdapter(context.docker);
}

async function adminToken(context) {
  if (context.adminToken) return context.adminToken;
  const response = await httpJson(context, "/api/v1/auth/login", {
    method: "POST",
    token: undefined,
    body: { email: context.expected.authentication.adminEmail, password: context.expected.authentication.adminPassword },
  });
  ensure(typeof response.body?.token === "string" && response.body.token.length > 0, "fixture admin login did not return a session token");
  return response.body.token;
}

async function assertHealthLive(context) {
  const response = await httpJson(context, "/health/live");
  ensure(response.body?.status === "Healthy", "liveness did not report Healthy");
}

function candidateInformationalVersion(candidate) {
  ensure(candidate && typeof candidate === "object", "candidate identity is required");
  ensure(typeof candidate.version === "string" && candidate.version.length > 0, "candidate version is missing");
  ensure(/^[0-9a-f]{40}$/.test(candidate.sourceSha), "candidate source SHA is not a full lowercase commit");
  const informationalVersion = `${candidate.version}+${candidate.sourceSha}`;
  if (candidate.informationalVersion !== undefined) {
    ensure(candidate.informationalVersion === informationalVersion, "candidate informational version does not match the build contract");
  }
  return informationalVersion;
}

async function assertHealthReady(context) {
  const response = await httpJson(context, "/health/ready");
  ensure(response.body?.status === "Healthy", "readiness did not report Healthy");
  if (context.phase === "candidate") ensure(response.body?.metadata?.version === candidateInformationalVersion(context.candidate), "candidate readiness version does not match the tested image informational version");
}

async function assertMigrationHistory(context) {
  const actual = (await scalar(context, 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";')).split(/\r?\n/).filter(Boolean);
  const expected = context.phase === "candidate" ? context.expected.candidate.migrations : context.expected.migrations;
  exactArray(actual, expected, `${context.phase} migration history is not the exact expected ${expected.length}-migration set`);
}

async function assertWorkspaces(context) {
  const ids = idsOf(context);
  const list = await httpJson(context, "/api/v1/workspaces?page=1&pageSize=50");
  itemById(asItems(list.body, "workspace list"), ids.primaryWorkspace, "workspace list");
  const detail = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}`);
  ensure(detail.body?.id === ids.primaryWorkspace && detail.body?.slug === "upgrade-fixture", "primary workspace detail changed");
  const timestamps = context.expected.timestamps;
  await expectSqlCount(context, "SELECT count(*) FROM workspaces WHERE id IN (:'primary_workspace_id', :'restricted_workspace_id') AND created_at = :'created_at'::timestamptz AND updated_at = :'updated_at'::timestamptz;", {
    primary_workspace_id: ids.primaryWorkspace,
    restricted_workspace_id: ids.restrictedWorkspace,
    created_at: timestamps.workspaceCreatedAt,
    updated_at: timestamps.workspaceUpdatedAt,
  }, 2, "both fixed synthetic workspaces must remain related and timestamp-stable");
}

async function assertEditorGrant(context) {
  const ids = idsOf(context);
  await expectSqlCount(context, "SELECT count(*) FROM user_workspace_accesses access JOIN users actor ON actor.id = access.user_id JOIN workspaces workspace ON workspace.id = access.workspace_id WHERE access.user_id = :'editor_user_id' AND access.workspace_id = :'primary_workspace_id' AND access.access_level = 'Write' AND actor.role = 'Editor' AND actor.is_active AND NOT actor.is_deleted;", {
    editor_user_id: ids.editorUser,
    primary_workspace_id: ids.primaryWorkspace,
  }, 1, "the active Editor write grant and both foreign-key parents must remain linked");
}

async function assertReaderPrimaryResolve(context) {
  const ids = idsOf(context);
  const response = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.publishedContent}?resolve=true&asOf=${encodeURIComponent(context.expected.fixtureClock)}`);
  ensure(response.body?.id === ids.publishedContent, "reader could not resolve primary published content");
}

async function assertReaderRestrictedHidden(context) {
  const ids = idsOf(context);
  await httpJson(context, `/api/v1/workspaces/${ids.restrictedWorkspace}`, { expectedStatuses: [404] });
}

async function assertTemplateFields(context) {
  const ids = idsOf(context);
  const list = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/templates?page=1&pageSize=50`);
  itemById(asItems(list.body, "template list"), ids.template, "template list");
  const detail = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/templates/${ids.template}`);
  ensure(detail.body?.id === ids.template && detail.body?.currentVersion?.id === ids.templateVersion, "template current version changed");
  const versions = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/templates/${ids.template}/versions?page=1&pageSize=50`);
  itemById(historicallyUnpagedItems(context, versions.body, "template version list"), ids.templateVersion, "template version list");
  const version = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/templates/${ids.template}/versions/1`);
  const fields = version.body?.fields;
  ensure(Array.isArray(fields) && fields.length === 3, "published template must retain its three fields");
  ensure(fields.some((field) => field.id === ids.titleField && field.primitiveType === "Text"), "template text field changed");
  ensure(fields.some((field) => field.id === ids.choiceField && field.primitiveType === "PickList"), "template choice field changed");
  ensure(fields.some((field) => field.id === ids.componentField && field.componentId === ids.component), "template inline component field changed");
  await expectSqlCount(context, "SELECT count(*) FROM template_fields field JOIN template_versions version ON version.id = field.template_version_id JOIN templates template ON template.id = version.template_id WHERE field.id IN (:'title_field_id', :'choice_field_id', :'component_field_id') AND version.id = :'template_version_id' AND template.id = :'template_id' AND template.created_at = :'created_at'::timestamptz AND template.updated_at = :'updated_at'::timestamptz;", {
    title_field_id: ids.titleField,
    choice_field_id: ids.choiceField,
    component_field_id: ids.componentField,
    template_version_id: ids.templateVersion,
    template_id: ids.template,
    created_at: context.expected.timestamps.templateCreatedAt,
    updated_at: context.expected.timestamps.templateUpdatedAt,
  }, 3, "template fields, foreign-key parents, and fixed timestamps must remain linked");
}

async function assertComponentSnapshot(context) {
  const ids = idsOf(context);
  const list = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/components?page=1&pageSize=50`);
  itemById(historicallyUnpagedItems(context, list.body, "component list"), ids.component, "component list");
  const detail = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/components/${ids.component}`);
  ensure(detail.body?.currentVersion?.id === ids.componentVersion, "component current version changed");
  const version = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/components/${ids.component}/versions/1`);
  const fields = version.body?.fields;
  ensure(Array.isArray(fields) && fields.length === 2 && fields.every((field) => field.nestedComponentId === null), "component graph is no longer acyclic");
  const content = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.publishedContent}?resolve=true&asOf=${encodeURIComponent(context.expected.fixtureClock)}`);
  const snapshot = content.body?.fields?.find((field) => field.fieldId === ids.componentField)?.jsonValue;
  ensure(snapshot?.summary === "Inline published" && snapshot?.accent === "alpha", "inline component snapshot JSON changed");
  await expectSqlCount(context, "SELECT count(*) FROM components WHERE id = :'component_id' AND created_at = :'created_at'::timestamptz AND updated_at = :'updated_at'::timestamptz;", {
    component_id: ids.component,
    created_at: context.expected.timestamps.componentCreatedAt,
    updated_at: context.expected.timestamps.componentUpdatedAt,
  }, 1, "component fixed timestamps changed");
  await expectSqlCount(context, "SELECT count(*) FROM component_fields field JOIN component_versions version ON version.id = field.component_version_id JOIN components component ON component.id = version.component_id WHERE version.id = :'component_version_id' AND component.id = :'component_id' AND field.nested_component_id IS NOT NULL;", {
    component_version_id: ids.componentVersion,
    component_id: ids.component,
  }, 0, "component graph must remain acyclic in storage");
}

async function assertImmutableRevisions(context) {
  const ids = idsOf(context);
  const list = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/picklists?page=1&pageSize=50`);
  itemById(historicallyUnpagedItems(context, list.body, "choice-set list"), ids.choiceSet, "choice-set list");
  const current = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/picklists/${ids.choiceSet}`);
  ensure(current.body?.currentRevisionId === ids.choiceRevisionTwo && current.body?.currentVersionNumber === 2, "choice set current revision changed");
  ensure(current.body?.options?.some((option) => option.value === "alpha" && option.label === context.expected.content.currentChoiceLabel), "current choice label changed");
  const original = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/picklists/${ids.choiceSet}/revisions/${ids.choiceRevisionOne}`);
  ensure(original.body?.options?.some((option) => option.value === "alpha" && option.label === context.expected.content.publishedChoiceLabel), "original immutable choice revision changed");
  const timestamps = context.expected.timestamps;
  await expectSqlCount(context, "SELECT count(*) FROM pick_list_revisions revision JOIN pick_lists choice_set ON choice_set.id = revision.pick_list_id WHERE revision.pick_list_id = :'choice_set_id' AND ((revision.id = :'revision_one_id' AND revision.version_number = 1 AND revision.created_at = :'revision_one_created_at'::timestamptz) OR (revision.id = :'revision_two_id' AND revision.version_number = 2 AND revision.created_at = :'revision_two_created_at'::timestamptz));", {
    choice_set_id: ids.choiceSet,
    revision_one_id: ids.choiceRevisionOne,
    revision_one_created_at: timestamps.choiceRevisionOneCreatedAt,
    revision_two_id: ids.choiceRevisionTwo,
    revision_two_created_at: timestamps.choiceRevisionTwoCreatedAt,
  }, 2, "both immutable choice revisions and fixed timestamps must remain intact");
}

async function assertPublishedChoiceLabel(context) {
  const ids = idsOf(context);
  const version = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.publishedContent}/versions/1`);
  const field = version.body?.fields?.find((candidate) => candidate.fieldId === ids.choiceField);
  ensure(field?.textValue === context.expected.content.publishedChoiceValue, "published choice value changed");
  ensure(field?.displayLabel === context.expected.content.publishedChoiceLabel, "published choice label snapshot changed");
  await expectSqlCount(context, "SELECT count(*) FROM content_version_field_values value JOIN content_versions version ON version.id = value.content_version_id WHERE value.content_version_id = :'published_version_id' AND value.field_id = :'choice_field_id' AND value.text_value = :'choice_value' AND value.display_label = :'choice_label';", {
    published_version_id: ids.publishedVersion,
    choice_field_id: ids.choiceField,
    choice_value: context.expected.content.publishedChoiceValue,
    choice_label: context.expected.content.publishedChoiceLabel,
  }, 1, "published choice value and label snapshot must remain immutable");
}

async function assertContentVersions(context) {
  const ids = idsOf(context);
  const [draft, published, list, versions] = await Promise.all([
    httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.draftContent}`),
    httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.publishedContent}`),
    httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content?page=1&pageSize=50`),
    httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.publishedContent}/versions?page=1&pageSize=50`),
  ]);
  ensure(draft.body?.id === ids.draftContent && draft.body?.status === "Draft", "draft content changed");
  ensure(published.body?.id === ids.publishedContent && published.body?.status === "Published", "published content changed");
  itemById(asItems(list.body, "content list"), ids.draftContent, "content list");
  itemById(asItems(list.body, "content list"), ids.publishedContent, "content list");
  itemById(historicallyUnpagedItems(context, versions.body, "content version list"), ids.publishedVersion, "content version list");
  await expectSqlCount(context, "SELECT count(*) FROM content_items content JOIN template_versions template_version ON template_version.id = content.template_version_id JOIN workspaces workspace ON workspace.id = content.workspace_id WHERE content.id IN (:'draft_content_id', :'published_content_id') AND template_version.id = :'template_version_id' AND workspace.id = :'primary_workspace_id' AND ((content.id = :'draft_content_id' AND content.created_at = :'draft_created_at'::timestamptz AND content.updated_at = :'draft_created_at'::timestamptz) OR (content.id = :'published_content_id' AND content.created_at = :'published_created_at'::timestamptz AND content.updated_at = :'published_updated_at'::timestamptz));", {
    draft_content_id: ids.draftContent,
    published_content_id: ids.publishedContent,
    template_version_id: ids.templateVersion,
    primary_workspace_id: ids.primaryWorkspace,
    draft_created_at: context.expected.timestamps.draftContentCreatedAt,
    published_created_at: context.expected.timestamps.publishedContentCreatedAt,
    published_updated_at: context.expected.timestamps.publishedContentUpdatedAt,
  }, 2, "draft and published content relationships and fixed timestamps must remain distinct");
}

async function assertFuturePublishAt(context) {
  const ids = idsOf(context);
  const detail = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.scheduledContent}`);
  ensure(detail.body?.id === ids.scheduledContent && detail.body?.status === "Approved", "scheduled content state changed");
  await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.scheduledContent}?resolve=true&asOf=${encodeURIComponent(context.expected.fixtureClock)}`, { expectedStatuses: [404] });
  await expectSqlCount(context, "SELECT count(*) FROM content_items WHERE id = :'scheduled_content_id' AND status = 'Approved' AND publish_at = :'publish_at'::timestamptz AND created_at = :'created_at'::timestamptz AND updated_at = :'updated_at'::timestamptz;", {
    scheduled_content_id: ids.scheduledContent,
    publish_at: context.expected.content.scheduledPublishAt,
    created_at: context.expected.timestamps.scheduledContentCreatedAt,
    updated_at: context.expected.timestamps.scheduledContentUpdatedAt,
  }, 1, "future PublishAt and fixed scheduled timestamps must remain intact");
}

async function assertBoundedCurrent(context) {
  const ids = idsOf(context);
  const response = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.publishedContent}?resolve=true&asOf=${encodeURIComponent(context.expected.fixtureClock)}`);
  ensure(response.body?.id === ids.publishedContent, "bounded current content did not resolve");
  await expectSqlCount(context, "SELECT count(*) FROM content_versions WHERE id = :'published_version_id' AND effective_start_at = :'effective_start_at'::timestamptz AND effective_end_at = :'effective_end_at'::timestamptz;", {
    published_version_id: ids.publishedVersion,
    effective_start_at: context.expected.content.currentEffectiveStartAt,
    effective_end_at: context.expected.content.currentEffectiveEndAt,
  }, 1, "bounded currently-effective range must remain intact");
}

async function assertExpiredRange(context) {
  const ids = idsOf(context);
  const version = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.expiredContent}/versions/1`);
  ensure(version.body?.id === ids.expiredVersion, "expired content version changed");
  ensure(
    sameInstant(version.body?.effectiveStartAt, context.expected.content.expiredEffectiveStartAt)
      && sameInstant(version.body?.effectiveEndAt, context.expected.content.expiredEffectiveEndAt),
    "expired effective range changed",
  );
  await expectSqlCount(context, "SELECT count(*) FROM content_versions version JOIN content_items content ON content.id = version.content_item_id WHERE version.id = :'expired_version_id' AND version.effective_start_at = :'effective_start_at'::timestamptz AND version.effective_end_at = :'effective_end_at'::timestamptz AND content.created_at = :'content_created_at'::timestamptz AND content.updated_at = :'content_updated_at'::timestamptz;", {
    expired_version_id: ids.expiredVersion,
    effective_start_at: context.expected.content.expiredEffectiveStartAt,
    effective_end_at: context.expected.content.expiredEffectiveEndAt,
    content_created_at: context.expected.timestamps.expiredContentCreatedAt,
    content_updated_at: context.expected.timestamps.expiredContentUpdatedAt,
  }, 1, "expired effective range and fixed content timestamps must remain intact");
}

async function assertExpiredHidden(context) {
  const ids = idsOf(context);
  await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.expiredContent}?resolve=true&asOf=${encodeURIComponent(context.expected.fixtureClock)}`, { expectedStatuses: [404] });
}

async function assertAvailableMedia(context) {
  const ids = idsOf(context);
  const expectedMedia = context.expected.media.text;
  const list = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/media?page=1&pageSize=50`);
  const item = itemById(asItems(list.body, "media list"), ids.textMedia, "media list");
  ensure(item.fileName === expectedMedia.fileName && item.mimeType === expectedMedia.contentType && item.sizeBytes === expectedMedia.sizeBytes, "available media metadata changed");
  const detail = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/media/${ids.textMedia}`);
  ensure(detail.body?.id === ids.textMedia, "available media detail changed");
  const downloaded = await httpBytes(context, `/api/v1/workspaces/${ids.primaryWorkspace}/media/${ids.textMedia}/file`);
  ensure(downloaded.sha256 === expectedMedia.sha256, `media asset ${ids.textMedia} SHA-256 mismatch: expected ${expectedMedia.sha256}, actual ${downloaded.sha256}`);
  await expectSqlCount(context, "SELECT count(*) FROM media_assets asset JOIN workspaces workspace ON workspace.id = asset.workspace_id WHERE asset.id = :'text_media_id' AND workspace.id = :'primary_workspace_id' AND NOT asset.is_deleted AND asset.deleted_at IS NULL AND asset.storage_provider = :'storage_provider' AND asset.storage_key = :'storage_key' AND asset.created_at = :'created_at'::timestamptz AND asset.updated_at = :'updated_at'::timestamptz;", {
    text_media_id: ids.textMedia,
    primary_workspace_id: ids.primaryWorkspace,
    storage_provider: context.expected.candidate.storageProvider,
    storage_key: expectedMedia.storageKey,
    created_at: context.expected.timestamps.textMediaCreatedAt,
    updated_at: context.expected.timestamps.textMediaCreatedAt,
  }, 1, "available media canonical workspace/storage identity and fixed timestamps changed");
}

async function assertDeletedMediaHidden(context) {
  const ids = idsOf(context);
  const expectedMedia = context.expected.media.image;
  await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/media/${ids.imageMedia}`, { expectedStatuses: [404] });
  await httpBytes(context, `/api/v1/workspaces/${ids.primaryWorkspace}/media/${ids.imageMedia}/file`, { expectedStatuses: [404] });
  await expectSqlCount(context, "SELECT count(*) FROM media_assets asset JOIN workspaces workspace ON workspace.id = asset.workspace_id WHERE asset.id = :'image_media_id' AND workspace.id = :'primary_workspace_id' AND asset.is_deleted AND asset.deleted_at = :'deleted_at'::timestamptz AND asset.file_name = :'file_name' AND asset.mime_type = :'content_type' AND asset.size_bytes = :'size_bytes'::bigint AND asset.storage_key = :'storage_key' AND asset.created_at = :'created_at'::timestamptz AND asset.updated_at = :'updated_at'::timestamptz;", {
    image_media_id: ids.imageMedia,
    primary_workspace_id: ids.primaryWorkspace,
    deleted_at: expectedMedia.lifecycle.historicalDeletedAt,
    file_name: expectedMedia.fileName,
    content_type: expectedMedia.contentType,
    size_bytes: expectedMedia.sizeBytes,
    storage_key: expectedMedia.storageKey,
    created_at: context.expected.timestamps.imageMediaCreatedAt,
    updated_at: context.expected.timestamps.imageMediaUpdatedAt,
  }, 1, "historical soft-deleted media workspace metadata and fixed deletion boundary changed");
  const storedSha = await storageOf(context).sha256(expectedMedia.storageKey, ids.imageMedia);
  ensure(storedSha === expectedMedia.sha256, `media asset ${ids.imageMedia} SHA-256 mismatch: expected ${expectedMedia.sha256}, actual ${storedSha}`);
}

async function assertCandidateDeletionBoundary(context) {
  const ids = idsOf(context);
  if (context.phase !== "candidate") {
    await expectSqlCount(context, "SELECT count(*) FROM media_assets asset JOIN workspaces workspace ON workspace.id = asset.workspace_id WHERE workspace.id = :'primary_workspace_id' AND ((asset.id = :'text_media_id' AND NOT asset.is_deleted AND asset.deleted_at IS NULL) OR (asset.id = :'image_media_id' AND asset.is_deleted AND asset.deleted_at = :'deleted_at'::timestamptz));", {
      primary_workspace_id: ids.primaryWorkspace,
      text_media_id: ids.textMedia,
      image_media_id: ids.imageMedia,
      deleted_at: context.expected.media.image.lifecycle.historicalDeletedAt,
    }, 2, "historical active and deleted media workspace/lifecycle boundary changed");
    return;
  }
  const rows = await jsonRows(context, "SELECT COALESCE(jsonb_agg(jsonb_build_object('id', asset.id, 'workspaceId', asset.workspace_id, 'provider', asset.storage_provider, 'storageKey', asset.storage_key, 'blobState', asset.blob_state, 'deletionIntentReason', intent.reason, 'deletionIntentProvider', intent.provider, 'deletionIntentStorageKey', intent.storage_key, 'deletionIntentCount', (SELECT count(*) FROM media_deletion_intents all_intents WHERE all_intents.media_asset_id = asset.id)) ORDER BY asset.id), '[]'::jsonb)::text FROM media_assets asset JOIN workspaces workspace ON workspace.id = asset.workspace_id LEFT JOIN media_deletion_intents intent ON intent.media_asset_id = asset.id AND intent.completed_at IS NULL WHERE workspace.id = :'primary_workspace_id' AND asset.id IN (:'text_media_id', :'image_media_id');", {
    text_media_id: ids.textMedia,
    image_media_id: ids.imageMedia,
    primary_workspace_id: ids.primaryWorkspace,
  });
  ensure(Array.isArray(rows) && rows.length === 2, "candidate media migration must retain exactly both fixture assets");
  const text = rows.find((row) => row.id === ids.textMedia);
  const image = rows.find((row) => row.id === ids.imageMedia);
  ensure(text, `candidate media migration omitted active asset ${ids.textMedia}`);
  ensure(image, `candidate media migration omitted deleted asset ${ids.imageMedia}`);
  ensure(text.workspaceId === ids.primaryWorkspace && image.workspaceId === ids.primaryWorkspace, "candidate media assets or deletion intent left the primary workspace boundary");
  ensure(text.provider === context.expected.candidate.storageProvider, `active media ${ids.textMedia} provider is not canonical s3`);
  ensure(text.storageKey === context.expected.media.text.storageKey, `active media ${ids.textMedia} storage key changed`);
  ensure(text.blobState === context.expected.media.text.lifecycle.candidateBlobState, `active media ${ids.textMedia} did not migrate to Available`);
  ensure(text.deletionIntentCount === 0 && text.deletionIntentReason === null && text.deletionIntentProvider === null && text.deletionIntentStorageKey === null, `active media ${ids.textMedia} gained spurious deletion intent`);
  ensure(image.provider === context.expected.candidate.storageProvider, `deleted media ${ids.imageMedia} provider is not canonical s3`);
  ensure(image.storageKey === context.expected.media.image.storageKey, `deleted media ${ids.imageMedia} storage key changed`);
  ensure(image.blobState === context.expected.media.image.lifecycle.candidateBlobState, `deleted media ${ids.imageMedia} did not migrate to DeletePending`);
  ensure(image.deletionIntentCount === 1 && image.deletionIntentReason === context.expected.media.image.lifecycle.candidateDeletionIntentReason, `deleted media ${ids.imageMedia} lacks exactly one migration_deleted intent`);
  ensure(image.deletionIntentProvider === context.expected.candidate.storageProvider && image.deletionIntentStorageKey === context.expected.media.image.storageKey, `deleted media ${ids.imageMedia} deletion intent is not canonical`);
}

async function assertInertWebhook(context) {
  const ids = idsOf(context);
  const list = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/webhooks?page=1&pageSize=50`);
  const endpoint = itemById(asItems(list.body, "webhook list"), ids.webhook, "webhook list");
  ensure(endpoint.isActive === false && endpoint.url === "https://fixture-webhook.example.test/cmsify-upgrade-fixture", "fixture webhook is no longer inert");
  const detail = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/webhooks/${ids.webhook}`);
  ensure(detail.body?.id === ids.webhook && detail.body?.isActive === false, "fixture webhook detail changed");
  await expectSqlCount(context, "SELECT count(*) FROM webhook_endpoints WHERE id = :'webhook_id' AND NOT is_active AND NOT is_deleted AND created_at = :'created_at'::timestamptz AND updated_at = :'updated_at'::timestamptz;", {
    webhook_id: ids.webhook,
    created_at: context.expected.timestamps.webhookCreatedAt,
    updated_at: context.expected.timestamps.webhookCreatedAt,
  }, 1, "fixture webhook must remain inactive with fixed timestamps");
}

async function assertTerminalDelivery(context) {
  const ids = idsOf(context);
  const response = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/webhooks/${ids.webhook}/deliveries?page=1&pageSize=50`);
  const delivery = itemById(asItems(response.body, "webhook delivery history"), ids.webhookDelivery, "webhook delivery history");
  ensure(delivery.attemptCount === 10 && delivery.isFailed === true && delivery.isDelivered === false && delivery.nextRetryAt === null, "terminal webhook delivery changed");
  await expectSqlCount(context, "SELECT count(*) FROM webhook_delivery_logs delivery JOIN webhook_endpoints endpoint ON endpoint.id = delivery.webhook_endpoint_id WHERE delivery.id = :'delivery_id' AND endpoint.id = :'webhook_id' AND delivery.attempt_count = 10 AND delivery.is_failed AND NOT delivery.is_delivered AND delivery.next_retry_at IS NULL AND delivery.lease_expires_at IS NULL AND delivery.created_at = :'created_at'::timestamptz AND delivery.last_attempt_at = :'last_attempt_at'::timestamptz;", {
    delivery_id: ids.webhookDelivery,
    webhook_id: ids.webhook,
    created_at: context.expected.timestamps.webhookDeliveryCreatedAt,
    last_attempt_at: context.expected.timestamps.webhookDeliveryLastAttemptAt,
  }, 1, "terminal webhook delivery history and fixed timestamps changed");
}

async function webhookWorkerState(context) {
  const ids = idsOf(context);
  return scalar(context, "SELECT jsonb_build_object('endpointActive', endpoint.is_active, 'attemptCount', delivery.attempt_count, 'lastAttemptAt', delivery.last_attempt_at, 'nextRetryAt', delivery.next_retry_at, 'statusCode', delivery.status_code, 'isDelivered', delivery.is_delivered, 'isFailed', delivery.is_failed, 'leaseExpiresAt', delivery.lease_expires_at)::text FROM webhook_endpoints endpoint JOIN webhook_delivery_logs delivery ON delivery.webhook_endpoint_id = endpoint.id WHERE endpoint.id = :'webhook_id' AND delivery.id = :'delivery_id';", {
    webhook_id: ids.webhook,
    delivery_id: ids.webhookDelivery,
  });
}

async function assertWebhookStartupStable(context) {
  await expectSqlCount(context, "SELECT count(*) FROM webhook_delivery_logs delivery JOIN webhook_endpoints endpoint ON endpoint.id = delivery.webhook_endpoint_id WHERE endpoint.is_active AND NOT endpoint.is_deleted AND NOT delivery.is_delivered AND NOT delivery.is_failed AND delivery.next_retry_at IS NOT NULL;", {}, 0, "historical webhook work became eligible after startup");
  if (context.webhookWorkerStateBeforeStart !== undefined) {
    ensure(typeof context.webhookWorkerStateBeforeStart === "string" && context.webhookWorkerStateBeforeStart.length > 0, "pre-start webhook worker snapshot is missing");
    ensure(await webhookWorkerState(context) === context.webhookWorkerStateBeforeStart, "webhook worker mutated terminal fixture state during startup");
  }
}

async function assertAudit(context) {
  const ids = idsOf(context);
  const token = await adminToken(context);
  const response = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/audit?entityId=${ids.publishedContent}&page=1&pageSize=50`, { token });
  const audit = itemById(asItems(response.body, "workspace audit query"), ids.audit, "workspace audit query");
  ensure(audit.entityId === ids.publishedContent && audit.workspaceId === ids.primaryWorkspace, "audit entity/workspace relationship changed");
  ensure(audit.actor?.id === ids.adminUser && audit.changeDelta?.correlationId === "fixture-correlation-001", "audit actor/correlation relationship changed");
  await expectSqlCount(context, "SELECT count(*) FROM audit_logs audit JOIN users actor ON actor.id = audit.actor_user_id JOIN workspaces workspace ON workspace.id = audit.workspace_id JOIN content_items content ON content.id = audit.entity_id WHERE audit.id = :'audit_id' AND actor.id = :'admin_user_id' AND workspace.id = :'primary_workspace_id' AND content.id = :'published_content_id' AND audit.action = 'StatusChanged' AND audit.change_delta->>'correlationId' = 'fixture-correlation-001' AND audit.timestamp = :'audit_timestamp'::timestamptz;", {
    audit_id: ids.audit,
    admin_user_id: ids.adminUser,
    primary_workspace_id: ids.primaryWorkspace,
    published_content_id: ids.publishedContent,
    audit_timestamp: context.expected.timestamps.auditTimestamp,
  }, 1, "audit actor/workspace/content/correlation/timestamp relationship changed");
}

async function assertAuthentication(context) {
  const ids = idsOf(context);
  const response = await httpJson(context, "/api/v1/auth/me");
  ensure(response.body?.apiClientId === ids.readerClient, "fixture reader token resolved to the wrong API client");
  ensure(response.body?.role === "Reader" && response.body?.workspaceId === ids.primaryWorkspace, "fixture reader token lost least-privilege workspace scope");
  await expectSqlCount(context, "SELECT count(*) FROM api_clients WHERE id = :'reader_client_id' AND role = 'Reader' AND workspace_id = :'primary_workspace_id' AND is_active AND token_identifier = :'token_identifier' AND created_at = :'created_at'::timestamptz;", {
    reader_client_id: ids.readerClient,
    primary_workspace_id: ids.primaryWorkspace,
    token_identifier: context.expected.authentication.readerTokenIdentifier,
    created_at: context.expected.timestamps.readerClientCreatedAt,
  }, 1, "fixture reader API client metadata changed");
}

async function assertManifestBinding(context) {
  const manifest = context.fixture;
  const provenance = context.expected.provenance;
  ensure(manifest?.baseline?.version === provenance.baselineVersion, "fixture baseline version no longer matches expected provenance");
  ensure(manifest?.baseline?.sourceSha === provenance.sourceSha, "fixture source SHA no longer matches expected provenance");
  ensure(manifest?.baseline?.apiImage?.digest === provenance.apiImageDigest, "fixture API digest no longer matches expected provenance");
}

async function assertPackageProvenance(context) {
  const ids = idsOf(context);
  const provenance = context.expected.provenance;
  await expectSqlCount(context, "SELECT (SELECT count(*) FROM components WHERE id = :'component_id' AND package_namespace = :'package_namespace' AND package_id = :'package_id' AND package_version = :'package_version') + (SELECT count(*) FROM pick_lists WHERE id = :'choice_set_id' AND package_namespace = :'package_namespace' AND package_id = :'package_id' AND package_version = :'package_version') + (SELECT count(*) FROM templates WHERE id = :'template_id' AND package_namespace IS NULL AND package_id IS NULL AND package_version IS NULL);", {
    component_id: ids.component,
    choice_set_id: ids.choiceSet,
    template_id: ids.template,
    package_namespace: provenance.packageNamespace,
    package_id: provenance.packageId,
    package_version: provenance.packageVersion,
  }, 3, "component, choice-set, and template package provenance columns changed");
}

function decryptLegacyWebhook(ciphertext, keyBase64) {
  const parts = ciphertext.split(".");
  ensure(parts.length === 4 && parts[0] === "v1", "legacy webhook ciphertext format changed");
  const key = Buffer.from(keyBase64, "base64");
  const nonce = Buffer.from(parts[1], "base64");
  const tag = Buffer.from(parts[2], "base64");
  const encrypted = Buffer.from(parts[3], "base64");
  ensure(key.length === 32 && nonce.length === 12 && tag.length === 16 && encrypted.length > 0, "legacy webhook ciphertext has invalid canonical lengths");
  const decipher = createDecipheriv("aes-256-gcm", key, nonce, { authTagLength: 16 });
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(encrypted), decipher.final()]);
}

async function assertLegacyWebhookReadable(context) {
  const ids = idsOf(context);
  const ciphertext = await scalar(context, "SELECT secret FROM webhook_endpoints WHERE id = :'webhook_id';", { webhook_id: ids.webhook });
  let actualSha;
  try {
    const plaintext = decryptLegacyWebhook(ciphertext, context.expected.candidate.legacyWebhookKeyBase64);
    actualSha = createHash("sha256").update(plaintext).digest("hex");
    plaintext.fill(0);
  } catch {
    throw new Error(`legacy webhook ciphertext for endpoint ${ids.webhook} is not readable; ciphertext and plaintext were withheld`);
  }
  ensure(actualSha === context.expected.candidate.legacyWebhookSecretSha256, `legacy webhook plaintext digest mismatch for endpoint ${ids.webhook}; secret material was withheld`);
}

async function assertCandidateIdentity(context) {
  const candidate = context.candidate;
  ensure(candidate && typeof candidate === "object", "candidate identity is required");
  ensure(/^sha256:[0-9a-f]{64}$/.test(candidate.imageId), "candidate image ID is not immutable");
  ensure(typeof candidate.version === "string" && candidate.version.length > 0, "candidate version is missing");
  ensure(/^[0-9a-f]{40}$/.test(candidate.sourceSha), "candidate source SHA is not a full lowercase commit");
  candidateInformationalVersion(candidate);
  ensure(candidate.labels?.["org.opencontainers.image.version"] === candidate.version, "candidate OCI version label mismatch");
  ensure(candidate.labels?.["org.opencontainers.image.revision"] === candidate.sourceSha, "candidate OCI revision label mismatch");
}

function canarySlug(context) {
  const suffix = String(context.runId ?? "upgrade-rehearsal").toLowerCase().replace(/[^a-z0-9-]+/g, "-").replace(/^-+|-+$/g, "");
  ensure(suffix.length > 0 && suffix.length <= 64, "candidate canary run identifier is invalid");
  return `upgrade-canary-${suffix}`;
}

function canaryFields(context, title) {
  const ids = idsOf(context);
  const nullable = { boolValue: null, mediaAssetId: null, fileAssetId: null, childContentItemId: null };
  return [
    { fieldId: ids.titleField, order: 0, valueKind: "Text", textValue: title, jsonValue: null, ...nullable },
    { fieldId: ids.choiceField, order: 0, valueKind: "PickList", textValue: context.expected.content.publishedChoiceValue, jsonValue: null, ...nullable },
    { fieldId: ids.componentField, order: 0, valueKind: "Component", textValue: null, jsonValue: { summary: "Upgrade canary", accent: context.expected.content.publishedChoiceValue }, ...nullable },
  ];
}

function requiredEtag(response, operation) {
  const etag = response.headers?.get("etag");
  ensure(typeof etag === "string" && /^(?:W\/)?"[^"\r\n]+"$/.test(etag), `candidate canary ${operation} did not return a valid ETag`);
  return etag;
}

async function assertCanaryWriteRead(context) {
  const ids = idsOf(context);
  const token = await adminToken(context);
  const slug = canarySlug(context);
  const immutableBefore = await immutableHistorySnapshot(context);
  const createdFields = canaryFields(context, `Upgrade canary ${context.runId ?? "rehearsal"}`);
  const created = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content`, {
    method: "POST", token, expectedStatuses: [201],
    body: {
      templateVersionId: ids.templateVersion, slug, localeCode: "en-US", translationGroupId: null, tags: [],
      fields: createdFields,
    },
  });
  const canaryId = created.body?.id;
  ensure(typeof canaryId === "string" && /^[0-9a-f-]{36}$/i.test(canaryId), "candidate canary create did not return an ID");
  ensure(created.body?.slug === slug, "candidate canary create returned the wrong slug");
  const createEtag = requiredEtag(created, "create");
  const updatedFields = canaryFields(context, `Upgrade canary updated ${context.runId ?? "rehearsal"}`);
  const updateBody = { slug, localeCode: "en-US", translationGroupId: null, publishAt: null, tags: [], fields: updatedFields };
  await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${canaryId}`, {
    method: "PUT", token, body: updateBody, expectedStatuses: [412],
  });
  await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${canaryId}`, {
    method: "PUT", token, headers: { "if-match": '"stale-canary-etag"' }, body: updateBody, expectedStatuses: [412],
  });
  const updated = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${canaryId}`, {
    method: "PUT", token, headers: { "if-match": createEtag }, body: updateBody,
  });
  const updateEtag = requiredEtag(updated, "conditional update");
  ensure(updateEtag !== createEtag, "candidate canary conditional update did not advance the ETag");
  const read = await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${canaryId}`);
  ensure(read.body?.id === canaryId && read.body?.slug === slug, "candidate canary could not be read back through the public API");
  ensure(read.body?.fields?.some((field) => field.fieldId === ids.titleField && field.textValue === `Upgrade canary updated ${context.runId ?? "rehearsal"}`), "candidate canary read-back did not contain the conditional update");
  ensure(requiredEtag(read, "read-back") === updateEtag, "candidate canary read-back ETag did not match the conditional update");
  const immutableAfter = await immutableHistorySnapshot(context);
  ensure(JSON.stringify(immutableAfter) === JSON.stringify(immutableBefore), "candidate canary write changed existing immutable history");
  return Object.freeze({ detail: `canaryId=${canaryId}`, canaryId });
}

async function assertRollbackCanaryAbsent(context) {
  ensure(typeof context.canaryId === "string" && /^[0-9a-f-]{36}$/i.test(context.canaryId), "rollback requires the candidate canary ID");
  const ids = idsOf(context);
  await httpJson(context, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${context.canaryId}`, { expectedStatuses: [404] });
}

const COMMON_ASSERTIONS = Object.freeze([
  { name: "health-live", scenario: "provenance", category: "baseline-manifest-binding", run: assertHealthLive },
  { name: "health-ready", scenario: "provenance", category: "baseline-manifest-binding", run: assertHealthReady },
  { name: "exact-migration-history", scenario: "provenance", category: "baseline-manifest-binding", run: assertMigrationHistory },
  { name: "primary-and-restricted-exist", scenario: "workspaces", category: "primary-and-restricted-exist", run: assertWorkspaces },
  { name: "editor-primary-write-grant", scenario: "permissions", category: "editor-primary-write-grant", run: assertEditorGrant },
  { name: "reader-primary-resolve", scenario: "permissions", category: "reader-primary-resolve", run: assertReaderPrimaryResolve },
  { name: "reader-restricted-hidden", scenario: "permissions", category: "reader-restricted-hidden", run: assertReaderRestrictedHidden },
  { name: "published-template-fields", scenario: "templates", category: "published-template-fields", run: assertTemplateFields },
  { name: "inline-acyclic-snapshot", scenario: "components", category: "inline-acyclic-snapshot", run: assertComponentSnapshot },
  { name: "immutable-revisions", scenario: "choice-revisions", category: "immutable-revisions", run: assertImmutableRevisions },
  { name: "published-choice-label-snapshot", scenario: "choice-revisions", category: "published-choice-label-snapshot", run: assertPublishedChoiceLabel },
  { name: "draft-and-published-distinct", scenario: "content-versions", category: "draft-and-published-distinct", run: assertContentVersions },
  { name: "future-publish-at", scenario: "schedules", category: "future-publish-at", run: assertFuturePublishAt },
  { name: "bounded-current-effective", scenario: "schedules", category: "bounded-current-effective", run: assertBoundedCurrent },
  { name: "expired-effective-range", scenario: "schedules", category: "expired-effective-range", run: assertExpiredRange },
  { name: "expired-hidden", scenario: "schedules", category: "expired-hidden", run: assertExpiredHidden },
  { name: "available-media-download", scenario: "media", category: "available-media-download", run: assertAvailableMedia },
  { name: "historical-deleted-media-hidden", scenario: "media", category: "historical-deleted-media-hidden", run: assertDeletedMediaHidden },
  { name: "candidate-deletion-boundary", scenario: "media", category: "candidate-deletion-boundary", run: assertCandidateDeletionBoundary },
  { name: "inert-endpoint", scenario: "webhooks", category: "inert-endpoint", run: assertInertWebhook },
  { name: "terminal-delivery", scenario: "webhooks", category: "terminal-delivery", run: assertTerminalDelivery },
  { name: "startup-state-stable", scenario: "webhooks", category: "startup-state-stable", run: assertWebhookStartupStable },
  { name: "linked-mutation", scenario: "audit", category: "linked-mutation", run: assertAudit },
  { name: "fixed-reader-token", scenario: "authentication", category: "fixed-reader-token", run: assertAuthentication },
  { name: "baseline-manifest-binding", scenario: "provenance", category: "baseline-manifest-binding", run: assertManifestBinding },
  { name: "package-provenance", scenario: "provenance", category: "package-provenance", run: assertPackageProvenance },
]);

const CANDIDATE_ONLY_ASSERTIONS = Object.freeze([
  { name: "candidate-identity-provenance", scenario: "provenance", category: "baseline-manifest-binding", run: assertCandidateIdentity },
  { name: "candidate-webhook-legacy-ciphertext-readable", scenario: "webhooks", category: "startup-state-stable", run: assertLegacyWebhookReadable },
  { name: "candidate-canary-write-read", scenario: "content-versions", category: "draft-and-published-distinct", run: assertCanaryWriteRead },
]);

const ROLLBACK_EXTRA = Object.freeze({ name: "rollback-canary-absent", scenario: "content-versions", category: "draft-and-published-distinct", run: assertRollbackCanaryAbsent });

function definitionsFor(phase) {
  ensure(["baseline", "candidate", "rollback"].includes(phase), `unknown assertion phase ${phase}`);
  return phase === "candidate" ? [...COMMON_ASSERTIONS, ...CANDIDATE_ONLY_ASSERTIONS] : COMMON_ASSERTIONS;
}

export function assertionCatalog(phase) {
  return definitionsFor(phase).map(({ name, scenario, category }) => Object.freeze({ name, scenario, category }));
}

export function assertionNames(phase) {
  return definitionsFor(phase).map(({ name }) => name);
}

async function executeDefinition(definition, context) {
  try {
    const value = await definition.run(context);
    return Object.freeze({ name: definition.name, scenario: definition.scenario, status: "passed", ...(value?.detail ? { detail: value.detail } : {}), ...(value?.canaryId ? { canaryId: value.canaryId } : {}) });
  } catch (error) {
    const message = error instanceof Error ? error.message : "unknown assertion failure";
    throw new Error(`Invariant ${definition.name} failed: ${message}`);
  }
}

export async function runNamedAssertion(name, context) {
  const definition = [...COMMON_ASSERTIONS, ...CANDIDATE_ONLY_ASSERTIONS, ROLLBACK_EXTRA].find((candidate) => candidate.name === name);
  ensure(definition, `unknown named assertion ${name}`);
  return executeDefinition(definition, context);
}

async function runRegistry(context, definitions) {
  ensure(context && typeof context === "object", "an assertion context is required");
  ensure(context.expected && typeof context.expected === "object", "verified expected fixture data is required");
  ensure(typeof context.token === "string" && context.token.length > 0, "fixture token is required");
  const assertions = [];
  let canaryId;
  for (const definition of definitions) {
    const result = await executeDefinition(definition, context);
    assertions.push(result);
    if (result.canaryId) canaryId = result.canaryId;
  }
  return Object.freeze({ phase: context.phase, assertions: Object.freeze(assertions), ...(canaryId ? { canaryId } : {}) });
}

export async function assertBaseline(context) {
  const baseline = { ...context, phase: "baseline" };
  return runRegistry(baseline, COMMON_ASSERTIONS);
}

export async function assertCandidate(context) {
  const candidate = { ...context, phase: "candidate" };
  return runRegistry(candidate, [...COMMON_ASSERTIONS, ...CANDIDATE_ONLY_ASSERTIONS]);
}

export async function assertRollback(context) {
  const rollback = { ...context, phase: "rollback" };
  const report = await runRegistry(rollback, COMMON_ASSERTIONS);
  const canary = await executeDefinition(ROLLBACK_EXTRA, rollback);
  return Object.freeze({ phase: "rollback", assertions: Object.freeze([...report.assertions, canary]) });
}

export async function captureWebhookWorkerState(harness, ids) {
  return webhookWorkerState({ expected: { ids: {}, relatedIds: {} }, ids, docker: harness });
}

/** The fixture generator delegates to the exact shared baseline registry. */
export async function assertBaselineFixture({ harness, expected, ids, webhookWorkerStateBeforeStart, fixture }) {
  const manifest = fixture ?? { baseline: { version: expected.provenance.baselineVersion, sourceSha: expected.provenance.sourceSha, apiImage: { digest: expected.provenance.apiImageDigest } } };
  await assertBaseline({
    fixture: manifest, expected, ids, docker: harness, apiBaseUrl: "http://localhost:8080",
    token: expected.authentication.readerToken,
    http: createDockerHttpAdapter(harness, "baseline-api"), sql: createDockerSqlAdapter(harness), storage: dockerStorageAdapter(harness),
    webhookWorkerStateBeforeStart,
  });
  return Object.freeze({ mediaAggregateSha256: createHash("sha256").update(`${expected.media.text.sha256}\n${expected.media.image.sha256}\n`).digest("hex") });
}
