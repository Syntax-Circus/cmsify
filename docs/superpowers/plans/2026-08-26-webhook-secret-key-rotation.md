# Webhook Secret Key Rotation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace single-key webhook secret encryption with a validated versioned keyring and safely re-encrypt stored secrets online using the active key.

**Architecture:** Typed options validate an active keyring; a version-aware AES-GCM protector writes authenticated `v2` ciphertext and retains exact `v1` reads. An opt-in scoped PostgreSQL processor claims bounded locked batches, while a hosted service drives starvation-safe keyset passes and bounded metrics.

**Tech Stack:** .NET 10, AES-256-GCM, options validation, EF Core 10, PostgreSQL 17, xUnit, NSubstitute, Testcontainers, `System.Diagnostics.Metrics`.

**Spec:** `docs/superpowers/specs/2026-08-26-webhook-egress-secret-rotation-design.md`

## Global Constraints

- Write `v2.<keyId>.<nonce>.<tag>.<ciphertext>` and authenticate `v2.<keyId>` as associated data.
- Key IDs match `[A-Za-z0-9_-]{1,64}`; keyring values are canonical Base64 decoding to exactly 32 bytes.
- Production rejects checked-in development keys, fewer than 16 distinct bytes, repeated fixed-size patterns, and entropy below 3.5 bits per byte.
- Legacy v1 reads preserve the current raw-Base64-or-SHA-256 derivation exactly; the legacy key never encrypts.
- Rotation is disabled by default, bounded, idempotent, multi-replica/concurrency/starvation safe, and never retires keys.
- Never expose a public API or log/meter plaintext, ciphertext, key material, or unbounded parsed IDs.
- Follow strict red-green-refactor sequencing and commit each reviewed task.

## File map

- Create `src/Cmsify.Infrastructure/Security/SecretProtectionOptions.cs`: options and startup validator.
- Modify `src/Cmsify.Infrastructure/Security/AesSecretProtector.cs`: v2 writes and backward reads.
- Create `src/Cmsify.Infrastructure/BackgroundServices/WebhookSecretRotationProcessor.cs`: transactional batches/counts.
- Create `src/Cmsify.Infrastructure/BackgroundServices/WebhookSecretRotationService.cs`: hosted orchestration.
- Modify `CmsifyOperationalMetrics.cs` and `ServiceCollectionExtensions.cs`: instruments and DI.
- Test in `WebhookInfrastructureTests.cs`, new `WebhookSecretRotationTests.cs`, and `CmsifyOperationalMetricsTests.cs`.
- Update `appsettings.json`, environment/Compose examples, `README.md`, and `docs/operations.md`.

---

### Task 1: Strict keyring and version-aware protector

**Files:** Create `SecretProtectionOptions.cs`; modify `AesSecretProtector.cs`, `ServiceCollectionExtensions.cs`, `WebhookInfrastructureTests.cs`, and existing test key configurations.

**Interfaces:** Produces `SecretProtectionOptions`, `SecretRotationOptions`, `SecretProtectionOptionsValidator`, and `AesSecretProtector(IOptions<SecretProtectionOptions>)`; preserves `ISecretProtector`.

- [ ] **Step 1: Write failing configuration tests.** Cover missing/invalid active ID, missing active entry, malformed/noncanonical Base64, 31/33 bytes, checked-in development key in Production, fewer than 16 unique bytes, repeated 8-byte blocks, entropy below 3.5, a generated success, `BatchSize` 1–500, and `DelaySeconds` 1–3600.

```csharp
var options = new SecretProtectionOptions { ActiveKeyId = "key_2026_08" };
options.EncryptionKeys["key_2026_08"] =
    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
Assert.True(new SecretProtectionOptionsValidator(Environments.Production)
    .Validate(null, options).Succeeded);
```

- [ ] **Step 2: Write failing crypto tests.** Require five v2 segments, active ID, randomized output, round-trip, old retained-key reads after active-key change, authenticated key ID, tamper/unknown-version/unknown-key failures, and fixed v1 fixtures for both historical derivation branches.

