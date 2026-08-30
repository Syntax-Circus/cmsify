# Offline OCI Candidate Loader Transport Design

Date: 2026-08-30  
Status: Approved direction; pending written-spec review

## Context

Cmsify release candidates are built once as OCI-layout archives. Smoke, accessibility, and upgrade/rollback checks must execute those exact candidate contents without rebuilding them or pulling a substitute image.

Direct `docker load` does not accept an OCI-layout archive on the supported classic Docker daemon because the archive has `oci-layout` and `index.json`, not Docker's `manifest.json`. A registry relay was implemented to preserve the OCI distribution-manifest digest, but live validation exposed two incompatible Docker Desktop boundaries:

1. an internal Docker network does not publish the registry port to the host;
2. a loopback port published to the Windows host is not reachable as `127.0.0.1` from the Linux Docker daemon performing `docker pull`.

Further registry or daemon-configuration work would make certification depend on runner-specific networking, an external registry, or containerd image-store configuration. The approved replacement is a fully offline conversion used only as a runtime transport.

## Decision

The original OCI archive remains the certified release artifact and the only promoted image source. The release manifest, OCI descriptor digest, and `SHA256SUMS` bind that archive before any runtime loading.

Pinned Skopeo converts the selected image from the immutable OCI archive into a temporary Docker-archive representation. Docker loads that temporary representation. The loader proves runtime identity through the OCI config digest and root-filesystem DiffIDs rather than claiming that Docker's representation exposes the original OCI distribution-manifest digest.

This distinction is explicit:

- artifact identity is the original OCI archive and its certified OCI descriptor digest;
- runtime identity is the exact OCI config object and its ordered root-filesystem DiffIDs;
- the temporary Docker archive is transport scratch, not a release artifact, certification subject, or promotion source.

## Loader Flow

1. Validate the requested kind, strict SemVer version, canonical repository/tag, release-manifest descriptor, and safe regular input paths before invoking Docker.
2. Parse the OCI archive index and select exactly one `linux/amd64` descriptor by the expected tag and canonical image name.
3. Read the selected OCI manifest blob from the archive. Verify its byte length and SHA-256 against the selected descriptor before parsing it.
4. Validate the OCI manifest schema/media type and every config/layer descriptor. Read the config blob, verifying its byte length and SHA-256, then capture:
   - config digest;
   - OS and architecture;
   - ordered `rootfs.diff_ids`;
   - required release version, informational version, and source-revision labels.
5. Refuse to proceed if the canonical Docker tag already exists. Preflight the exact run-owned Skopeo container name and scratch output path.
6. Create a run-owned scratch directory outside the certified artifact tree. Reject links/reparse points and register cleanup before each mutation.
7. Run the digest-pinned Skopeo container with:
   - `--network none`;
   - no Docker socket;
   - the OCI archive mounted read-only;
   - only the scratch directory mounted writable;
   - an exact `oci-archive:` source selector and `docker-archive:` destination tagged with the canonical candidate reference.
8. Require Skopeo success and a bounded, regular, non-linked Docker archive inside the run-owned scratch directory. The source OCI archive must remain unchanged.
9. Invoke `docker image load --input <scratch archive> --platform linux/amd64`. Do not trust or use human-readable load output as identity evidence.
10. Inspect the canonical tag and require:
    - Docker image ID equals the verified OCI config digest;
    - OS is `linux` and architecture is `amd64`;
    - ordered Docker `RootFS.Layers` equals the verified OCI config `rootfs.diff_ids` exactly;
    - required version, informational-version, and source-revision labels equal the candidate manifest/source identity;
    - the exact canonical tag is present.
11. Return the canonical reference, certified OCI descriptor digest, config/image digest, and ordered DiffIDs for downstream evidence.
12. Always remove the Skopeo helper and scratch directory. On any failure after `docker load` may have mutated the daemon, remove only the exact run-owned canonical tag. On success, leave that tag for the authorized downstream rehearsal and smoke checks.

