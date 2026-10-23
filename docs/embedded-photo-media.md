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

### Versioned JPEG SDR-base policy (local, unreleased)

`JpegSdrBaseEnabled` defaults false. Opting in enables `jpeg-sdr-base-webp-v1`
alongside the configured legacy and optional PNG policies. The host supplies a
policy-specific qualified capability and processor; Cmsify does not decode JPEG,
recognize gain maps or select frames. The host must separately qualify its SDR
conversion and obtain any required fidelity decision while preserving full originals.

Candidate proof must identify JPEG,8-bit,sRGB,no alpha,`UsedSdrBase=true`, conversion
version `apple-jpeg-sdr-base-q8-srgb-strip-v1` and original bit depth8. The existing
320/640/1280 WebP requirements and24MP bound apply. JPEG manifests use identity
version3, binding conversion/source metadata and variants. Legacy version1 and PNG
version2 identities remain unchanged. Reads, reference protection and historical
manifest lookup verify the JPEG proof even after the option is disabled.

Use the existing source-hash/receipt-bound upload enrollment or exact-revision
operation; operation replay, source-metadata consistency, authority, immutable
originals and retention still apply. Disabled revision/retry requests do not invoke
the optional adapter. An unavailable or throwing JPEG capability probe leaves its
jobs queued while eligible legacy/PNG work continues; cancellation propagates.
Readiness requires the JPEG capability only when opted in. The adapter must dispatch
by the persisted policy/processor version and never relabel a PNG or legacy proof.

### Versioned PNG normalization (local, unreleased)

`PngNormalizationEnabled` defaults false. Explicitly enabling it allows policy
`png-sdr-webp-v1` alongside the configured legacy `PolicyId`. The host must override
`IEmbeddedPhotoProcessorCapability.GetAsync(policyId, cancellationToken)` and
dispatch its processor by the immutable input policy/version. The default overload
keeps existing single-policy adapters compatible and cannot advertise PNG support.
Both old and new eligible jobs share the existing global slot, authorization,
leases, attempt bounds and bounded queue paging. Readiness requires the opted-in
PNG capability. An unavailable PNG adapter does not prevent eligible legacy work.

The PNG contract admits one eight/16-bit PNG up to30 MP/8192 edge, selected
eight-bit sRGB pixels, no HDR SDR-base assertion, and four WebP variants at
320/640/1280/4096 bounds (quality85). The native host must enforce single-frame,
color, encoded-size and resource admission; this library never decodes originals.
Candidate metadata includes `Conversion` with version `png-q8-srgb-strip-v1` and
`OriginalBitDepth`; oriented original dimensions stay truthful. Legacy policy
IDs retain24 MP/eight-bit/three variants. No PDF conversion or dog association.

Use normal explicit enrollment or `CreateEmbeddedPhotoRevision` with the new
policy, expected latest revision and a durable operation key. A rejected prior
revision stays rejected; it cannot become a transient Retry. Receipts replay their
original acknowledgement after context recreation. Originals, upload receipts,
old manifest hashes and protected references remain retained. PNG manifests use
a version2 hash binding source/conversion metadata and all four descriptors;
legacy version1 hashes stay unchanged. Reads/protection use the persisted policy,
independent of the current opt-in, with the existing private/reference authority.
The larger key is never accepted against a legacy manifest. No migration or new
HTTP endpoint is required: additive provenance uses the existing manifest JSON.

No native adapter, automatic activation, publication or release ships from these
local changes. Synthetic PostgreSQL evidence tests orchestration/container headers;
actual pixel/resource/privacy/browser qualification belongs to the embedding host.

### Configurable original intake (0.8.20)

Released0.8.20 adds
`EmbeddedPhotoOptions.MaximumOriginalBytes`. It defaults to20 MiB and may be
explicitly configured from1 byte through64 MiB. Registration snapshots the value,
rejects invalid/conflicting registrations, and exposes the immutable
`IEmbeddedPhotoUploadLimits` Core contract. The existing four-argument begin
handler constructor keeps20 MiB behavior; DI uses the limits-aware overload.

The PostgreSQL migration widens original/session checks to the supported64 MiB
ceiling. Reducing intake affects fresh reservations; retained originals and
already authorized pending writes remain bounded by their persisted lengths.
Resume through the existing intent/outcome handlers, rather than changing the
original request. Storage counts/hashes while reading, rejects declared length
overflow and validates complete provider acknowledgement. Private original reads
verify retained length/hash before sending bytes. Authorization and retention
fences remain mandatory. Down migration refuses larger retained rows without
deleting or truncating them.

This option does not change variants, dimensions,24 MP,8192 edge, decoder color
support, time, memory or processor qualification. An accepted original may still
be rejected for derivatives. Local actual-source testing demonstrated exactly
that: two28.1 MP8-bit PNGs reject dimensions, and a4.46 MP16-bit PNG rejects color;
their original bytes remain downloadable only with normal authority.

