using System.Security.Cryptography;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPhotoUploadOnlyTests
{
    private static CancellationToken Ct => EmbeddedPhotoFixture.Ct;
    private static readonly Guid Actor = Guid.NewGuid();
    private static Task<bool> Permit(EmbeddedPhotoOperation _, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }

    [Fact]
    public async Task UploadOnlyWithoutCapability_AcknowledgesOriginalWithoutJob()
    {
        await using var h = await Harness.Create();
        var begin = h.Begin();
        var started = await h.Repository(h.Db).BeginAsync(begin, Actor, Permit, Ct);
        started.IsSuccess.ShouldBeTrue("Explicit upload-only intake must not require a qualified decoder.");
        var result = await h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, started.Value.SessionId,
            Guid.NewGuid(), new Source([1, 2, 3])), Actor, Permit, Ct);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Receipt.ShouldNotBeNull();
        result.Value.Receipt.InitialRevision.ShouldBe(1);
        result.Value.Receipt.SourceSha256.ShouldBe(Convert.ToHexString(SHA256.HashData([1, 2, 3])));
        var revision = await h.Db.PhotoRevisions.SingleAsync(Ct);
        revision.State.ToString().ShouldBe("AwaitingProcessing");
        revision.ProcessorVersion.ShouldBeNull();
        (await h.Db.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
        (await h.Db.PhotoManifests.CountAsync(Ct)).ShouldBe(0);
        await using var restarted = h.Fixture.Context();
        var recovered = await h.Repository(restarted).GetOutcomeAsync(new(h.WorkspaceId, started.Value.SessionId), Actor, Permit, Ct);
        recovered.Value.Receipt!.ReceiptId.ShouldBe(result.Value.Receipt.ReceiptId);
        h.Capability.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task DefaultModeStillRequiresQualifiedCapability()
    {
        await using var h = await Harness.Create();
        h.Settings.UploadOnlyEnabled = false;
        (await h.Repository(h.Db).BeginAsync(h.Begin(), Actor, Permit, Ct)).IsFailure.ShouldBeTrue();
        (await h.Db.PhotoUploadSessions.CountAsync(Ct)).ShouldBe(0);
        h.Capability.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task SettingsChangeDoesNotEnrollExistingOriginal()
    {
        await using var h = await Harness.Create();
        var started = await h.Repository(h.Db).BeginAsync(h.Begin(), Actor, Permit, Ct);
        started.IsSuccess.ShouldBeTrue();
        h.Settings.UploadOnlyEnabled = false;
        var completed = await h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, started.Value.SessionId,
            Guid.NewGuid(), new Source([1, 2, 3])), Actor, Permit, Ct);
        completed.IsSuccess.ShouldBeTrue();
        (await h.Db.PhotoRevisions.SingleAsync(Ct)).State.ToString().ShouldBe("AwaitingProcessing");
        (await h.Db.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task BeginDigestConflictAndCompletionMismatchReject()
    {
        await using var h = await Harness.Create();
        var begin = h.Begin() with { ExpectedSourceSha256 = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])) };
        var started = await h.Repository(h.Db).BeginAsync(begin, Actor, Permit, Ct);
        started.IsSuccess.ShouldBeTrue();
        var changed = begin with { ExpectedSourceSha256 = Convert.ToHexStringLower(SHA256.HashData([4, 5, 6])) };
        (await h.Repository(h.Db).BeginAsync(changed, Actor, Permit, Ct)).IsFailure.ShouldBeTrue();
        var wrong = await h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, started.Value.SessionId,
            Guid.NewGuid(), new Source([4, 5, 6])), Actor, Permit, Ct);
        wrong.IsFailure.ShouldBeTrue();
        (await h.Db.PhotoOriginals.CountAsync(Ct)).ShouldBe(0);
        (await h.Db.PhotoUploadReceipts.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task LookupRecoversLostBeginWithoutAllocating()
    {
        await using var h = await Harness.Create();
        var begin = h.Begin();
        var started = await h.Repository(h.Db).BeginAsync(begin, Actor, Permit, Ct);
        started.IsSuccess.ShouldBeTrue();
        await using var restarted = h.Fixture.Context();
        var result = await h.Repository(restarted).GetIntentAsync(new(h.WorkspaceId, begin.AssetId, begin.OperationKey), Actor, Permit, Ct);
        result.IsSuccess.ShouldBeTrue(); result.Value.SessionId.ShouldBe(started.Value.SessionId);
        (await restarted.PhotoUploadSessions.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task UploadReadinessIsIndependentOfMissingCapability()
    {
        await using var h = await Harness.Create();
        var result = await new EmbeddedPhotoUploadReadiness(h.Db, h.Provider, Options.Create(h.Settings)).CheckAsync(Ct);
        result.Ready.ShouldBeTrue();
        var processing = await new EmbeddedPhotoReadiness(h.Db, h.Provider, h.Capability, Options.Create(h.Settings)).CheckAsync(Ct);
        processing.Ready.ShouldBeFalse(); processing.Reasons.ShouldContain("capability-unavailable");
    }

    [Fact]
    public async Task LookupDeniedNeverReadsSourceAndMissingDoesNotAllocate()
    {
        await using var h = await Harness.Create();
        var begin = h.Begin();
        var started = await h.Repository(h.Db).BeginAsync(begin, Actor, Permit, Ct);
        var denied = await h.Repository(h.Db).GetIntentAsync(new(h.WorkspaceId, begin.AssetId, begin.OperationKey), Actor,
            (_, _) => Task.FromResult(false), Ct);
        denied.Errors[0].Kind.ShouldBe(SyntaxCircus.Common.ResultErrorKind.Forbidden);
        var missing = await h.Repository(h.Db).GetIntentAsync(new(h.WorkspaceId, Guid.NewGuid(), Guid.NewGuid()), Actor, Permit, Ct);
        missing.Errors[0].Kind.ShouldBe(SyntaxCircus.Common.ResultErrorKind.NotFound);
        (await h.Db.PhotoUploadSessions.CountAsync(Ct)).ShouldBe(1);
        var calls = 0;
        var revoked = await h.Repository(h.Db).GetIntentAsync(new(h.WorkspaceId, begin.AssetId, begin.OperationKey), Actor,
            (_, _) => Task.FromResult(++calls < 3), Ct);
        revoked.IsFailure.ShouldBeTrue("Authority revoked after receipt lookup must suppress the returned outcome.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        public EmbeddedPhotoFixture Fixture { get; private init; } = null!;
        public CmsifyDbContext Db { get; private init; } = null!;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cmsify-upload-only", Guid.NewGuid().ToString("N"));
        public LocalFileStorageProvider Provider { get; private set; } = null!;
        public EmbeddedPhotoOptions Settings { get; } = new() { Enabled = true, PolicyId = "phone-sdr-webp-v1" };
        public MissingCapability Capability { get; } = new();
        public Guid WorkspaceId => Fixture.Workspace.Id;
        public EmbeddedPhotoUploadRepository Repository(CmsifyDbContext db) =>
            new(db, new EmbeddedPhotoStorage(Provider, "local"), Options.Create(Settings), Capability, TimeProvider.System);
        public BeginEmbeddedPhotoUploadRequest Begin() => new(WorkspaceId, Guid.NewGuid(), Guid.NewGuid(), "phone.heic", 3, Settings.PolicyId);
        public static async Task<Harness> Create()
        {
            var fixture = await EmbeddedPhotoFixture.Create();
            var h = new Harness { Fixture = fixture, Db = fixture.Context() };
            h.Settings.UploadOnlyEnabled = true;
            h.Provider = new(Options.Create(new LocalStorageOptions { RootPath = h.Root }));
            return h;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); await Fixture.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class MissingCapability : IEmbeddedPhotoProcessorCapability
    {
        public int Calls { get; private set; }
        public Task<EmbeddedPhotoCapability> GetAsync(CancellationToken ct)
        { Calls++; return Task.FromResult(new EmbeddedPhotoCapability(false, false, "", "", "", "capability-unavailable")); }
    }
    private sealed class Source(byte[] bytes) : IEmbeddedPhotoByteSource
    {
        public int Opens { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Opens++; return ValueTask.FromResult<Stream>(new MemoryStream(bytes)); }
    }
}
