using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;
using static Cmsify.Infrastructure.Tests.Fixtures.EmbeddedPhotoProcessingFixture;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPngCicpProcessingTests
{
    private const string Policy = "png-cicp-sdr-webp-v1";
    [Fact]
    public async Task ExplicitOptInPreservesLegacyManifestAndOriginalAcrossFreshContext()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); var asset = await f.Upload();
        CreateEmbeddedPhotoRevisionRequest request;
        string legacyHash;
        await using (var db = f.Context())
        {
            (await f.Worker(db,new CandidateProcessor(pngOriginal:true,originalBitDepth:8)).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
            legacyHash = (await db.PhotoManifests.AsNoTracking().SingleAsync(Ct)).ManifestSha256;
            request = new(f.WorkspaceId,asset,Guid.NewGuid(),1,new(0),Policy);
            (await f.Repository(db).CreateRevisionAsync(request,f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
            f.Settings.PngCicpSdrEnabled = true;
            (await f.Repository(db).CreateRevisionAsync(request,f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        }
        await using (var db = f.Context())
        {
            (await f.Repository(db).CreateRevisionAsync(request,f.ActorId,Permit,Ct)).Value.Revision.ShouldBe(2);
            (await f.Worker(db,new CandidateProcessor(originalBitDepth:8)).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
            var current = await db.PhotoManifests.AsNoTracking().SingleAsync(x => x.Revision == 2,Ct);
            f.Settings.PngCicpSdrEnabled = false;
            (await f.Repository(db).GetManifestAsync(new(f.WorkspaceId,asset,1,legacyHash),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
            (await f.Repository(db).GetManifestAsync(new(f.WorkspaceId,asset,2,current.ManifestSha256),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
            (await db.PhotoVariants.CountAsync(Ct)).ShouldBe(7);
            (await db.PhotoOriginals.CountAsync(Ct)).ShouldBe(1);
            db.Database.HasPendingModelChanges().ShouldBeFalse();
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TamperedConversionOrValidDimensionsDoNotPassManifestIntegrity(bool dimensions)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); f.Settings.PngCicpSdrEnabled = true;
        var asset = await f.Upload(); await using var db = f.Context();
        await f.Worker(db,new FailingProcessor(EmbeddedPhotoErrors.Unsupported)).ProcessNextAsync(f.ActorId,Ct);
        (await f.Repository(db).CreateRevisionAsync(new(f.WorkspaceId,asset,Guid.NewGuid(),1,new(0),Policy),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
        var manifest = await db.PhotoManifests.SingleAsync(Ct);
        var metadata = JsonSerializer.Deserialize<EmbeddedPhotoSourceMetadata>(manifest.MetadataJson!)!;
        manifest.MetadataJson = JsonSerializer.Serialize(dimensions ? metadata with { Width = metadata.Width+1 } : metadata with { Conversion = new("wrong-version",16) });
        await db.SaveChangesAsync(Ct);
        (await f.Repository(db).GetManifestAsync(new(f.WorkspaceId,asset,2,manifest.ManifestSha256),f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
    }
    [Theory]
    [InlineData("unavailable")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task AdapterOutageLeavesP3QueuedAndLegacyWorkContinues(string failure)
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); f.Settings.PngCicpSdrEnabled = true;
        var p3 = await f.Upload();
        await using (var db = f.Context())
        {
            await f.Worker(db,new FailingProcessor(EmbeddedPhotoErrors.Unsupported)).ProcessNextAsync(f.ActorId,Ct);
            (await f.Repository(db).CreateRevisionAsync(new(f.WorkspaceId,p3,Guid.NewGuid(),1,new(0),Policy),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        }
        var legacy = await f.Upload(); f.Capability.PngCicpAvailable = false; f.Capability.ThrowForPngCicp = failure == "throw";
        f.Capability.CancelForPngCicp = failure == "cancel";
        await using (var db = f.Context())
        {
            (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
            (await f.Repository(db).GetAsync(new(f.WorkspaceId,legacy,1),f.ActorId,Permit,Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Ready);
            (await f.Repository(db).GetAsync(new(f.WorkspaceId,p3,2),f.ActorId,Permit,Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Queued);
            (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeFalse();
        }
        f.Capability.PngCicpAvailable = true; f.Capability.ThrowForPngCicp = false; f.Capability.CancelForPngCicp = false;
        await using (var db = f.Context()) (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
    }
    [Fact]
    public async Task CallerCancellationPropagatesWithoutClaimingQueuedWork()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); f.Settings.PngCicpSdrEnabled = true;
        await f.Upload(); f.Capability.CancelForPngCicp = true;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await using var db = f.Context();
        await Should.ThrowAsync<OperationCanceledException>(() => f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,canceled.Token));
        var job = await db.PhotoJobs.AsNoTracking().SingleAsync(Ct);
        job.State.ShouldBe(EmbeddedPhotoState.Queued); job.AutomaticAttemptCount.ShouldBe(0);
    }
    [Fact]
    public async Task UploadOnlyEnrollmentBindsSourceWorkspaceAndReplayIntent()
    {
        await using var f = await EmbeddedPhotoProcessingFixture.Create(); f.Settings.PngCicpSdrEnabled = true; f.Settings.UploadOnlyEnabled = true;
        var asset = await f.Upload(); await using var db = f.Context();
        var receipt = await db.PhotoUploadReceipts.AsNoTracking().SingleAsync(Ct); var original = await db.PhotoOriginals.AsNoTracking().SingleAsync(Ct);
        var request = new EnrollEmbeddedPhotoUploadRequest(f.WorkspaceId,asset,receipt.Id,original.SourceSha256,1,Guid.NewGuid(),Policy);
        (await f.Repository(db).EnrollAsync(request with { SourceSha256 = new string('a',64) },f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
        (await f.Repository(db).EnrollAsync(request with { WorkspaceId = Guid.NewGuid() },f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
        (await f.Repository(db).EnrollAsync(request,f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        (await f.Repository(db).EnrollAsync(request with { PolicyId = EmbeddedPhotoPolicy.PngNormalizationPolicyId },f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
        (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
        (await db.PhotoVariants.CountAsync(Ct)).ShouldBe(4); (await db.PhotoOriginals.CountAsync(Ct)).ShouldBe(1);
    }
}
