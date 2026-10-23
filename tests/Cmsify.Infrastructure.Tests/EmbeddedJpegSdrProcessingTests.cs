using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;
using static Cmsify.Infrastructure.Tests.Fixtures.EmbeddedPhotoProcessingFixture;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedJpegSdrProcessingTests
{
    private const string Policy="jpeg-sdr-base-webp-v1";
    private static void Enable(EmbeddedPhotoProcessingFixture f)
    {
        f.Settings.JpegSdrBaseEnabled=true;
    }
    [Fact]
    public async Task OptInIsRequiredAndExactHistoricalManifestsSurviveDisable()
    {
        await using var f=await EmbeddedPhotoProcessingFixture.Create();var asset=await f.Upload();
        await using var db=f.Context();await f.Worker(db,new CandidateProcessor(jpegOriginal:true)).ProcessNextAsync(f.ActorId,Ct);
        var legacy=await db.PhotoManifests.AsNoTracking().SingleAsync(Ct);
        var request=new CreateEmbeddedPhotoRevisionRequest(f.WorkspaceId,asset,Guid.NewGuid(),1,new(0),Policy);
        (await f.Repository(db).CreateRevisionAsync(request,f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
        Enable(f);
        (await f.Repository(db).CreateRevisionAsync(request,f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
        var current=await db.PhotoManifests.AsNoTracking().SingleAsync(x=>x.Revision==2,Ct);
        f.Settings.JpegSdrBaseEnabled=false;
        foreach(var manifest in new[]{legacy,current})
            (await f.Repository(db).GetManifestAsync(new(f.WorkspaceId,asset,manifest.Revision,manifest.ManifestSha256),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        (await db.PhotoOriginals.CountAsync(Ct)).ShouldBe(1);(await db.PhotoVariants.CountAsync(Ct)).ShouldBe(6);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MixedQueueLeavesJpegQueuedWhileLegacyContinuesDuringOutage(bool throwing)
    {
        await using var f=await EmbeddedPhotoProcessingFixture.Create();Enable(f);var jpeg=await f.Upload();
        await using(var db=f.Context())
        {
            await f.Worker(db,new FailingProcessor(EmbeddedPhotoErrors.Unsupported)).ProcessNextAsync(f.ActorId,Ct);
            (await f.Repository(db).CreateRevisionAsync(new(f.WorkspaceId,jpeg,Guid.NewGuid(),1,new(0),Policy),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        }
        var legacy=await f.Upload();f.Capability.JpegAvailable=false;f.Capability.ThrowForJpeg=throwing;
        await using(var db=f.Context())
        {
            (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
            (await f.Repository(db).GetAsync(new(f.WorkspaceId,legacy,1),f.ActorId,Permit,Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Ready);
            (await f.Repository(db).GetAsync(new(f.WorkspaceId,jpeg,2),f.ActorId,Permit,Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Queued);
            (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeFalse();
        }
        f.Capability.JpegAvailable=true;f.Capability.ThrowForJpeg=false;
        await using(var db=f.Context())(await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
    }
    [Fact]
    public async Task UploadOnlyEnrollmentPinsSourceAndSurvivesFreshWorker()
    {
        await using var f=await EmbeddedPhotoProcessingFixture.Create();Enable(f);f.Settings.UploadOnlyEnabled=true;var asset=await f.Upload();
        EnrollEmbeddedPhotoUploadRequest request;
        await using(var db=f.Context())
        {
            var receipt=await db.PhotoUploadReceipts.AsNoTracking().SingleAsync(Ct);var original=await db.PhotoOriginals.AsNoTracking().SingleAsync(Ct);
            request=new(f.WorkspaceId,asset,receipt.Id,original.SourceSha256,1,Guid.NewGuid(),Policy);
            (await f.Repository(db).EnrollAsync(request,f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        }
        await using(var db=f.Context())
        {
            (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
            (await f.Repository(db).EnrollAsync(request,f.ActorId,Permit,Ct)).Value.State.ShouldBe(EmbeddedPhotoState.Queued);
            (await f.Repository(db).EnrollAsync(request with{SourceSha256=new string('b',64)},f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
            (await f.Repository(db).EnrollAsync(request with{PolicyId=f.Settings.PolicyId},f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
            (await db.PhotoOriginals.CountAsync(Ct)).ShouldBe(1);(await db.PhotoJobs.CountAsync(Ct)).ShouldBe(1);(await db.PhotoManifests.CountAsync(Ct)).ShouldBe(1);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TamperedConversionMetadataCannotBeReadAsReady(bool validButDifferentDimensions)
    {
        await using var f=await EmbeddedPhotoProcessingFixture.Create();Enable(f);f.Settings.UploadOnlyEnabled=true;var asset=await f.Upload();
        await using var db=f.Context();var receipt=await db.PhotoUploadReceipts.AsNoTracking().SingleAsync(Ct);var original=await db.PhotoOriginals.AsNoTracking().SingleAsync(Ct);
        (await f.Repository(db).EnrollAsync(new(f.WorkspaceId,asset,receipt.Id,original.SourceSha256,1,Guid.NewGuid(),Policy),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        (await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
        var manifest=await db.PhotoManifests.SingleAsync(Ct);
        var metadata=JsonSerializer.Deserialize<EmbeddedPhotoSourceMetadata>(manifest.MetadataJson!)!;
        manifest.MetadataJson=JsonSerializer.Serialize(validButDifferentDimensions?metadata with{Width=metadata.Width+1}:metadata with{Conversion=new("tampered-conversion",8)});await db.SaveChangesAsync(Ct);
        (await f.Repository(db).GetManifestAsync(new(f.WorkspaceId,asset,manifest.Revision,manifest.ManifestSha256),f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
    }
    [Fact]
    public async Task DisabledJpegAdapterIsNeverInvoked()
    {
        await using var f=await EmbeddedPhotoProcessingFixture.Create();f.Capability.ThrowForJpeg=true;await f.Upload();
        await using var db=f.Context();(await f.Worker(db,new CandidateProcessor()).ProcessNextAsync(f.ActorId,Ct)).ShouldBeTrue();
        var asset=await db.PhotoOriginals.AsNoTracking().Select(x=>x.AssetId).SingleAsync(Ct);
        (await f.Repository(db).CreateRevisionAsync(new(f.WorkspaceId,asset,Guid.NewGuid(),1,new(0),Policy),f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
    }
    [Fact]
    public async Task DisabledJpegRetryDoesNotInvokeAbsentAdapter()
    {
        await using var f=await EmbeddedPhotoProcessingFixture.Create();Enable(f);f.Settings.UploadOnlyEnabled=true;var asset=await f.Upload();
        await using var db=f.Context();var receipt=await db.PhotoUploadReceipts.AsNoTracking().SingleAsync(Ct);var original=await db.PhotoOriginals.AsNoTracking().SingleAsync(Ct);
        (await f.Repository(db).EnrollAsync(new(f.WorkspaceId,asset,receipt.Id,original.SourceSha256,1,Guid.NewGuid(),Policy),f.ActorId,Permit,Ct)).IsSuccess.ShouldBeTrue();
        for(var i=0;i<3;i++){await f.Worker(db,new FailingProcessor()).ProcessNextAsync(f.ActorId,Ct);f.Time.Now+=TimeSpan.FromSeconds(i==0?5:30);}
        f.Settings.JpegSdrBaseEnabled=false;f.Capability.ThrowForJpeg=true;
        (await f.Repository(db).RetryAsync(new(f.WorkspaceId,asset,2,Guid.NewGuid(),0),f.ActorId,Permit,Ct)).IsFailure.ShouldBeTrue();
    }
}