- [ ] **Step 3: Run RED.**

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WebhookInfrastructureTests
```

Expected: FAIL because strict options and v2 do not exist.

- [ ] **Step 4: Implement these option shapes and validator.**

```csharp
public sealed class SecretProtectionOptions
{
    public const string SectionName = "Secrets";
    public string ActiveKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> EncryptionKeys { get; set; } = new(StringComparer.Ordinal);
    public string? EncryptionKey { get; set; }
    public SecretRotationOptions Rotation { get; set; } = new();
}

public sealed class SecretRotationOptions
{
    public bool Enabled { get; set; }
    public int BatchSize { get; set; } = 100;
    public int DelaySeconds { get; set; } = 5;
}
```

Give the validator an environment-name constructor for tests and `IHostEnvironment` DI constructor. Re-encode decoded keys to prove canonical form. Never include configured values in validation messages.

- [ ] **Step 5: Implement v2 and exact v1 compatibility.** Parse exactly four v1 or five v2 segments; require 12-byte nonce/16-byte tag/canonical Base64. Use AES-GCM associated data `Encoding.UTF8.GetBytes($"v2.{keyId}")`. For v1 only, use exact 32-byte Base64 directly or SHA-256 hash the UTF-8 legacy string. Select only the named retained v2 key; use the active key for every `Protect`.

- [ ] **Step 6: Bind `Secrets`, register validator with `ValidateOnStart`, and construct the protector from typed options.** Update ordinary tests to an active ID plus deterministic valid Base64; leave `EncryptionKey` only in explicit legacy tests.

- [ ] **Step 7: Run GREEN and compile API.** Run Step 3, then `dotnet build src/Cmsify.Api/Cmsify.Api.csproj --configuration Release --no-restore`.

- [ ] **Step 8: Commit.** Stage options, protector, DI, and affected tests; commit `Add versioned webhook secret keys`.

### Task 2: Bounded PostgreSQL rotation processor

**Files:** Create `WebhookSecretRotationProcessor.cs` and `WebhookSecretRotationTests.cs`.

**Interfaces:** Produces `RotateBatchAsync(Guid? afterId, CancellationToken)` and `CountRemainingAsync(CancellationToken)` with:

```csharp
public sealed record SecretRotationBatchResult(
    Guid? NextCursor, int Selected, int Rotated, int Skipped, int Failed, bool ReachedEnd);
