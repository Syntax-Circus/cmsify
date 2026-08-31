import { execFileSync } from "node:child_process";
import { cpSync, linkSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { afterEach, describe, expect, it } from "vitest";

const repositoryRoot = resolve(import.meta.dirname, "../../..");
const sdkRoot = resolve(repositoryRoot, "sdk/typescript");
const temporaryDirectories: string[] = [];

afterEach(() => {
  for (const directory of temporaryDirectories.splice(0)) {
    rmSync(directory, { recursive: true, force: true });
  }
});

function createFixture() {
  const directory = mkdtempSync(resolve(tmpdir(), "cmsify-openapi-test-"));
  temporaryDirectories.push(directory);
  const snapshot = resolve(directory, "openapi.snapshot.json");
  const generated = resolve(directory, "generated");
  cpSync(resolve(sdkRoot, "openapi.snapshot.json"), snapshot);
  cpSync(resolve(sdkRoot, "src/generated"), generated, { recursive: true });
  return { directory, snapshot, generated };
}

function check(liveDocument: string, snapshot: string, generated: string) {
  try {
    execFileSync(process.execPath, [
      resolve(repositoryRoot, "scripts/openapi.mjs"),
      "check",
      "--live-document", liveDocument,
      "--snapshot", snapshot,
      "--generated-dir", generated,
    ], { cwd: repositoryRoot, encoding: "utf8", stdio: "pipe" });
    return { status: 0, output: "" };
  } catch (error) {
    const result = error as { status?: number; stdout?: string; stderr?: string };
    return { status: result.status, output: `${result.stdout ?? ""}${result.stderr ?? ""}` };
  }
}

function update(liveDocument: string, snapshot: string, generated: string) {
  try {
    execFileSync(process.execPath, [
      resolve(repositoryRoot, "scripts/openapi.mjs"),
      "update",
      "--live-document", liveDocument,
      "--snapshot", snapshot,
      "--generated-dir", generated,
    ], { cwd: repositoryRoot, encoding: "utf8", stdio: "pipe" });
    return 0;
  } catch (error) {
    return (error as { status?: number }).status;
  }
}

function updateDefaultOutputs(liveDocument: string) {
  try {
    execFileSync(process.execPath, [
      resolve(repositoryRoot, "scripts/openapi.mjs"),
      "update",
      "--live-document", liveDocument,
    ], { cwd: repositoryRoot, encoding: "utf8", stdio: "pipe" });
    return { status: 0, output: "" };
  } catch (error) {
    const result = error as { status?: number; stdout?: string; stderr?: string };
    return { status: result.status, output: `${result.stdout ?? ""}${result.stderr ?? ""}` };
  }
}

describe("OpenAPI workflow", () => {
  it("preserves required non-null contracts and bearer security from the live API", () => {
    const document = JSON.parse(readFileSync(resolve(sdkRoot, "openapi.snapshot.json"), "utf8"));
    const packageImportResponse = document.components.schemas.PackageImportResponse;

    expect(packageImportResponse.required).toContain("pickLists");
    expect(packageImportResponse.properties.pickLists.nullable).not.toBe(true);
    expect(document.security).toContainEqual({ Bearer: [] });
  });

  it("marks anonymous login as exempt from the global bearer requirement", () => {
    const document = JSON.parse(readFileSync(resolve(sdkRoot, "openapi.snapshot.json"), "utf8"));

    expect(document.paths["/api/v1/auth/login"].post.security).toEqual([]);
  });

  it("reports live and generated drift without changing tracked artifacts", () => {
    const { directory, snapshot, generated } = createFixture();
    const live = resolve(directory, "live.json");
    const expectedSnapshot = readFileSync(snapshot, "utf8");
    const client = resolve(generated, "client.ts");
    const expectedClient = readFileSync(client, "utf8");

    writeFileSync(live, expectedSnapshot.replace("Cmsify API", "Cmsify API drift"));
    writeFileSync(client, "export const stale = true;\n");

    const result = check(live, snapshot, generated);

    expect(result.status).not.toBe(0);
    expect(result.output).toContain("Live OpenAPI differs from the checked-in snapshot.");
    expect(result.output).toContain("Generated TypeScript output differs from tracked output.");
    expect(readFileSync(snapshot, "utf8")).toBe(expectedSnapshot);
    expect(readFileSync(client, "utf8")).toBe("export const stale = true;\n");
    expect(expectedClient).not.toBe("export const stale = true;\n");
  });

  it("does not update tracked artifacts when generation fails", () => {
    const { directory, snapshot, generated } = createFixture();
    const live = resolve(directory, "invalid.json");
    const expectedSnapshot = readFileSync(snapshot, "utf8");
    const schema = resolve(generated, "schema.ts");
    const expectedSchema = readFileSync(schema, "utf8");
    writeFileSync(live, "{");

    expect(update(live, snapshot, generated)).not.toBe(0);
    expect(readFileSync(snapshot, "utf8")).toBe(expectedSnapshot);
    expect(readFileSync(schema, "utf8")).toBe(expectedSchema);
  });

  it("refuses arbitrary live documents when update targets tracked artifacts", () => {
    const directory = mkdtempSync(resolve(tmpdir(), "cmsify-openapi-authority-"));
    temporaryDirectories.push(directory);
    const live = resolve(directory, "untrusted.json");
    const snapshot = resolve(sdkRoot, "openapi.snapshot.json");
    const schema = resolve(sdkRoot, "src/generated/schema.ts");
    const expectedSnapshot = readFileSync(snapshot, "utf8");
    const expectedSchema = readFileSync(schema, "utf8");
    writeFileSync(live, "{");

    const result = updateDefaultOutputs(live);

    expect(result.status).not.toBe(0);
    expect(result.output).toContain("--live-document is only allowed when both --snapshot and --generated-dir target test fixtures.");
    expect(readFileSync(snapshot, "utf8")).toBe(expectedSnapshot);
    expect(readFileSync(schema, "utf8")).toBe(expectedSchema);
  });

  it("refuses tracked artifacts addressed through a filesystem alias", () => {
    const directory = mkdtempSync(resolve(tmpdir(), "cmsify-openapi-alias-"));
    temporaryDirectories.push(directory);
    const alias = resolve(directory, "sdk-alias");
    const snapshot = resolve(sdkRoot, "openapi.snapshot.json");
    const schema = resolve(sdkRoot, "src/generated/schema.ts");
    const expectedSnapshot = readFileSync(snapshot, "utf8");
    const expectedSchema = readFileSync(schema, "utf8");
    symlinkSync(sdkRoot, alias, process.platform === "win32" ? "junction" : "dir");

    expect(update(snapshot, resolve(alias, "openapi.snapshot.json"), resolve(alias, "src/generated"))).not.toBe(0);
    expect(readFileSync(snapshot, "utf8")).toBe(expectedSnapshot);
    expect(readFileSync(schema, "utf8")).toBe(expectedSchema);
  });

  it("refuses tracked snapshots addressed through a hard link", () => {
    const { generated } = createFixture();
    const directory = mkdtempSync(resolve(sdkRoot, ".openapi-hardlink-"));
    temporaryDirectories.push(directory);
    const snapshot = resolve(sdkRoot, "openapi.snapshot.json");
    const schema = resolve(sdkRoot, "src/generated/schema.ts");
    const alias = resolve(directory, "snapshot.json");
    const expectedSnapshot = readFileSync(snapshot, "utf8");
    const expectedSchema = readFileSync(schema, "utf8");
    linkSync(snapshot, alias);

    expect(update(snapshot, alias, generated)).not.toBe(0);
    expect(readFileSync(snapshot, "utf8")).toBe(expectedSnapshot);
    expect(readFileSync(schema, "utf8")).toBe(expectedSchema);
  });
});
