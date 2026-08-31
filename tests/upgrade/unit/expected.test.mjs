import assert from "node:assert/strict";
import { resolve } from "node:path";
import test from "node:test";

import { validateExpectedData } from "../../../eng/upgrade-tests/expected.mjs";
import { validExpectedDocument, validManifestDocument } from "./fixture-documents.mjs";

const fixtureDirectory = resolve(process.cwd(), "tests", "upgrade", "fixtures", "v0.1.3");

test("accepts the exact expected-data contract and manifest binding", () => {
  const expected = validateExpectedData(validExpectedDocument(), validManifestDocument(), fixtureDirectory);

  assert.equal(expected.content.currentEffectiveStartAt, "2026-08-19T12:00:00.000000Z");
  assert.equal(expected.media.image.lifecycle.candidateBlobState, "DeletePending");
  assert.equal(expected.candidate.migrations.length, 14);
  assert.equal(expected.timestamps.webhookDeliveryLastAttemptAt, "2026-08-20T12:15:00.000000Z");
  assert.equal(Object.isFrozen(expected.media.image.lifecycle), true);
});

test("rejects expected provenance that contradicts the manifest", () => {
  const expected = validExpectedDocument();
  expected.provenance.sourceSha = "0".repeat(40);

  assert.throws(
    () => validateExpectedData(expected, validManifestDocument(), fixtureDirectory),
    /provenance\.sourceSha.*manifest/i,
  );
});

test("rejects a missing required assertion category", () => {
  const expected = validExpectedDocument();
  expected.scenarios.find((scenario) => scenario.id === "media").assertions.pop();

  assert.throws(
    () => validateExpectedData(expected, validManifestDocument(), fixtureDirectory),
    /media.*assertion categor/i,
  );
});

test("rejects an unknown assertion category", () => {
  const expected = validExpectedDocument();
  expected.scenarios.find((scenario) => scenario.id === "webhooks").assertions.push("worker-probably-idle");

  assert.throws(
    () => validateExpectedData(expected, validManifestDocument(), fixtureDirectory),
    /webhooks.*assertion categor/i,
  );
});

test("rejects a current range that does not contain fixtureClock", () => {
  const expected = validExpectedDocument();
  expected.content.currentEffectiveEndAt = "2026-08-20T11:59:59.000000Z";

  assert.throws(
    () => validateExpectedData(expected, validManifestDocument(), fixtureDirectory),
    /current effective range.*fixtureClock/i,
  );
});

test("rejects media without the historical deleted-to-candidate boundary", () => {
  const expected = validExpectedDocument();
  expected.media.image.lifecycle.candidateBlobState = "Available";

  assert.throws(
    () => validateExpectedData(expected, validManifestDocument(), fixtureDirectory),
    /deleted media.*DeletePending/i,
  );
});

test("rejects an incomplete candidate migration boundary", () => {
  const expected = validExpectedDocument();
  expected.candidate.migrations.pop();

  assert.throws(
    () => validateExpectedData(expected, validManifestDocument(), fixtureDirectory),
    /candidate\.migrations.*exact.*14/i,
  );
});

test("rejects malformed legacy webhook readability material", () => {
  const expected = validExpectedDocument();
  expected.candidate.legacyWebhookSecretSha256 = "not-a-digest";

  assert.throws(
    () => validateExpectedData(expected, validManifestDocument(), fixtureDirectory),
    /legacyWebhookSecretSha256.*SHA-256/i,
  );
});
