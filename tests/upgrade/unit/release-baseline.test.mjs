import assert from "node:assert/strict";
import test from "node:test";

import { main } from "../../../eng/upgrade-tests/cli.mjs";
import {
  fetchDockerLinuxAmd64Descriptor,
  fetchPublishedStable01Releases,
  selectLatestPublishedStable01,
  verifyReleaseBaseline,
} from "../../../eng/upgrade-tests/release-baseline.mjs";

const baselineSha = "bc652aec1acad7ef440576b5019a0fe7c72004b3";
const nextSha = "0123456789abcdef0123456789abcdef01234567";
const baselineDigest = "sha256:e28a7c884ed4cc4933fbb58608ba8d1dd97bf6a1e443ef234e0a0aa8b5c51931";
const nextDigest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

function published(version, options = {}) {
  return {
    version,
    tag: options.tag ?? `v${version}`,
    sourceSha: options.sourceSha ?? (version === "0.1.3" ? baselineSha : nextSha),
    publishedAt: options.publishedAt ?? `2026-08-${String(10 + Number(version.split(".").at(-1))).padStart(2, "0")}T12:00:00Z`,
  };
}

function manifest(version, options = {}) {
  return {
    baseline: {
      version,
      sourceSha: options.sourceSha ?? (version === "0.1.3" ? baselineSha : nextSha),
      apiImage: {
        repository: "docker.io/syntaxcircus/cmsify-api",
        tag: version,
        digest: options.digest ?? (version === "0.1.3" ? baselineDigest : nextDigest),
        platform: "linux/amd64",
      },
    },
  };
}

function descriptorFor(version, options = {}) {
  return {
    repository: "docker.io/syntaxcircus/cmsify-api",
    tag: version,
    digest: options.digest ?? (version === "0.1.3" ? baselineDigest : nextDigest),
    platform: options.platform ?? "linux/amd64",
  };
}

function response(body, status = 200, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", ...headers },
  });
}

test("selects only stable published 0.1.x releases older than the candidate", () => {
  const selected = selectLatestPublishedStable01([
    published("0.1.2"),
    published("0.1.3-rc.1"),
    published("0.2.0"),
    published("0.1.3"),
    published("0.1.4"),
  ], "0.1.4");

  assert.equal(selected.version, "0.1.3");
  assert.equal(selected.tag, "v0.1.3");
});

test("certifies 0.1.4 from the latest already-published 0.1.3 baseline", () => {
  const result = verifyReleaseBaseline({
    candidateVersion: "0.1.4",
    fixtureManifest: manifest("0.1.3"),
    githubReleases: [published("0.1.3"), published("0.1.4")],
    dockerDescriptor: descriptorFor("0.1.3"),
  });

  assert.equal(result.baselineVersion, "0.1.3");
  assert.equal(result.sourceSha, baselineSha);
  assert.equal(result.apiDigest, baselineDigest);
  assert.equal(Number.isNaN(Date.parse(result.verifiedAt)), false);
});

test("rejects stale fixture after 0.1.4 is published", () => {
  assert.throws(() => verifyReleaseBaseline({
    candidateVersion: "0.1.5",
    fixtureManifest: manifest("0.1.3"),
    githubReleases: [published("0.1.3"), published("0.1.4")],
    dockerDescriptor: descriptorFor("0.1.4"),
  }), /fixture records 0\.1\.3 but latest published baseline is 0\.1\.4/i);
});

test("certifies v1 only from the latest published stable 0.1.x release", () => {
  const result = verifyReleaseBaseline({
    candidateVersion: "1.0.0",
    fixtureManifest: manifest("0.1.4"),
    githubReleases: [published("0.1.3"), published("0.1.4"), published("1.0.0-rc.1")],
    dockerDescriptor: descriptorFor("0.1.4"),
  });

  assert.equal(result.baselineVersion, "0.1.4");
  assert.equal(result.sourceSha, nextSha);
  assert.equal(result.apiDigest, nextDigest);
});

