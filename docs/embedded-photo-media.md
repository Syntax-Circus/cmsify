# Embedded photo foundation

This optional PostgreSQL foundation owns private originals, immutable responsive
variants, durable processing jobs and consumer reference protection. It is an
embedding contract, not a gallery, publication model or installed image decoder.
The host owns assignments, published snapshots, transport, current grants and
recovery/backup retention.

Local candidate `0.8.14-photo.1` at engine source
`9de88a7621544db736ca7a9bb10b2ee24800d629` passed the synthetic package-only
consumer on Windows and Linux amd64 with SDK 10.0.400. This proves generic
orchestration and package exports. It does not qualify actual phone formats,
HDR SDR-base selection, color/orientation, encoded pixels or native isolation.
No production processor is installed. Do not enable native processing until the
host's sandbox, corpus, licensing and image acceptance gates are qualified.

## Composition

Register host services before CMS composition, or explicitly replace defaults:

```csharp
services.AddScoped<ICurrentActor, HostCurrentActor>();
services.AddScoped<IEmbeddedPhotoAuthorizationService, HostPhotoAuthority>();
services.AddSingleton<IEmbeddedPhotoProcessorCapability, QualifiedHostCapability>();
services.AddScoped<IEmbeddedPhotoProcessor, SupervisedHostProcessor>();
services.AddSingleton(new RegisteredEmbeddedMediaRetentionGuard("host-publication", hostRetention));
services.AddCmsifyInfrastructure(configuration, new CmsifyInfrastructureOptions
{
    UseHostCurrentActorForAudit = true,
    Workers = CmsifyWorkers.None
});
services.AddCmsifyEmbeddedPhotos(options =>
{
    options.Enabled = false;
    options.ProcessingEnabled = false;
});
```

These host types are examples of required implementations, not shipped adapters.
Photo options are validated startup choices. Repeat identical registration is
idempotent; conflicting choices fail. Register CMS infrastructure first.
SQLite fails explicitly before photo descriptors are added. Photo worker
selection is independent of existing `CmsifyWorkers`; both photo flags must be
true to register the photo worker. Defaults deny resource operations, report
unavailable processor capability and perform no decoding.

Operator setup applies packaged PostgreSQL migrations explicitly, including
`20261010162757_AddEmbeddedPhotoFoundation`. Legacy media is not backfilled as
Ready. `IEmbeddedPhotoReadiness.CheckAsync` reads required schema column shapes,
storage reachability and policy/capability identity. It never migrates, saves,
repairs uploads or creates probe objects. Reasons are allowlisted:
`disabled`, `unsupported-provider`, `schema-unavailable`,
`storage-unavailable`, `capability-unavailable`, `policy-mismatch`.
Readiness alone does not certify storage write permissions, private serving
configuration or native safety; establish those during host setup/qualification.

## Operations and identity

All twelve named handlers are in `Cmsify.Core.EmbeddedMedia`. They return Common
results and detached records. No HTTP or CMS table access is required:

| Purpose | Handler stem |
| --- | --- |
| Reserve, stream, recover upload | BeginEmbeddedPhotoUpload, WriteEmbeddedPhotoUpload, GetEmbeddedPhotoUploadOutcome |
| Read processing state, create immutable rotation, explicitly retry | GetEmbeddedPhoto, CreateEmbeddedPhotoRevision, RetryEmbeddedPhotoProcessing |
| Read exact immutable manifest | GetEmbeddedPhotoManifest |
| Protect ordered references, recover result, terminal release | ProtectEmbeddedMediaReferences, GetEmbeddedMediaReferenceOutcome, ReleaseEmbeddedMediaReferences |
| Private original, exact private/consumer variant | ReadEmbeddedPhotoOriginal, ReadEmbeddedPhotoVariant |

Interface names are `I<Stem>RequestHandler`; implementations are
`<Stem>RequestHandler` and entry points are `HandleAsync(request, cancellationToken)`.
Each photo operation requires an authenticated, nonempty user subject, Reader
or Editor as appropriate, workspace authorization and fresh host resource
authorization. API client identity alone and missing-user SuperAdmin flags do
not substitute for a photo subject. A background worker resolves a scoped,
explicit host subject; an anonymous worker claims nothing.

The resource operation identifies workspace, asset/revision or consumer
namespace/reference. Return grants from trusted host records; browser-provided
asset/reference IDs are not proof. Protection itself grants no public access.
A public-serving host must verify the exact currently published snapshot, then
use its configured authenticated consumer subject to read the protected variant.
That public authority must deny originals and unrelated private operations.

## Streaming and recovery

Authenticate before constructing/opening an `IEmbeddedPhotoByteSource`.
Pass the request stream through the source port, bounded by the CMS reader;
do not buffer whole phone files or transfer them through a Blazor circuit.
Display names and MIME are hints, never paths or acceptance evidence. The CMS
counts bytes incrementally, requires exact declared length/EOF, limits input to
20 MiB, computes SHA-256 and chooses private server-owned attempt keys.