public sealed record SecretCiphertextCount(string Version, string KeyId, long Count);
```

- [ ] **Step 1: Write failing PostgreSQL batch tests.** Migrate a PostgreSQL 17 Testcontainer; seed v1, old-v2, active-v2, and soft-deleted endpoints. With batch size 2, require at most two selections, active rows untouched, soft-deleted material rotated, repeated passes plaintext-stable, restart-idempotent, and final old count zero.

- [ ] **Step 2: Write failing concurrency/starvation tests.** Release two processors together and require disjoint locked IDs. Race a signing-secret update and require the final plaintext be the new secret. Put an undecryptable low-ID row before valid rows and require the keyset cursor to advance so valid rows rotate before the failure is reconsidered.

- [ ] **Step 3: Run RED.**

```powershell
dotnet test tests/Cmsify.Infrastructure.Tests/Cmsify.Infrastructure.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WebhookSecretRotationTests
```

Expected: FAIL because processor/result types are absent.

- [ ] **Step 4: Implement the short transaction.** Select `id > afterId` and ciphertext not beginning with the exact active `v2.<keyId>.` prefix, order by ID, `FOR UPDATE SKIP LOCKED`, limit by validated batch size. Parameterize every value and escape LIKE metacharacters or use a translated `StartsWith`; never concatenate key material into SQL.

- [ ] **Step 5: Implement conditional rewrap.** For each row retain the original ciphertext, decrypt, encrypt active v2, then update only where ID and ciphertext still match. Count one affected row as rotated and zero as skipped. Per-row crypto/format errors increment failed and continue; database/cancellation errors roll back. `NextCursor` is the greatest selected ID even on failure; `ReachedEnd` means fewer rows than the limit, not zero rotations.

- [ ] **Step 6: Implement database-derived grouped counts.** Return only configured IDs plus bounded `legacy`/`unknown` categories; query counts rather than loading ciphertexts.

- [ ] **Step 7: Run GREEN.** Run Step 3 and require bounded, restart, replica, concurrent-update, failure, starvation, and count cases to pass.

- [ ] **Step 8: Commit.** Stage processor and tests; commit `Rotate webhook secrets in bounded batches`.

### Task 3: Opt-in orchestration and bounded observability

**Files:** Create `WebhookSecretRotationService.cs`; modify metrics, DI, rotation tests, and metric tests.

- [ ] **Step 1: Write failing lifecycle tests.** Disabled configuration creates no scope; enabled mode advances cursor; end-of-pass refreshes counts, delays, then resets; cancellation stops promptly; unexpected failures log/measure a category and delay rather than spin.

- [ ] **Step 2: Write failing instruments tests.** Require:

```text
cmsify.webhook.secret.decrypt_failures   version,key_id,reason
cmsify.webhook.secret.rotation.rows      outcome
cmsify.webhook.secret.rotation.cycles    outcome
cmsify.webhook.secret.rotation.duration  no tags
cmsify.webhook.secret.rotation.remaining version,key_id
```

Only configured IDs may be labels; invalid/unconfigured input maps to `unknown`; endpoint/workspace/ciphertext labels are prohibited.

- [ ] **Step 3: Run RED.** Filter Infrastructure tests to `WebhookSecretRotationTests|CmsifyOperationalMetricsTests`; expect missing service/instruments.

- [ ] **Step 4: Implement `WebhookSecretRotationService : BackgroundService`.** Return immediately when disabled. Otherwise create one scope per batch, call the processor with the current cursor, report results, refresh counts at pass end, delay `DelaySeconds`, and reset. Cancellation exits; unexpected exceptions record/log a bounded reason and use the same delay.

- [ ] **Step 5: Implement metrics and DI.** Store remaining counts in an immutable snapshot read by an observable gauge. Register processor scoped and hosted service through an explicit singleton factory matching existing workers. Add no controller or endpoint.

- [ ] **Step 6: Run GREEN.** Run the focused filter, then all Infrastructure tests Release/`--no-restore`; require PostgreSQL cases to pass.

- [ ] **Step 7: Commit.** Stage hosted service, metrics, DI, and tests; commit `Operate webhook key rotation safely`.

### Task 4: Configuration, runbook, and complete Task 7 gate

**Files:** Modify `src/Cmsify.Api/appsettings.json`, `.env.example`, `src/Cmsify.Api/.env.example`, `docker-compose.prod.env.example`, `docker-compose.prod.yml`, `README.md`, and `docs/operations.md`.

- [ ] **Step 1: Establish docs/config RED.** Search for `ActiveKeyId`, `EncryptionKeys`, `Rotation__Enabled`, CSPRNG generation, remaining-count verification, and v2 rollback restriction; record their absence.

- [ ] **Step 2: Add keyring examples.** Use a canonical development-only 32-byte key with ID `development`; retain `Secrets__EncryptionKey` only as a commented migration input. Add rotation defaults `Enabled=false`, `BatchSize=100`, `DelaySeconds=5`. Production Compose requires an active ID/key through environment substitution without committing material.

- [ ] **Step 3: Add generation and enable-observe-disable runbook.** Include:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Require reader-first deploy, rotation initially off, one enabled window, decrypt/row/remaining monitoring, investigation of every failure, zero on an independent pass, disablement, and later key retirement. State that any rollback after v2 writes needs a v2-capable binary and every referenced key.

- [ ] **Step 4: Verify docs/config and whitespace.** Run `rg -n "ActiveKeyId|EncryptionKeys|Rotation__Enabled|RandomNumberGenerator|remaining|v2-capable"` across all seven files, then `git diff --check`.

- [ ] **Step 5: Run complete Task 7 validation serially.** Run API Release build; Core, Infrastructure, API integration, Admin integration, and .NET client Release tests with `--no-restore`; then TypeScript `generate:check`, `typecheck`, tests, and build. Require no unexplained OpenAPI/generated diff. Run one heavy process at a time and remove only exact `sdk/typescript/dist` output after verification.

- [ ] **Step 6: Request independent Task 7 spec and quality review.** Compare both implementation plans and commits with the approved spec and F-08/F-18. Record genuine RED evidence, fixes, and final checks in `.superpowers/sdd/2026-08-24-v1-remediation/task-7-report.md`; update the ledger only after PASS.

- [ ] **Step 7: Commit.** Stage configuration and documentation; commit `Document webhook key rotation operations`.