test("rejects GitHub and Docker digest disagreement", () => {
  assert.throws(() => verifyReleaseBaseline({
    candidateVersion: "1.0.0",
    fixtureManifest: manifest("0.1.3"),
    githubReleases: [published("0.1.3")],
    dockerDescriptor: descriptorFor("0.1.3", { digest: nextDigest }),
  }), /Docker.*digest.*does not match.*fixture/i);
});

test("rejects GitHub and fixture source disagreement", () => {
  assert.throws(() => verifyReleaseBaseline({
    candidateVersion: "1.0.0",
    fixtureManifest: manifest("0.1.3"),
    githubReleases: [published("0.1.3", { sourceSha: nextSha })],
    dockerDescriptor: descriptorFor("0.1.3"),
  }), /GitHub.*source.*does not match.*fixture/i);
});

test("fails closed when no published stable 0.1.x baseline exists", () => {
  assert.throws(
    () => selectLatestPublishedStable01([published("0.1.3-rc.1"), published("0.2.0")], "1.0.0"),
    /no published stable 0\.1\.x baseline/i,
  );
});

test("rejects malformed or inconsistent Docker descriptors", async (context) => {
  for (const [name, descriptor] of [
    ["missing digest", { ...descriptorFor("0.1.3"), digest: undefined }],
    ["uppercase digest", { ...descriptorFor("0.1.3"), digest: baselineDigest.toUpperCase() }],
    ["wrong tag", { ...descriptorFor("0.1.3"), tag: "latest" }],
    ["wrong platform", descriptorFor("0.1.3", { platform: "linux/arm64" })],
  ]) {
    await context.test(name, () => {
      assert.throws(() => verifyReleaseBaseline({
        candidateVersion: "1.0.0",
        fixtureManifest: manifest("0.1.3"),
        githubReleases: [published("0.1.3")],
        dockerDescriptor: descriptor,
      }), /Docker.*(?:descriptor|tag|platform|digest)/i);
    });
  }
});

test("discovers non-draft stable releases and peels annotated Git tags to commits", async () => {
  const annotatedTagSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
  const fetchImpl = async (url, options) => {
    assert.equal(options.signal instanceof AbortSignal, true);
    assert.equal(options.headers.authorization, "Bearer test-token");
    if (url.endsWith("/releases?per_page=100&page=1")) {
      return response([
        { tag_name: "v0.1.3", draft: false, prerelease: false, published_at: "2026-08-13T12:00:00Z" },
        { tag_name: "v0.1.4-rc.1", draft: false, prerelease: true, published_at: "2026-08-14T12:00:00Z" },
        { tag_name: "v0.1.2", draft: true, prerelease: false, published_at: "2026-08-12T12:00:00Z" },
      ]);
    }
    if (url.endsWith("/git/ref/tags/v0.1.3")) return response({ object: { type: "tag", sha: annotatedTagSha } });
    if (url.endsWith(`/git/tags/${annotatedTagSha}`)) return response({ object: { type: "commit", sha: baselineSha } });
    throw new Error(`Unexpected URL: ${url}`);
  };

  const releases = await fetchPublishedStable01Releases({ fetchImpl, githubToken: "test-token" });

  assert.deepEqual(releases, [published("0.1.3")]);
});

test("rejects GitHub rate limiting and every non-200 response", async (context) => {
  await context.test("rate limit", async () => {
    await assert.rejects(
      () => fetchPublishedStable01Releases({
        fetchImpl: async () => response({ message: "rate limited" }, 403, { "x-ratelimit-remaining": "0" }),
      }),
      /GitHub.*rate limit/i,
    );
  });
  await context.test("unexpected success status", async () => {
    await assert.rejects(
      () => fetchPublishedStable01Releases({ fetchImpl: async () => response([], 201) }),
      /GitHub.*HTTP 201/i,
    );
  });
  await context.test("server error", async () => {
    await assert.rejects(
      () => fetchPublishedStable01Releases({ fetchImpl: async () => response({ message: "failure" }, 500) }),
      /GitHub.*HTTP 500/i,
    );
  });
});