## Integrity Chain

The loader must demonstrate this chain without substituting representations:

```text
SHA256SUMS
  -> original OCI archive bytes
  -> selected OCI descriptor digest/size
  -> verified OCI manifest bytes
  -> verified config digest + ordered compressed layer descriptors
  -> verified config rootfs.diff_ids
  -> Docker image ID + Docker RootFS.Layers
```

The Docker archive may have a different distribution-manifest representation. It is never added to `SHA256SUMS`, uploaded, attested, signed, promoted, or retained as evidence.

## Failure and Cleanup Rules

- Every pre-existing exact target collision is blocking; the loader never overwrites or removes an image it did not create.
- Cleanup intent is registered before every mutating Docker or filesystem operation, including create-then-throw boundaries.
- Cleanup targets are derived only from validated inputs and run-owned generated names, never subprocess output.
- A primary failure plus cleanup failure reports both in bounded sanitized form and remains non-zero.
- Missing exact run-owned cleanup targets may be ignored only for recognized exit-code-1 diagnostics whose extracted target matches case-sensitively.
- Source archive, release manifest, checksum file, and OCI metadata are read-only inputs.
- No registry, Docker network, Docker socket mount, external candidate pull, or image rebuild is permitted.

## Workflow and Documentation Changes

The existing `load-oci-candidate.mjs` interface remains stable unless implementation proves an additional checksum path is required. Release, accessibility, smoke, and upgrade workflows continue invoking the loader rather than embedding conversion commands.

The semantic release verifier must require the offline transport properties and reject regressions that reintroduce:

- registry/network creation;
- Skopeo networking;
- Docker socket mounts;
- direct loading of the OCI archive;
- source rebuilds or external candidate pulls;
- use of the temporary Docker archive as a certified/uploaded subject;
- missing config/diff-ID/label identity checks;
- cleanup after rather than before checksum/upload boundaries.

The release and upgrade runbooks will explain that runtime conversion is disposable and does not change the promoted OCI bytes.

## Test Strategy

Implementation follows strict RED-GREEN-REFACTOR.

Behavioral tests must cover:

- OCI manifest/config digest or size mismatch before Docker;
- malformed config, wrong platform, missing/duplicate labels, and unsafe DiffIDs;
- pre-existing canonical tag and Skopeo-name collisions;
- Skopeo missing `--network none`, a writable source mount, socket mount, unsafe output path, or wrong source selector;
- Docker loading the original OCI archive instead of the scratch Docker archive;
- loaded image ID, ordered DiffIDs, platform, labels, or canonical tag differing by one value;
- partial scratch/Skopeo/load/tag failures and exact cleanup behavior;
- mutation tests proving workflows and documentation cannot revert to registry relay or rebuild behavior.

After independent code review, one live non-certifying check will use the preserved API candidate. It must bind source hashes before and after, load successfully, validate the returned identity and image labels, remove the exact candidate tag and scratch resources, and leave no loader-owned Docker resources.

The final candidate tuple will then rerun the same loader for API and Admin candidates before accessibility, upgrade/rollback, and release smoke.

## Security and Portability

Skopeo processes untrusted archive structure without network access, without a Docker socket, and with only one writable run-owned scratch mount. Docker receives a local archive only after OCI manifest/config validation. The design works on Docker Desktop's classic daemon and ordinary GitHub-hosted Linux Docker without daemon reconfiguration or an external service.

The accepted tradeoff is that Docker does not expose the original OCI distribution-manifest digest after representation conversion. The original artifact remains cryptographically certified, while runtime equivalence is proven by the immutable config digest and ordered filesystem DiffIDs.

## Out of Scope

- Publishing, signing, attesting, promoting, tagging, or releasing candidates.
- Reconfiguring Docker Desktop or hosted runners to use containerd image storage.
- Running an external or shared registry.
- Treating temporary conversion output as a supported distribution artifact.
