const GITHUB_API = "https://api.github.com/repos/Syntax-Circus/cmsify";
const DOCKER_TOKEN_API = "https://auth.docker.io/token";
const DOCKER_REGISTRY_API = "https://registry-1.docker.io";
const API_TIMEOUT_MS = 30_000;
const MAX_RESPONSE_BYTES = 1024 * 1024;
const MAX_GITHUB_PAGES = 10;
const MAX_TAG_DEPTH = 8;
const FULL_SHA = /^[0-9a-f]{40}$/;
const DIGEST = /^sha256:[0-9a-f]{64}$/;
const STABLE_01 = /^0\.1\.(0|[1-9]\d*)$/;
const CANDIDATE_SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*)?$/;
const DOCKER_REPOSITORY = /^docker\.io\/[a-z0-9]+(?:[._/-][a-z0-9]+)*$/;
const DOCKER_TAG = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const INDEX_MEDIA_TYPES = new Set([
  "application/vnd.oci.image.index.v1+json",
  "application/vnd.docker.distribution.manifest.list.v2+json",
]);
const IMAGE_MANIFEST_MEDIA_TYPES = new Set([
  "application/vnd.oci.image.manifest.v1+json",
  "application/vnd.docker.distribution.manifest.v2+json",
]);

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/** Validates the release candidate version before any publication lookup. */
export function validateReleaseCandidateVersion(candidateVersion) {
  assert(typeof candidateVersion === "string" && CANDIDATE_SEMVER.test(candidateVersion), "Candidate version must be valid SemVer without build metadata.");
  return candidateVersion;
}

function assertPublishedRelease(release) {
  assert(isPlainObject(release), "Published release metadata is malformed.");
  assert(STABLE_01.test(release.version), "Published release version is malformed.");
  assert(release.tag === `v${release.version}`, `Published release tag must equal v${release.version}.`);
  assert(typeof release.sourceSha === "string" && FULL_SHA.test(release.sourceSha), `Published release ${release.tag} source SHA is malformed.`);
  assert(typeof release.publishedAt === "string" && !Number.isNaN(Date.parse(release.publishedAt)), `Published release ${release.tag} publication time is malformed.`);
}

/**
 * @typedef {{version:string, tag:string, sourceSha:string, publishedAt:string}} PublishedRelease
 * @typedef {{repository:string, tag:string, digest:string, platform:"linux/amd64"}} DockerDescriptor
 * @typedef {{baselineVersion:string, sourceSha:string, apiDigest:string, verifiedAt:string}} VerificationResult
 */

/**
 * Selects the newest stable 0.1.x release published before the candidate.
 * @param {PublishedRelease[]} releases
 * @param {string} candidateVersion
 * @returns {PublishedRelease}
 */
export function selectLatestPublishedStable01(releases, candidateVersion) {
  validateReleaseCandidateVersion(candidateVersion);
  assert(Array.isArray(releases), "Published releases must be an array.");

  const stable = [];
  for (const release of releases) {
    if (!isPlainObject(release) || typeof release.version !== "string" || !STABLE_01.test(release.version)) continue;
    if (release.version === candidateVersion) continue;
    assertPublishedRelease(release);
    stable.push(release);
  }
  assert(stable.length > 0, "No published stable 0.1.x baseline exists before this candidate.");
  stable.sort((left, right) => Number(right.version.slice("0.1.".length)) - Number(left.version.slice("0.1.".length)));
  return Object.freeze({ ...stable[0] });
}

function assertDockerDescriptor(descriptor) {
  assert(isPlainObject(descriptor), "Docker descriptor is malformed.");
  assert(typeof descriptor.repository === "string" && DOCKER_REPOSITORY.test(descriptor.repository), "Docker descriptor repository is malformed.");
  assert(typeof descriptor.tag === "string" && DOCKER_TAG.test(descriptor.tag), "Docker descriptor tag is malformed.");
  assert(typeof descriptor.digest === "string" && DIGEST.test(descriptor.digest), "Docker descriptor digest is malformed.");
  assert(descriptor.platform === "linux/amd64", "Docker descriptor platform must be linux/amd64.");
}

/**
 * Verifies the checked-in fixture against GitHub and Docker publication identity.
 * @param {{candidateVersion:string, fixtureManifest:object, githubReleases:PublishedRelease[], dockerDescriptor:DockerDescriptor}} options
 * @returns {VerificationResult}
 */
