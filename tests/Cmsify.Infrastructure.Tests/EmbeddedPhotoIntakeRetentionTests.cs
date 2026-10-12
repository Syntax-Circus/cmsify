using System.Security.Cryptography;
using System.Diagnostics;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Storage;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPhotoIntakeRetentionTests
{
    private static CancellationToken Ct => EmbeddedPhotoFixture.Ct;
    private static readonly Guid Actor = Guid.NewGuid();
    private static Task<bool> Permit(EmbeddedPhotoOperation _, CancellationToken ct) => Task.FromResult(true);

    [Fact]
    public async Task GenericDeleteWithCmsWriterRetainsIntake()
    {
        await using var h = await Harness.Create();
        var outcome = await h.Upload();
        await using var fresh = h.Fixture.Context();
        var asset = await fresh.MediaAssets.AsNoTracking().SingleAsync(Ct);
        var deletion = await new MediaAssetDeletionRepository(fresh, Options.Create(new MediaOperationalOptions()), TimeProvider.System)
            .DeleteAsync(new(h.WorkspaceId, asset.Id, asset.UpdatedAt.UtcTicks), Actor, _ => Task.FromResult(true), Ct);
        deletion.IsFailure.ShouldBeTrue("Upload-only acknowledgement must persist a retention hold, without a Ready manifest.");
        deletion.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Retained);
        (await fresh.MediaAssets.AsNoTracking().SingleAsync(Ct)).IsDeleted.ShouldBeFalse();
        await h.AssertBytes(fresh, outcome);
    }

    [Fact]
    public async Task QueuedPurgeAndOrphanScanCannotDeleteHeldOriginal()
    {
        await using var h = await Harness.Create();
        var outcome = await h.Upload();
        var original = await h.Db.PhotoOriginals.SingleAsync(Ct);
        h.Db.MediaDeletionIntents.Add(new MediaDeletionIntent { MediaAssetId = original.AssetId, Provider = "local",
            StorageKey = original.StorageKey, Reason = "orphan", NotBefore = DateTimeOffset.UtcNow.AddMinutes(-1), NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await h.Db.SaveChangesAsync(Ct);
        var repository = new MediaReconciliationRepository(h.Db);
        var claims = await repository.ClaimDeletionIntentsAsync("retention-test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), 20, Ct);
        claims.Count.ShouldBe(1);
        (await repository.PrepareDeletionAsync(claims[0], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), Ct))
            .ShouldBe(DeletionPreparationResult.Retained);
        var asset = await h.Db.MediaAssets.SingleAsync(Ct);
        asset.TransitionBlobState(MediaBlobState.DeletePending, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1));
        asset.IsDeleted = true;
        var queued = await h.Db.MediaDeletionIntents.SingleAsync(Ct);
        queued.Reason = "user_delete";
        queued.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await h.Db.SaveChangesAsync(Ct);
        var storage = new DeleteCounter(h.Provider);
        var processor = new MediaReconciliationProcessor(repository, storage, Options.Create(new MediaOperationalOptions()), "local", NullLogger<MediaReconciliationProcessor>.Instance);
        await processor.RunCycleAsync("retention-purge", DateTimeOffset.UtcNow.AddMinutes(6), Ct);
        storage.DeleteCalls.ShouldBe(0);
        await h.AssertBytes(h.Db, outcome);
    }

    [Fact]
    public async Task ExpiredUnacknowledgedAttemptRetiresWithoutTouchingOriginal()
    {
        await using var h = await Harness.Create();
        var outcome = await h.Upload();
        var failedBegin = (await h.UploadRepository(h.Db).BeginAsync(h.Begin(), Actor, Permit, Ct)).Value;
        (await h.UploadRepository(h.Db).WriteAsync(new(h.WorkspaceId, failedBegin.SessionId, Guid.NewGuid(), new Source([1])), Actor, Permit, Ct)).IsFailure.ShouldBeTrue();
        var failed = await h.Db.PhotoUploadAttempts.SingleAsync(x => x.SessionId == failedBegin.SessionId, Ct);
        failed.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await h.Db.SaveChangesAsync(Ct);
        var repository = new MediaReconciliationRepository(h.Db);
        var now = DateTimeOffset.UtcNow.AddHours(1);
        await repository.FailStaleUploadsAsync(now.AddMinutes(-5), now, 20, Ct);
        (await h.Db.PhotoUploadAttempts.AsNoTracking().SingleAsync(x => x.Id == failed.Id, Ct)).RetiredAt.ShouldNotBeNull();
        await h.AssertBytes(h.Db, outcome);
    }

    [Fact]
    public async Task AcknowledgementAndHoldCommitAtomically()
    {
        await using var h = await Harness.Create();
        var committed = await h.Upload();
        var heldReceipt = await h.Db.Database.SqlQuery<Guid>($"SELECT upload_receipt_id AS \"Value\" FROM embedded_photo_intake_retentions WHERE asset_id = {committed.AssetId}").SingleAsync(Ct);
        heldReceipt.ShouldBe(committed.Receipt!.ReceiptId);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>(h.Fixture.Options).AddInterceptors(new FailReceiptSave()).Options;
        await using var faulted = new CmsifyDbContext(options);
        var begin = h.Begin();
        var started = await h.UploadRepository(faulted).BeginAsync(begin, Actor, Permit, Ct);
        started.IsSuccess.ShouldBeTrue();
        await Should.ThrowAsync<IOException>(() => h.UploadRepository(faulted).WriteAsync(new(h.WorkspaceId, started.Value.SessionId,
            Guid.NewGuid(), new Source([1,2,3])), Actor, Permit, Ct));
        await using var fresh = h.Fixture.Context();
        (await fresh.PhotoUploadReceipts.CountAsync(Ct)).ShouldBe(1);
        (await fresh.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM embedded_photo_intake_retentions").SingleAsync(Ct)).ShouldBe(1);
        (await fresh.PhotoOriginals.CountAsync(x => x.AssetId == begin.AssetId, Ct)).ShouldBe(0);
        (await fresh.MediaAssets.AsNoTracking().SingleAsync(x => x.Id == begin.AssetId, Ct)).BlobState.ShouldBe(MediaBlobState.PendingUpload);
    }

    [Fact]
    public async Task CrashAfterReceiptBeforeHostConfirmRetainsBytes()
    {
        await using var h = await Harness.Create();
        Directory.CreateDirectory(h.Root);
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Cmsify.Photo.CrashHost/bin/Release/net10.0/Cmsify.Photo.CrashHost.dll"));
        File.Exists(host).ShouldBeTrue();
        var asset = Guid.NewGuid(); var operation = Guid.NewGuid(); var marker = Path.Combine(h.Root, "intake.marker");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { host, "upload-only-after-receipt", h.Fixture.ConnectionString, h.Root, h.WorkspaceId.ToString(), asset.ToString(), operation.ToString(), Guid.NewGuid().ToString(), Actor.ToString(), marker })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(Ct); var stderr = child.StandardError.ReadToEndAsync(Ct);
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(25, Ct);
            File.Exists(marker).ShouldBeTrue("Upload-only crash host must reach acknowledged-receipt boundary before termination.");
            child.HasExited.ShouldBeFalse(); child.Kill(true); await child.WaitForExitAsync(Ct); child.ExitCode.ShouldNotBe(0);
            _ = await stdout; _ = await stderr;
            await using var fresh = h.Fixture.Context();
            var recovered = await h.UploadRepository(fresh).GetIntentAsync(new(h.WorkspaceId, asset, operation), Actor, Permit, Ct);
            recovered.IsSuccess.ShouldBeTrue();
            recovered.Value.Receipt!.ReceiptId.ShouldBe(Guid.Parse(await File.ReadAllTextAsync(Path.Combine(h.Root, "receipt-id"), Ct)));
            (await fresh.Database.SqlQuery<Guid>($"SELECT upload_receipt_id AS \"Value\" FROM embedded_photo_intake_retentions WHERE asset_id = {asset}").SingleAsync(Ct)).ShouldBe(recovered.Value.Receipt.ReceiptId);
            (await fresh.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
            await h.AssertBytes(fresh, recovered.Value);
            var current = await fresh.MediaAssets.AsNoTracking().SingleAsync(Ct);
            var deletion = await new MediaAssetDeletionRepository(fresh, Options.Create(new MediaOperationalOptions()), TimeProvider.System)
                .DeleteAsync(new(h.WorkspaceId, asset, current.UpdatedAt.UtcTicks), Actor, _ => Task.FromResult(true), Ct);
            deletion.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Retained);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(Ct); }
            _ = await stdout; _ = await stderr;
        }
    }

    [Fact]
    public async Task CompletionVersusDeletionSerializes()
    {
        await using var h = await Harness.Create();
        var begin = h.Begin();
        var started = await h.UploadRepository(h.Db).BeginAsync(begin, Actor, Permit, Ct);
        var gate = new CompletionGate(h.Provider);
        var writer = new EmbeddedPhotoUploadRepository(h.Db, new(gate, "local"),
            Options.Create(new EmbeddedPhotoOptions { Enabled = true, UploadOnlyEnabled = true }), new MissingCapability(), TimeProvider.System);
        var completing = writer.WriteAsync(new(h.WorkspaceId, started.Value.SessionId, Guid.NewGuid(), new Source([1,2,3])), Actor, Permit, Ct);
        await gate.Entered.Task.WaitAsync(Ct);
        try
        {
            await using var deleting = h.Fixture.Context();
            var current = await deleting.MediaAssets.AsNoTracking().SingleAsync(Ct);
            var deletion = new MediaAssetDeletionRepository(deleting, Options.Create(new MediaOperationalOptions()), TimeProvider.System)
                .DeleteAsync(new(h.WorkspaceId, begin.AssetId, current.UpdatedAt.UtcTicks), Actor, _ => Task.FromResult(true), Ct);
            gate.Resume.TrySetResult();
            var acknowledged = await completing;
            acknowledged.IsSuccess.ShouldBeTrue(); (await deletion).IsFailure.ShouldBeTrue();
            await using var fresh = h.Fixture.Context();
            (await fresh.MediaAssets.AsNoTracking().SingleAsync(Ct)).IsDeleted.ShouldBeFalse();
            await h.AssertBytes(fresh, acknowledged.Value);
        }
        finally { gate.Resume.TrySetResult(); }
    }

    private sealed class DeleteCounter(IStorageProvider inner) : IStorageProvider
    {
        public int DeleteCalls { get; private set; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { DeleteCalls++; return inner.DeleteAsync(key, cancellationToken); }
        public Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default) => inner.GetMetadataAsync(key, cancellationToken);
        public Task<StoredObject> StoreAsync(StoreObjectRequest request, CancellationToken cancellationToken = default) => inner.StoreAsync(request, cancellationToken);
        public Task<StorageReadResult?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => inner.ExistsAsync(key, cancellationToken);
        public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest request, CancellationToken cancellationToken = default) => inner.ListAsync(request, cancellationToken);
        public Task<string> GetAccessUrlAsync(string key, TimeSpan? expiry = null, CancellationToken cancellationToken = default) => inner.GetAccessUrlAsync(key, expiry, cancellationToken);
    }

    private sealed class CompletionGate(IStorageProvider inner) : IStorageProvider
    {
        private int _metadataCalls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
        {
            var result = await inner.GetMetadataAsync(key, cancellationToken);
            if (++_metadataCalls == 2) { Entered.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); }
            return result;
        }
        public Task<StoredObject> StoreAsync(StoreObjectRequest request, CancellationToken cancellationToken = default) => inner.StoreAsync(request, cancellationToken);
        public Task<StorageReadResult?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => inner.ExistsAsync(key, cancellationToken);
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
        public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest request, CancellationToken cancellationToken = default) => inner.ListAsync(request, cancellationToken);
        public Task<string> GetAccessUrlAsync(string key, TimeSpan? expiry = null, CancellationToken cancellationToken = default) => inner.GetAccessUrlAsync(key, expiry, cancellationToken);
    }

    private sealed class FailReceiptSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PhotoUploadReceiptRecord>().Any(x => x.State == EntityState.Added))
                throw new IOException("Injected failure before original/receipt/hold commit.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public EmbeddedPhotoFixture Fixture { get; private init; } = null!;
        public CmsifyDbContext Db { get; private init; } = null!;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cmsify-intake-retention", Guid.NewGuid().ToString("N"));
        public LocalFileStorageProvider Provider { get; private set; } = null!;
        public Guid WorkspaceId => Fixture.Workspace.Id;
        public EmbeddedPhotoUploadRepository UploadRepository(CmsifyDbContext db) => new(db,
            new EmbeddedPhotoStorage(Provider, "local"), Options.Create(new EmbeddedPhotoOptions { Enabled = true, UploadOnlyEnabled = true }),
            new MissingCapability(), TimeProvider.System);
        public BeginEmbeddedPhotoUploadRequest Begin() => new(WorkspaceId, Guid.NewGuid(), Guid.NewGuid(), "phone.heic", 3, "phone-sdr-webp-v1");
        public async Task<EmbeddedPhotoUploadOutcome> Upload()
        {
            var started = await UploadRepository(Db).BeginAsync(Begin(), Actor, Permit, Ct);
            started.IsSuccess.ShouldBeTrue();
            var completed = await UploadRepository(Db).WriteAsync(new(WorkspaceId, started.Value.SessionId, Guid.NewGuid(), new Source([1,2,3])), Actor, Permit, Ct);
            completed.IsSuccess.ShouldBeTrue(); return completed.Value;
        }
        public async Task AssertBytes(CmsifyDbContext db, EmbeddedPhotoUploadOutcome outcome)
        {
            var original = await db.PhotoOriginals.AsNoTracking().SingleAsync(x => x.AssetId == outcome.AssetId, Ct);
            var stored = await Provider.ReadAsync(original.StorageKey, Ct);
            stored.ShouldNotBeNull();
            await using var bytes = stored.Content;
            Convert.ToHexString(await SHA256.HashDataAsync(bytes, Ct)).ShouldBe(outcome.Receipt!.SourceSha256);
        }
        public static async Task<Harness> Create()
        {
            var fixture = await EmbeddedPhotoFixture.Create(); var h = new Harness { Fixture = fixture, Db = fixture.Context() };
            h.Provider = new(Options.Create(new LocalStorageOptions { RootPath = h.Root })); return h;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); await Fixture.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class MissingCapability : IEmbeddedPhotoProcessorCapability
    {
        public Task<EmbeddedPhotoCapability> GetAsync(CancellationToken ct) => Task.FromResult(new EmbeddedPhotoCapability(false, false, "", "", "", "missing"));
    }
    private sealed class Source(byte[] bytes) : IEmbeddedPhotoByteSource
    {
        public ValueTask<Stream> OpenReadAsync(CancellationToken ct) => ValueTask.FromResult<Stream>(new MemoryStream(bytes));
    }
}