test("selects the Docker Registry linux/amd64 child descriptor", async () => {
  const armDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
  const fetchImpl = async (url, options) => {
    assert.equal(options.signal instanceof AbortSignal, true);
    if (url.startsWith("https://auth.docker.io/token?")) return response({ token: "registry-token" });
    assert.equal(options.headers.authorization, "Bearer registry-token");
    return response({
      schemaVersion: 2,
      mediaType: "application/vnd.oci.image.index.v1+json",
      manifests: [
        { mediaType: "application/vnd.oci.image.manifest.v1+json", size: 100, digest: armDigest, platform: { os: "linux", architecture: "arm64" } },
        { mediaType: "application/vnd.oci.image.manifest.v1+json", size: 200, digest: baselineDigest, platform: { os: "linux", architecture: "amd64" } },
      ],
    });
  };

  const descriptor = await fetchDockerLinuxAmd64Descriptor({
    repository: "docker.io/syntaxcircus/cmsify-api",
    tag: "0.1.3",
    fetchImpl,
  });

  assert.deepEqual(descriptor, descriptorFor("0.1.3"));
});

test("rejects registry errors and malformed linux/amd64 child descriptors", async (context) => {
  await context.test("token endpoint error", async () => {
    await assert.rejects(
      () => fetchDockerLinuxAmd64Descriptor({
        repository: "docker.io/syntaxcircus/cmsify-api",
        tag: "0.1.3",
        fetchImpl: async () => response({ message: "slow down" }, 429),
      }),
      /Docker Registry token.*HTTP 429/i,
    );
  });
  await context.test("malformed platform descriptor", async () => {
    let request = 0;
    await assert.rejects(
      () => fetchDockerLinuxAmd64Descriptor({
        repository: "docker.io/syntaxcircus/cmsify-api",
        tag: "0.1.3",
        fetchImpl: async () => {
          request += 1;
          if (request === 1) return response({ token: "registry-token" });
          return response({
            schemaVersion: 2,
            mediaType: "application/vnd.oci.image.index.v1+json",
            manifests: [{ digest: baselineDigest, size: 0, platform: { os: "linux", architecture: "amd64" } }],
          });
        },
      }),
      /Docker Registry.*descriptor.*malformed/i,
    );
  });
});

test("verify-release-baseline CLI verifies the configured fixture without exposing its GitHub token", async () => {
  let observedToken;
  let stdout = "";
  let stderr = "";
  const exitCode = await main([
    "verify-release-baseline",
    "--fixture", "tests/upgrade/fixtures/v0.1.3",
    "--candidate-version", "1.0.0",
    "--github-token-env", "TEST_GITHUB_TOKEN",
  ], {
    cwd: process.cwd(),
    env: { TEST_GITHUB_TOKEN: "github-test-token" },
    stdout: { write: (value) => { stdout += value; } },
    stderr: { write: (value) => { stderr += value; } },
    fetchPublishedStable01Releases: async ({ githubToken }) => {
      observedToken = githubToken;
      return [published("0.1.3")];
    },
    fetchDockerLinuxAmd64Descriptor: async () => descriptorFor("0.1.3"),
  });

  assert.equal(exitCode, 0, stderr);
  assert.equal(observedToken, "github-test-token");
  assert.match(stdout, /release baseline verified for 0\.1\.3/i);
  assert.doesNotMatch(`${stdout}\n${stderr}`, /github-test-token/);
});

test("verify-release-baseline CLI rejects malformed candidate SemVer before network access", async () => {
  let fetched = false;
  let stderr = "";
  const exitCode = await main([
    "verify-release-baseline",
    "--fixture", "tests/upgrade/fixtures/v0.1.3",
    "--candidate-version", "not-semver",
  ], {
    cwd: process.cwd(),
    stderr: { write: (value) => { stderr += value; } },
    stdout: { write: () => {} },
    fetchPublishedStable01Releases: async () => {
      fetched = true;
      return [published("0.1.3")];
    },
  });

  assert.equal(exitCode, 1);
  assert.equal(fetched, false);
  assert.match(stderr, /candidate version must be valid SemVer/i);
});
