import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const sdkRoot = resolve(import.meta.dirname, "..");

describe("published package consumer", () => {
  it("packs, installs, and typechecks a clean consumer through only public exports", () => {
    const output = execFileSync(process.execPath, ["scripts/check-clean-consumer.mjs"], { cwd: sdkRoot, encoding: "utf8" });

    expect(output).toContain("Clean consumer typecheck passed.");
  }, 30_000);

  it("fails closed when release validation supplies a missing packed archive", () => {
    const missingTarball = resolve(sdkRoot, "missing-release-candidate.tgz");

    expect(() => execFileSync(process.execPath, ["scripts/check-clean-consumer.mjs"], {
      cwd: sdkRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      env: { ...process.env, CMSIFY_CLIENT_TARBALL: missingTarball },
    })).toThrow(/CMSIFY_CLIENT_TARBALL/);
  });
});