export function verifyReleaseBaseline({ candidateVersion, fixtureManifest, githubReleases, dockerDescriptor }) {
  const latest = selectLatestPublishedStable01(githubReleases, candidateVersion);
  assert(isPlainObject(fixtureManifest?.baseline), "Fixture baseline metadata is malformed.");
  assert(fixtureManifest.baseline.version === latest.version, `Fixture records ${fixtureManifest.baseline.version ?? "<missing>"} but latest published baseline is ${latest.version}.`);
  assert(typeof fixtureManifest.baseline.sourceSha === "string" && FULL_SHA.test(fixtureManifest.baseline.sourceSha), "Fixture baseline source SHA is malformed.");
  assert(fixtureManifest.baseline.sourceSha === latest.sourceSha, `GitHub release ${latest.tag} source ${latest.sourceSha} does not match fixture source ${fixtureManifest.baseline.sourceSha}.`);
  assertDockerDescriptor(dockerDescriptor);

  const fixtureImage = fixtureManifest.baseline.apiImage;
  assert(isPlainObject(fixtureImage), "Fixture baseline API image metadata is malformed.");
  assert(dockerDescriptor.repository === fixtureImage.repository, `Docker descriptor repository ${dockerDescriptor.repository} does not match fixture repository ${fixtureImage.repository ?? "<missing>"}.`);
  assert(dockerDescriptor.tag === latest.version && dockerDescriptor.tag === fixtureImage.tag, `Docker descriptor tag ${dockerDescriptor.tag} does not match fixture baseline tag ${fixtureImage.tag ?? "<missing>"}.`);
  assert(dockerDescriptor.platform === fixtureImage.platform, `Docker descriptor platform ${dockerDescriptor.platform} does not match fixture platform ${fixtureImage.platform ?? "<missing>"}.`);
  assert(dockerDescriptor.digest === fixtureImage.digest, `Docker descriptor digest ${dockerDescriptor.digest} does not match fixture digest ${fixtureImage.digest ?? "<missing>"}.`);

  return Object.freeze({
    baselineVersion: latest.version,
    sourceSha: latest.sourceSha,
    apiDigest: dockerDescriptor.digest,
    verifiedAt: new Date().toISOString(),
  });
}

async function readBoundedJson(response, label) {
  const declaredLength = Number(response.headers.get("content-length"));
  assert(!Number.isFinite(declaredLength) || declaredLength <= MAX_RESPONSE_BYTES, `${label} response exceeds the one MiB limit.`);
  assert(response.body && typeof response.body.getReader === "function", `${label} response body is missing.`);
  const reader = response.body.getReader();
  const chunks = [];
  let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > MAX_RESPONSE_BYTES) {
      await reader.cancel();
      throw new Error(`${label} response exceeds the one MiB limit.`);
    }
    chunks.push(Buffer.from(value));
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    throw new Error(`${label} returned malformed JSON.`);
  }
}