The paired PuppiesPlus host now consumes public0.8.20 through ordinary locked
NuGet references. Earlier isolated local-source qualification is historical; it
is not required for this released setting. Defaults remain20 MiB unless the host
explicitly configures a larger limit. The new PNG profile above remains local
and unreleased, separate from that already-published original-intake setting.

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
`storage-unavailable`, `capability-unavailable`, `policy-mismatch`,
and opt-in `png-capability-unavailable`.
Readiness alone does not certify storage write permissions, private serving
configuration or native safety; establish those during host setup/qualification.

## Private upload-only composition

The enrollment amendment adds `IEnrollEmbeddedPhotoUploadRequestHandler` for
acknowledged upload-only originals. Bind workspace/asset, upload receipt, original
SHA256, expected latest revision, operation key and processing policy. It creates
the next processing revision/job atomically without replacing the original or its
upload receipt, original revision or intake retention hold. Same-key replay returns
the original result; changed intent or a competing enrollment conflicts. A normal
revision request still rejects an unenrolled upload-only asset.

Upload-only intake and processing can coexist when both are explicitly enabled.
New uploads remain AwaitingProcessing until enrollment. The host still supplies
the processor/capability and narrowly scoped worker authority. Keep storage/intake
readiness independent of decoder availability.

Use `IGetEmbeddedPhotoOperationOutcomeRequestHandler` to recover enrollment/retry
acknowledgements without writing. Its workspace/asset/operation-key/kind lookup
returns the immutable operation result, which can say Queued even when current
status is Ready. Read current status separately. Only enrollment/retry kinds are
accepted, with fresh read authority; foreign keys do not reveal another asset.
Apply the additive `AddPhotoProcessingEnrollment` migration before adoption.
These are source amendment contracts; public0.8.16 does not export them.

A host can accept opaque private originals before qualifying a processor. Register
its current subject/resource authority and CMS infrastructure with `CmsifyWorkers.None`,
then use `AddCmsifyEmbeddedPhotos(o => { o.Enabled = true; o.UploadOnlyEnabled = true; })`.
For intake alone, leave `ProcessingEnabled` false and register no processor or capability.
Upload-only requires Enabled and defaults off; the amendment permits explicit
ProcessingEnabled coexistence when a host supplies processing capability.

Apply all packaged migrations, including AddUploadOnlyPhotoIntake,
PinPhotoUploadSourceDigest and AddPhotoIntakeRetention.
`IEmbeddedPhotoUploadReadiness.CheckAsync` verifies explicit upload-only settings,
PostgreSQL schema/column shapes, storage reachability and policy without invoking a
decoder. The full `IEmbeddedPhotoReadiness` retains its capability prerequisite.
Neither readiness surface substitutes for the authenticated handlers or host grants.

Supply Begin's init-only `ExpectedSourceSha256` to pin the selected bytes (64 hex
characters, stored lowercase); omission remains compatible with existing callers.
Changed keyed intent conflicts, and completion rejects a mismatched streamed hash.
`GetEmbeddedPhotoUploadIntentRequest(workspace, asset, beginOperationKey)` recovers a
lost Begin or completion acknowledgement without allocating or opening a source.
It uses fresh ReadUploadOutcome authority for the exact asset. A denied or uncertain
lookup must not be interpreted as permission to restart.

Begin persists the mode. Later settings do not enroll the original in processing.
Revision creation rejects unenrolled upload-only originals even if a capability
is registered later. Use the explicit enrollment contract above; hold release
remains deferred and enrollment preserves the existing intake hold.
Upload-only completion atomically commits original, immutable receipt, initial
revision and an internal durable intake hold. The revision is AwaitingProcessing
(value 5), ProcessorVersion is null, and no job/manifest exists. Existing enum values
remain unchanged. The hold has no release API and prevents logical/physical deletion
regardless of host confirmation or settings changes. Expired unacknowledged attempts
can retire under the existing fences without deleting an acknowledged original.
The hold is asset-wide: unsuccessful attempt objects on an asset that later
completes are conservatively retained too. Cleanup of those extra objects remains
deferred; account for this storage cost until carefully fenced cleanup is designed.

Originals remain private opaque bytes; receiving them establishes no image-format,
pixel, orientation, HDR or native-safety qualification. Serve them only through
authorized original reads with safe attachment headers; use no public static URL.

## Operations and identity

All thirteen named handlers are in `Cmsify.Core.EmbeddedMedia`. They return Common
results and detached records. No HTTP or CMS table access is required:

| Purpose | Handler stem |
| --- | --- |
| Reserve, stream, recover upload | BeginEmbeddedPhotoUpload, WriteEmbeddedPhotoUpload, GetEmbeddedPhotoUploadOutcome, GetEmbeddedPhotoUploadIntent |
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

Qualified-mode original/receipt/initial revision/job acknowledgement is one CMS transaction.
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
headers test orchestration only. Real process-kill tests cover Infrastructure and the upload-only package consumer. The fixture file journal is disposable demonstration code, not a product
retention service or disaster-proof filesystem backup design.

The runner neither publishes packages nor upgrades hosts. Qualify deployment
and actual phone images separately before release/adoption.
