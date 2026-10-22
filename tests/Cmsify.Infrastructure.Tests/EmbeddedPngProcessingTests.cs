using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;
using static Cmsify.Infrastructure.Tests.Fixtures.EmbeddedPhotoProcessingFixture;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPngProcessingTests
{
    [Fact]
    public async Task MixedQueueKeepsLegacyWorkAvailableDuringPngAdapterOutage()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        f.Settings.PngNormalizationEnabled = true;
        var png = await f.Upload();
        await using (var db = f.Context())
        {
            await f.Worker(db, new FailingProcessor(EmbeddedPhotoErrors.Unsupported)).ProcessNextAsync(f.ActorId, Ct);
            (await f.Repository(db).CreateRevisionAsync(new(f.WorkspaceId, png, Guid.NewGuid(), 1, new(0), EmbeddedPhotoPolicy.PngNormalizationPolicyId), f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        }
        var legacy = await f.Upload(); f.Capability.PngAvailable = false;
        await using (var db = f.Context())
        {
            (await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
            (await f.Repository(db).GetAsync(new(f.WorkspaceId, legacy, 1), f.ActorId, Permit, Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Ready);
            (await f.Repository(db).GetAsync(new(f.WorkspaceId, png, 2), f.ActorId, Permit, Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Queued);
            (await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeFalse();
        }
        f.Capability.PngAvailable = true;
        await using (var db = f.Context())
        {
            (await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
            (await f.Repository(db).GetAsync(new(f.WorkspaceId, png, 2), f.ActorId, Permit, Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Ready);
        }
    }

    [Fact]
    public async Task Png16RevisionRetainsOriginalDepthAndOldReadPreview()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        f.Settings.PngNormalizationEnabled = true;
        EmbeddedPhotoManifestIdentity before;
        await using (var db = f.Context())
        {
            await f.Worker(db, new FailingProcessor(EmbeddedPhotoErrors.Unsupported)).ProcessNextAsync(f.ActorId, Ct);
            (await f.Repository(db).CreateRevisionAsync(new(f.WorkspaceId, asset, Guid.NewGuid(), 1, new(0), EmbeddedPhotoPolicy.PngNormalizationPolicyId), f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
            await f.Worker(db, new CandidateProcessor(width:6063,height:4639)).ProcessNextAsync(f.ActorId, Ct);
            var manifest = await db.PhotoManifests.AsNoTracking().SingleAsync(Ct);
            before = new(asset,2,manifest.ManifestSha256);
            (await f.Repository(db).CreateRevisionAsync(new(f.WorkspaceId, asset, Guid.NewGuid(), 2, new(1), EmbeddedPhotoPolicy.PngNormalizationPolicyId), f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
            // Interrupted processing leaves its lease durable; recovery uses ordinary bounded retry.
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var gate=new GateProcessor();
            var attempt=f.Worker(db,gate).ProcessNextAsync(f.ActorId,cancellation.Token);
            await gate.Started.Task.WaitAsync(Ct); cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(()=>attempt);
        }
        f.Time.Now += f.Settings.ProcessingLeaseDuration + TimeSpan.FromSeconds(1);
        await using (var db = f.Context())
        {
            (await f.Worker(db, new CandidateProcessor(width:6063,height:4639)).ProcessNextAsync(f.ActorId,Ct)).ShouldBeFalse();
            f.Time.Now += TimeSpan.FromSeconds(5);
            (await f.Worker(db, new CandidateProcessor(width:6063,height:4639)).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
            var rotated = await db.PhotoManifests.AsNoTracking().SingleAsync(x=>x.Revision==3,Ct);
            var metadata=JsonSerializer.Deserialize<EmbeddedPhotoSourceMetadata>(rotated.MetadataJson!)!;
            metadata.Conversion!.OriginalBitDepth.ShouldBe(16); metadata.Width.ShouldBe(6063); metadata.Height.ShouldBe(4639);
            var preview=await db.PhotoVariants.AsNoTracking().SingleAsync(x=>x.Revision==3 && x.VariantKey=="4096",Ct);
            preview.Height.ShouldBeGreaterThan(preview.Width);
            (await f.Repository(db).GetManifestAsync(new(f.WorkspaceId,asset,2,before.ManifestSha256),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
            (await db.PhotoOriginals.CountAsync(Ct)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task DisabledPngDoesNotInvokeAnUninstalledOptionalAdapter()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); await f.Upload();
        f.Capability.ThrowForPng = true;
        await using var db = f.Context();
        (await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        (await db.PhotoRevisions.AsNoTracking().SingleAsync(Ct)).State.ShouldBe(EmbeddedPhotoState.Ready);
    }

    [Fact]
    public async Task ExplicitNewPolicyKeepsLegacyQueueAndRetainedReadsAcrossRestart()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create();
        var asset = await f.Upload();
        f.Settings.PngNormalizationEnabled = true;
        EmbeddedPhotoManifestIdentity legacy;
        CreateEmbeddedPhotoRevisionRequest request = new(f.WorkspaceId, asset, Guid.NewGuid(), 1, new(0), EmbeddedPhotoPolicy.PngNormalizationPolicyId);
        await using (var db = f.Context())
        {
            (await f.Worker(db, new CandidateProcessor(pngOriginal: true)).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
            var manifest = await db.PhotoManifests.AsNoTracking().SingleAsync(Ct);
            legacy = new(asset, 1, manifest.ManifestSha256);
            (await f.Repository(db).CreateRevisionAsync(request, f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        }
        // Recreate context/worker to model process restart with persisted policy and receipt.
        await using (var db = f.Context())
        {
            (await f.Worker(db, new CandidateProcessor(originalBitDepth: 8)).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
            (await f.Repository(db).CreateRevisionAsync(request, f.ActorId, Permit, Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Queued);
            (await f.Repository(db).CreateRevisionAsync(request with { OperationKey = Guid.NewGuid() }, f.ActorId, Permit, Ct)).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
            (await db.PhotoVariants.CountAsync(Ct)).ShouldBe(7);
        }
        f.Settings.PngNormalizationEnabled = false;
        await using (var db = f.Context())
        {
            var old = await f.Repository(db).GetManifestAsync(new(f.WorkspaceId, asset, 1, legacy.ManifestSha256), f.ActorId, Permit, Ct);
            old.IsSuccess.ShouldBeTrue(); old.Value.Variants.Count.ShouldBe(3);
            var record = await db.PhotoManifests.AsNoTracking().SingleAsync(x => x.Revision == 2, Ct);
            var read = new GetEmbeddedPhotoManifestRequest(f.WorkspaceId, asset, 2, record.ManifestSha256);
            var newer = await f.Repository(db).GetManifestAsync(read, f.ActorId, Permit, Ct);
            newer.IsSuccess.ShouldBeTrue(); newer.Value.Variants.Count.ShouldBe(4);
            newer.Value.SourceMetadata!.Conversion.ShouldBe(new(EmbeddedPhotoPolicy.PngNormalizationVersion, 8));
            (await f.Repository(db).GetManifestAsync(read with { WorkspaceId = Guid.NewGuid() }, f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
            var references = new EmbeddedMediaReferenceRepository(db, f.Storage, [], f.Time);
            var protect = await references.ProtectAsync(new(f.WorkspaceId, "consumer", Guid.NewGuid(), Guid.NewGuid(), [legacy, newer.Value.Identity]), f.ActorId, Permit, Ct);
            protect.IsSuccess.ShouldBeTrue();
            var reader = new EmbeddedPhotoReadRepository(db, f.Storage);
            var sink = new Sink();
            var preview = new ReadEmbeddedPhotoVariantRequest(f.WorkspaceId, newer.Value.Identity, "4096", new(EmbeddedPhotoReadKind.PrivatePreview), sink);
            (await reader.ReadVariantAsync(preview, f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
            sink.Bytes.ShouldBeGreaterThan(0);
            (await reader.ReadVariantAsync(preview with { WorkspaceId = Guid.NewGuid(), Sink = new Sink() }, f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
            var denied = new Sink();
            (await reader.ReadVariantAsync(preview with { Sink = denied }, f.ActorId, (_, _) => Task.FromResult(false), Ct)).IsFailure.ShouldBeTrue();
            denied.Bytes.ShouldBe(0);
            (await reader.ReadVariantAsync(preview with { Manifest = legacy, Sink = new Sink() }, f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
            // Turning the opt-in off prevents new work but never prevents retained reads/protection.
            (await f.Repository(db).CreateRevisionAsync(request with { ExpectedLatestRevision = 2, OperationKey = Guid.NewGuid() }, f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task ChangedProvenanceCannotBeReadOrProtectedUnderTheOriginalManifestHash()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        f.Settings.PngNormalizationEnabled = true;
        await using var db = f.Context();
        await f.Worker(db, new FailingProcessor(EmbeddedPhotoErrors.Unsupported)).ProcessNextAsync(f.ActorId, Ct);
        (await f.Repository(db).CreateRevisionAsync(new(f.WorkspaceId, asset, Guid.NewGuid(), 1, new(0), EmbeddedPhotoPolicy.PngNormalizationPolicyId), f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        await f.Worker(db, new CandidateProcessor()).ProcessNextAsync(f.ActorId, Ct);
        var record = await db.PhotoManifests.SingleAsync(Ct);
        var metadata = JsonSerializer.Deserialize<EmbeddedPhotoSourceMetadata>(record.MetadataJson!)!;
        record.MetadataJson = JsonSerializer.Serialize(metadata with { Conversion = new(EmbeddedPhotoPolicy.PngNormalizationVersion, 8) });
        await db.SaveChangesAsync(Ct);
        var identity = new EmbeddedPhotoManifestIdentity(asset, 2, record.ManifestSha256);
        (await f.Repository(db).GetManifestAsync(new(f.WorkspaceId, asset, 2, record.ManifestSha256), f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
        var sink = new Sink();
        (await new EmbeddedPhotoReadRepository(db, f.Storage).ReadVariantAsync(new(f.WorkspaceId, identity, "4096", new(EmbeddedPhotoReadKind.PrivatePreview), sink), f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
        sink.Bytes.ShouldBe(0);
        (await new EmbeddedMediaReferenceRepository(db, f.Storage, [], f.Time).ProtectAsync(new(f.WorkspaceId, "consumer", Guid.NewGuid(), Guid.NewGuid(), [identity]), f.ActorId, Permit, Ct)).IsFailure.ShouldBeTrue();
    }

    private sealed class Sink : IEmbeddedPhotoByteSink
    {
        internal long Bytes { get; private set; }
        public async Task WriteAsync(Stream source, CancellationToken ct)
        {
            using var output = new MemoryStream(); await source.CopyToAsync(output, ct); Bytes = output.Length;
        }
    }

    [Theory]
    [InlineData(null, EmbeddedPhotoState.Ready)]
    [InlineData("provenance", EmbeddedPhotoState.Rejected)]
    [InlineData("originalDepth", EmbeddedPhotoState.Rejected)]
    [InlineData("metadata", EmbeddedPhotoState.Rejected)]
    [InlineData("bitDepth", EmbeddedPhotoState.Rejected)]
    [InlineData("missing", EmbeddedPhotoState.Rejected)]
    public async Task LargerPngRequiresExactConversionAndVariantContract(string? defect, EmbeddedPhotoState state)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        f.Settings.PngNormalizationEnabled = true;
        await using var db = f.Context();
        // A terminal rejection is recovered by an explicit new immutable revision, never Retry.
        await f.Worker(db, new FailingProcessor(EmbeddedPhotoErrors.Unsupported)).ProcessNextAsync(f.ActorId, Ct);
        var request = new CreateEmbeddedPhotoRevisionRequest(f.WorkspaceId, asset, Guid.NewGuid(), 1, new(0), EmbeddedPhotoPolicy.PngNormalizationPolicyId);
        (await f.Repository(db).CreateRevisionAsync(request, f.ActorId, Permit, Ct)).IsSuccess.ShouldBeTrue();
        (await f.Worker(db, new CandidateProcessor(defect, 6063, 4639)).ProcessNextAsync(f.ActorId, Ct)).ShouldBeTrue();
        (await f.Repository(db).GetAsync(new(f.WorkspaceId, asset, 2), f.ActorId, Permit, Ct)).Value.State.ShouldBe(state);
        (await db.PhotoRevisions.AsNoTracking().SingleAsync(x => x.Revision == 1, Ct)).State.ShouldBe(EmbeddedPhotoState.Rejected);
        (await db.PhotoManifests.CountAsync(Ct)).ShouldBe(state == EmbeddedPhotoState.Ready ? 1 : 0);
    }
}