async function fetchJson(url, { fetchImpl, headers, label, timeoutMs }) {
  assert(typeof fetchImpl === "function", `${label} fetch implementation is required.`);
  assert(Number.isFinite(timeoutMs) && timeoutMs > 0 && timeoutMs <= 120_000, `${label} timeout must be bounded.`);
  let response;
  try {
    response = await fetchImpl(url, {
      method: "GET",
      headers,
      redirect: "error",
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (error) {
    if (error?.name === "AbortError" || error?.name === "TimeoutError") throw new Error(`${label} request timed out.`);
    throw new Error(`${label} request failed.`, { cause: error });
  }
  assert(response instanceof Response, `${label} returned an invalid response.`);
  if (response.status !== 200) {
    if (label.startsWith("GitHub") && (response.status === 429 || (response.status === 403 && response.headers.get("x-ratelimit-remaining") === "0"))) {
      throw new Error(`GitHub API rate limit exhausted (HTTP ${response.status}).`);
    }
    throw new Error(`${label} returned HTTP ${response.status}; explicit HTTP 200 is required.`);
  }
  return readBoundedJson(response, label);
}

function githubHeaders(githubToken) {
  assert(githubToken === undefined || (typeof githubToken === "string" && githubToken.length > 0 && !/[\r\n\0]/.test(githubToken)), "GitHub token is malformed.");
  return {
    accept: "application/vnd.github+json",
    "user-agent": "cmsify-release-baseline-verifier",
    "x-github-api-version": "2022-11-28",
    ...(githubToken ? { authorization: `Bearer ${githubToken}` } : {}),
  };
}

async function peelGitTag(tag, options) {
  const headers = githubHeaders(options.githubToken);
  const ref = await fetchJson(`${GITHUB_API}/git/ref/tags/${tag}`, {
    ...options,
    headers,
    label: `GitHub tag reference ${tag}`,
  });
  assert(isPlainObject(ref?.object), `GitHub tag reference ${tag} is malformed.`);

  let object = ref.object;
  for (let depth = 0; depth < MAX_TAG_DEPTH; depth += 1) {
    assert(typeof object.sha === "string" && FULL_SHA.test(object.sha), `GitHub tag ${tag} object SHA is malformed.`);
    if (object.type === "commit") return object.sha;
    assert(object.type === "tag", `GitHub tag ${tag} does not resolve to a commit.`);
    const annotated = await fetchJson(`${GITHUB_API}/git/tags/${object.sha}`, {
      ...options,
      headers,
      label: `GitHub annotated tag ${tag}`,
    });
    assert(isPlainObject(annotated?.object), `GitHub annotated tag ${tag} is malformed.`);
    object = annotated.object;
  }
  throw new Error(`GitHub tag ${tag} exceeds the annotated-tag depth limit.`);
}

/**
 * Lists stable published 0.1.x releases and resolves each tag to a full commit.
 * @param {{fetchImpl?:typeof fetch, githubToken?:string, timeoutMs?:number}} options
 * @returns {Promise<PublishedRelease[]>}
 */
export async function fetchPublishedStable01Releases({
  fetchImpl = globalThis.fetch,
  githubToken,
  timeoutMs = API_TIMEOUT_MS,
} = {}) {
  const headers = githubHeaders(githubToken);
  const releases = [];
  let exhausted = false;
  for (let page = 1; page <= MAX_GITHUB_PAGES; page += 1) {
    const payload = await fetchJson(`${GITHUB_API}/releases?per_page=100&page=${page}`, {
      fetchImpl,
      headers,
      label: "GitHub releases API",
      timeoutMs,
    });
    assert(Array.isArray(payload), "GitHub releases API returned malformed release metadata.");
    assert(payload.length <= 100, "GitHub releases API returned an oversized page.");
    for (const release of payload) {
      if (!isPlainObject(release) || release.draft !== false || release.prerelease !== false) continue;
      const match = typeof release.tag_name === "string" ? release.tag_name.match(/^v(0\.1\.(?:0|[1-9]\d*))$/) : null;
      if (!match) continue;
      assert(typeof release.published_at === "string" && !Number.isNaN(Date.parse(release.published_at)), `GitHub release ${release.tag_name} publication time is malformed.`);
      releases.push(Object.freeze({
        version: match[1],
        tag: release.tag_name,
        sourceSha: await peelGitTag(release.tag_name, { fetchImpl, githubToken, timeoutMs }),
        publishedAt: release.published_at,
      }));
    }
    if (payload.length < 100) {
      exhausted = true;
      break;
    }
  }
  assert(exhausted, `GitHub releases API exceeds the ${MAX_GITHUB_PAGES}-page safety limit.`);
  return Object.freeze(releases);
}

function dockerRepositoryPath(repository) {
  assert(typeof repository === "string" && DOCKER_REPOSITORY.test(repository), "Docker repository must be a canonical docker.io repository.");
  return repository.slice("docker.io/".length);
}

/**
 * Resolves a Docker Hub tag to its immutable linux/amd64 child descriptor.
 * @param {{repository:string, tag:string, fetchImpl?:typeof fetch, timeoutMs?:number}} options
 * @returns {Promise<DockerDescriptor>}
 */
export async function fetchDockerLinuxAmd64Descriptor({
  repository,
  tag,
  fetchImpl = globalThis.fetch,
  timeoutMs = API_TIMEOUT_MS,
}) {
  const repositoryPath = dockerRepositoryPath(repository);
  assert(typeof tag === "string" && DOCKER_TAG.test(tag), "Docker tag is malformed.");
  const tokenUrl = new URL(DOCKER_TOKEN_API);
  tokenUrl.searchParams.set("service", "registry.docker.io");
  tokenUrl.searchParams.set("scope", `repository:${repositoryPath}:pull`);
  const tokenResponse = await fetchJson(tokenUrl.href, {
    fetchImpl,
    headers: { accept: "application/json", "user-agent": "cmsify-release-baseline-verifier" },
    label: "Docker Registry token endpoint",
    timeoutMs,
  });
  assert(typeof tokenResponse?.token === "string" && tokenResponse.token.length > 0 && tokenResponse.token.length <= 16_384 && !/[\r\n\0]/.test(tokenResponse.token), "Docker Registry token response is malformed.");

  const payload = await fetchJson(`${DOCKER_REGISTRY_API}/v2/${repositoryPath}/manifests/${encodeURIComponent(tag)}`, {
    fetchImpl,
    headers: {
      accept: [...INDEX_MEDIA_TYPES, ...IMAGE_MANIFEST_MEDIA_TYPES].join(", "),
      authorization: `Bearer ${tokenResponse.token}`,
      "user-agent": "cmsify-release-baseline-verifier",
    },
    label: `Docker Registry manifest ${repositoryPath}:${tag}`,
    timeoutMs,
  });
  assert(isPlainObject(payload) && payload.schemaVersion === 2 && INDEX_MEDIA_TYPES.has(payload.mediaType) && Array.isArray(payload.manifests), "Docker Registry manifest list is malformed.");
  const matches = payload.manifests.filter((descriptor) => descriptor?.platform?.os === "linux" && descriptor?.platform?.architecture === "amd64" && (descriptor.platform.variant === undefined || descriptor.platform.variant === ""));
  assert(matches.length === 1, "Docker Registry manifest must contain exactly one linux/amd64 child descriptor.");
  const descriptor = matches[0];
  assert(isPlainObject(descriptor) && IMAGE_MANIFEST_MEDIA_TYPES.has(descriptor.mediaType) && Number.isSafeInteger(descriptor.size) && descriptor.size > 0 && typeof descriptor.digest === "string" && DIGEST.test(descriptor.digest), "Docker Registry linux/amd64 child descriptor is malformed.");
  return Object.freeze({ repository, tag, digest: descriptor.digest, platform: "linux/amd64" });
}
