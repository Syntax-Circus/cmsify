import { createHash } from "node:crypto";

function assert(condition, message) {
  if (!condition) throw new Error(`Baseline assertion failed: ${message}`);
}

async function sql(harness, statement) {
  const result = await harness.exec("postgres", [
    "psql", "--username", "cmsify", "--dbname", "cmsify",
    "--no-psqlrc", "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1",
    "--command", statement,
  ]);
  return result.stdout.trim();
}

async function status(harness, path, token) {
  const result = await harness.exec("baseline-api", [
    "curl", "--silent", "--show-error", "--output", "/dev/null",
    "--write-out", "%{http_code}",
    "--header", `Authorization: Bearer ${token}`,
    `http://localhost:8080${path}`,
  ]);
  return result.stdout.trim();
}

async function downloadedSha256(harness, path, token, destination) {
  await harness.exec("baseline-api", [
    "curl", "--silent", "--show-error", "--fail-with-body",
    "--output", destination,
    "--header", `Authorization: Bearer ${token}`,
    `http://localhost:8080${path}`,
  ]);
  const result = await harness.exec("baseline-api", ["sha256sum", destination]);
  return result.stdout.trim().split(/\s+/, 1)[0];
}

/**
 * Validates the published baseline before fixture export or after a rollback restore.
 * Task 4 expands this registry for candidate-only assertions while reusing this entry point.
 * @param {{harness:ReturnType<import('./docker.mjs').createDockerHarness>,expected:object,ids:Record<string,string>}} input
 * @returns {Promise<{mediaAggregateSha256:string}>}
 */
export async function assertBaselineFixture({ harness, expected, ids }) {
  assert(harness && typeof harness.exec === "function", "a Docker harness is required");
  assert(expected && Array.isArray(expected.migrations), "expected migration identities are required");
  assert(ids && typeof ids === "object", "fixture IDs are required");

  const migrations = (await sql(harness, 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";')).split(/\r?\n/).filter(Boolean);
  assert(JSON.stringify(migrations) === JSON.stringify([...expected.migrations].sort()), "the migration history is not the exact published 11-migration baseline");

  assert(await sql(harness, `SELECT count(*) FROM workspaces WHERE id IN ('${ids.primaryWorkspace}', '${ids.restrictedWorkspace}');`) === "2", "both synthetic workspaces must exist");
  assert(await sql(harness, `SELECT count(*) FROM user_workspace_accesses WHERE user_id = '${ids.editorUser}' AND workspace_id = '${ids.primaryWorkspace}' AND access_level = 'Write';`) === "1", "the editor write grant must remain linked");
  assert(await sql(harness, `SELECT count(*) FROM pick_list_revisions WHERE pick_list_id = '${ids.choiceSet}';`) === "2", "both immutable choice revisions must exist");
  assert(await sql(harness, `SELECT count(*) FROM template_fields WHERE template_version_id = '${ids.templateVersion}' AND component_id = '${ids.component}';`) === "1", "the template must retain its inline component field");
  assert(await sql(harness, `SELECT count(*) FROM component_fields WHERE component_version_id = '${ids.componentVersion}' AND nested_component_id IS NOT NULL;`) === "0", "the component graph must remain acyclic");
  assert(await sql(harness, `SELECT display_label FROM content_version_field_values WHERE content_version_id = '${ids.publishedVersion}' AND field_id = '${ids.choiceField}';`) === expected.content.publishedChoiceLabel, "published content must retain the original choice label snapshot");
  assert(await sql(harness, `SELECT count(*) FROM content_items WHERE id = '${ids.scheduledContent}' AND status = 'Approved' AND publish_at = '${expected.content.scheduledPublishAt}'::timestamptz;`) === "1", "future PublishAt state must remain scheduled");
  assert(await sql(harness, `SELECT count(*) FROM content_versions WHERE id = '${ids.expiredVersion}' AND effective_end_at = '${expected.content.expiredEffectiveEndAt}'::timestamptz;`) === "1", "the expired effective range must remain intact");
  assert(await sql(harness, `SELECT count(*) FROM webhook_delivery_logs WHERE id = '${ids.webhookDelivery}' AND is_failed AND NOT is_delivered AND next_retry_at IS NULL;`) === "1", "the webhook history must remain non-retriable");
  assert(await sql(harness, `SELECT count(*) FROM audit_logs WHERE id = '${ids.audit}' AND actor_user_id = '${ids.adminUser}' AND workspace_id = '${ids.primaryWorkspace}' AND entity_id = '${ids.publishedContent}' AND change_delta->>'correlationId' = 'fixture-correlation-001';`) === "1", "the audit relationship and correlation must remain intact");
  assert(await sql(harness, `SELECT count(*) FROM components WHERE id = '${ids.component}' AND package_namespace = '${expected.provenance.packageNamespace}' AND package_id = '${expected.provenance.packageId}' AND package_version = '${expected.provenance.packageVersion}';`) === "1", "package provenance must remain attached to the reusable component");

  const token = expected.authentication.readerToken;
  assert(await status(harness, "/api/v1/auth/me", token) === "200", "the fixed reader token must authenticate");
  assert(await status(harness, `/api/v1/workspaces/${ids.restrictedWorkspace}`, token) === "404", "restricted workspace access must remain concealed as 404");
  assert(await status(harness, `/api/v1/workspaces/${ids.primaryWorkspace}/content/${ids.publishedContent}?resolve=true&asOf=${encodeURIComponent(expected.fixtureClock)}`, token) === "200", "published content must resolve through the historical API");

  const textDigest = await downloadedSha256(harness, `/api/v1/workspaces/${ids.primaryWorkspace}/media/${ids.textMedia}/file`, token, "/tmp/cmsify-fixture-text");
  const imageDigest = await downloadedSha256(harness, `/api/v1/workspaces/${ids.primaryWorkspace}/media/${ids.imageMedia}/file`, token, "/tmp/cmsify-fixture-image");
  assert(textDigest === expected.media.text.sha256, "text media bytes must match the fixture hash");
  assert(imageDigest === expected.media.image.sha256, "PNG media bytes must match the fixture hash");

  const expectedAggregate = createHash("sha256")
    .update(`${expected.media.text.sha256}\n${expected.media.image.sha256}\n`)
    .digest("hex");
  return Object.freeze({ mediaAggregateSha256: expectedAggregate });
}