Original/receipt/initial revision/job acknowledgement is one CMS transaction.
If a response is uncertain, read the upload outcome before any explicit restart.
Completed uploads refuse another body without opening it. Expired writers cannot
commit; restart uses a new fence/object. Processing has one leased site attempt,
a persisted three-attempt transient budget and 5/30-second delays. Rejected input
cannot retry. Failed processing uses a keyed explicit retry generation.

Ready status exposes `Manifest`: the exact asset/revision/checksum identity
needed by GetManifest. Non-Ready status has no manifest. Rotation creates a new
revision, preserving old source, manifests and derivatives. Required WebP
variants are ordered 320/640/1280, contained without enlargement, quality 80.
The CMS validates bounded container structure/dimensions/alpha, metadata absence
and hashes; this is not full compressed-pixel or color validation.

Source metadata dimensions are after automatic orientation, before manual
quarter-turns. Bit depth describes the selected SDR decode. The future supervised
native adapter must meet the actual policy and external deadline/resource/network
boundaries. The orchestration cancellation deadline is cooperative and cannot
terminate an uncooperative native child.

Read handlers invoke `IEmbeddedPhotoByteSink` inside the authorized storage
lifetime; consume to EOF and do not retain the stream after return. Originals
are opaque private bytes. Manifests/DTOs expose neither storage keys nor paths.
Keep every `cmsify/media/photos/` object outside public/static roots, and private
in remote storage. Generic repository adoption of that reserved prefix is refused.

## Retained references

Reserve a durable host intent before Protect. Ordered sets contain at most 1000
exact manifest identities. Reordering or changing keyed intent conflicts.
Protection returns a stable receipt and remains indefinitely when the host
commit is pending, absent, unavailable or unknown.

The registered host guard returns ReleaseAllowed only for a durable terminal
abandoned/retired intent with no serving, rollback, backup or recovery need.
Missing guards, exceptions and Unknown retain protection. Release checks the
exact protection revision and guard again under shared locks, returns a stable
terminal receipt and cannot resurrect the old reference key. New use needs a
new key. Draft removal does not release published or backup references.

Logical deletion and physical preparation share asset fences with Protect.
Committed originals/variants and active attempts are inventory owners. Expired
staging fences retire before becoming purge candidates under existing grace
rules. Physical deletion retains all stored content-version references,
including soft-deleted recovery history, until that history is permanently
removed. Consumer protections never expire automatically.

The generic `Cmsify.Core.Media.IDeleteMediaAssetRequestHandler` preserves
standalone ETags and content-reference conflicts. Its successful
`MediaAssetDeletionOutcome` is a business decision: inspect `Deleted`.
`Deleted == false` supplies immutable `ReferencedBy` content IDs; the standalone
HTTP adapter maps that to its existing 409 extension. Do not infer deletion from
Common `IsSuccess` alone.

## Reproduce local qualification

Photo-owned assets are excluded from generic media list/get/file and repository
discovery, including workspace-authorized API clients. Use the named photo
read handlers and fresh resource grants; a private original is never made
readable by the generic workspace grant. Generic adoption requires canonical
relative storage keys: repeated separators, backslashes, rooted/drive forms,
dot segments and trailing dot/space aliases are rejected. Physical deletion
also retains ambiguous keys and resolves photo inventory ownership before a
generic deletion claim's asset identity.

An unacknowledged Failed upload may be explicitly rewritten with its existing
session and write operation key after lease revocation/expiry. Restart locks
the asset before the session, allocates a new attempt key/generation and restores
only a live failed upload asset. Completed/abandoned/deleted sessions cannot
resend or resurrect bytes.

Queue claims filter asset/provider/policy/version eligibility before bounded
100-row paging. A shared host-local cursor advances past denied resources and
wraps at the end without changing their job state or consuming retry budget.
Restart resets traversal safely; it does not change durable claim fences.
Upload leases are at most ten minutes, processing leases at most two minutes;
startup and runtime share those limits.

```powershell
rtk proxy pwsh -NoProfile -File scripts/qualification/Invoke-EmbeddedPhotoQualification.ps1 `
  -PackageVersion <fresh-local-prerelease-version> `
  -SourceCommit <current-committed-HEAD> `
  -PackageDirectory <absolute-private-candidate-feed> `
  -DotnetPath <SDK-10.0.400-dotnet> -Platform Both
```

The recorded candidate above identifies the engine checkpoint used in the first
qualification. For a new run, use the current committed HEAD containing this
runner/fixture and a fresh local prerelease version. The runner rejects dirty engine source
and records exact arguments, package/source hashes and subprocess exits. The
consumer is copied outside either checkout, restores from candidate/public
packages with empty caches and no project references, verifies AGPL license,
readmes, assembly/package/source identities, and applies migrations only to
owned disposable PostgreSQL. Linux runs on `linux/amd64`.
The host fixture has no CMS SQL for photo operations or retention intents.
Its synthetic three-byte input and deliberately incomplete encoded image
headers test orchestration only. Real process-kill tests live in Infrastructure
tests. The fixture file journal is disposable demonstration code, not a product
retention service or disaster-proof filesystem backup design.

The runner neither publishes packages nor upgrades hosts. Qualify deployment
and actual phone images separately before release/adoption.
