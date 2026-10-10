using System.Security.Cryptography;
using System.Diagnostics;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using SyntaxCircus.Common;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests;

public sealed class EmbeddedPhotoUploadTests
{
    [Fact]
    public async Task FailedUnacknowledgedSessionRestartsSameIntentWithNewFence()
    {
        await using var h = await Harness.Create(); var begin = h.Begin(3); var repo = h.Repository(h.Db);
        var session = (await repo.BeginAsync(begin, Actor, Permit, Ct)).Value; var writeKey = Guid.NewGuid();
        (await repo.WriteAsync(new(h.WorkspaceId, session.SessionId, writeKey, new Source(() => new MemoryStream([1]))),
            Actor, Permit, Ct)).IsFailure.ShouldBeTrue();
        (await repo.GetOutcomeAsync(new(h.WorkspaceId, session.SessionId), Actor, Permit, Ct)).Value.State.ShouldBe(EmbeddedPhotoUploadState.Failed);
        var failedAttempt = await h.Db.PhotoUploadAttempts.SingleAsync(Ct);
        (await repo.BeginAsync(begin, Actor, Permit, Ct)).Value.SessionId.ShouldBe(session.SessionId);
        var restart = await repo.WriteAsync(new(h.WorkspaceId, session.SessionId, writeKey,
            new Source(() => new MemoryStream([1,2,3]))), Actor, Permit, Ct);
        restart.IsSuccess.ShouldBeTrue(); restart.Value.State.ShouldBe(EmbeddedPhotoUploadState.Completed);
        (await h.Db.PhotoUploadAttempts.CountAsync(Ct)).ShouldBe(2);
        (await h.Db.PhotoOriginals.SingleAsync(Ct)).StorageKey.ShouldNotBe(failedAttempt.StorageKey);
        (await h.Db.PhotoUploadReceipts.CountAsync(Ct)).ShouldBe(1); (await h.Db.PhotoJobs.CountAsync(Ct)).ShouldBe(1);
        var resend = new Source(() => new MemoryStream([9,9,9]));
        (await repo.WriteAsync(new(h.WorkspaceId, session.SessionId, writeKey, resend), Actor, Permit, Ct)).IsFailure.ShouldBeTrue();
        resend.OpenCount.ShouldBe(0);
    }

    private static CancellationToken Ct => EmbeddedPhotoFixture.Ct;
    private static Task<bool> Permit(EmbeddedPhotoOperation _, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }
    private static readonly Guid Actor = Guid.NewGuid();

    [Fact]
    public Task KillAfterStageLeavesReclaimableOrphan() => CrashAndRecover("after-stage", false);
    [Fact]
    public Task KillBeforeReceiptLeavesNoAcknowledgement() => CrashAndRecover("before-receipt", false);
    [Fact]
    public Task KillAfterReceiptPreservesOriginalAndQueuedJob() => CrashAndRecover("after-receipt", true);

    private static async Task CrashAndRecover(string stage, bool acknowledged)
    {
        await using var h = await Harness.Create();
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tests/Cmsify.Photo.CrashHost/bin/Release/net10.0/Cmsify.Photo.CrashHost.dll"));
        File.Exists(host).ShouldBeTrue("The real photo crash host must be built; exceptions are not a crash test.");
        Directory.CreateDirectory(h.Root);
        var asset = Guid.NewGuid(); var operation = Guid.NewGuid(); var writeKey = Guid.NewGuid();
        var marker = Path.Combine(h.Root, "crash-stage.marker");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { host, stage, h.Db.Database.GetDbConnection().ConnectionString, h.Root,
            h.WorkspaceId.ToString(), asset.ToString(), operation.ToString(), writeKey.ToString(), Actor.ToString(), marker })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(Ct);
        var stderr = child.StandardError.ReadToEndAsync(Ct);
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(marker) && !child.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(50, Ct);
            File.Exists(marker).ShouldBeTrue(child.HasExited
                ? "Crash host exited before its boundary: " + await stderr
                : "Crash host did not reach its bounded boundary.");
            child.HasExited.ShouldBeFalse();
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(Ct);
            child.ExitCode.ShouldNotBe(0);
            _ = await stdout; _ = await stderr;
            await using var restarted = h.Fixture.Context();
            var session = await restarted.PhotoUploadSessions.AsNoTracking().SingleAsync(Ct);
            var recovered = await h.Repository(restarted).GetOutcomeAsync(new(h.WorkspaceId, session.Id), Actor, Permit, Ct);
            var staged = await restarted.PhotoUploadAttempts.SingleAsync(Ct);
            (await h.Provider.ExistsAsync(staged.StorageKey, Ct)).ShouldBeTrue();
            (await restarted.PhotoUploadReceipts.CountAsync(Ct)).ShouldBe(acknowledged ? 1 : 0);
            (await restarted.PhotoOriginals.CountAsync(Ct)).ShouldBe(acknowledged ? 1 : 0);
            if (acknowledged)
            {
                recovered.Value.State.ShouldBe(EmbeddedPhotoUploadState.Completed);
                var receiptId = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(h.Root, "receipt-id"), Ct));
                recovered.Value.Receipt!.ReceiptId.ShouldBe(receiptId);
                (await restarted.PhotoJobs.SingleAsync(Ct)).State.ShouldBe(EmbeddedPhotoState.Queued);
            }
            else
            {
                recovered.Value.Receipt.ShouldBeNull();
                h.Clock.Now += TimeSpan.FromMinutes(3);
                var restart = await h.Repository(restarted).WriteAsync(new(h.WorkspaceId, session.Id, writeKey,
                    new Source(() => new MemoryStream([4,5,6]))), Actor, Permit, Ct);
                restart.IsSuccess.ShouldBeTrue();
                (await restarted.PhotoUploadAttempts.Select(x => x.StorageKey).Distinct().CountAsync(Ct)).ShouldBe(2);
                (await restarted.PhotoOriginals.SingleAsync(Ct)).StorageKey.ShouldNotBe(staged.StorageKey);
                (await restarted.PhotoJobs.CountAsync(Ct)).ShouldBe(1);
            }
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(Ct); }
        }
    }

    [Fact]
    public async Task SameIntentReturnsSameSessionChangedLengthConflicts()
    {
        await using var h = await Harness.Create();
        var request = h.Begin(3);
        var first = await h.Repository(h.Db).BeginAsync(request, Actor, Permit, Ct);
        first.IsSuccess.ShouldBeTrue();
        var replay = await h.Repository(h.Db).BeginAsync(request, Actor, Permit, Ct);
        replay.Value.SessionId.ShouldBe(first.Value.SessionId);
        var changed = await h.Repository(h.Db).BeginAsync(request with { DeclaredLength = 4 }, Actor, Permit, Ct);
        changed.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
        (await h.Db.PhotoUploadSessions.CountAsync(Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ActualLengthMustEqualDeclaredLength(int actual)
    {
        await using var h = await Harness.Create();
        var session = (await h.Repository(h.Db).BeginAsync(h.Begin(3), Actor, Permit, Ct)).Value;
        var result = await h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, session.SessionId, Guid.NewGuid(), new Source(() => new MemoryStream(new byte[actual]))), Actor, Permit, Ct);
        result.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.LengthMismatch);
        (await h.Db.PhotoOriginals.CountAsync(Ct)).ShouldBe(0);
        (await h.Db.PhotoJobs.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task LimitIsEnforcedDuringStreaming()
    {
        await using var h = await Harness.Create();
        var session = (await h.Repository(h.Db).BeginAsync(h.Begin(EmbeddedPhotoPolicy.MaxBytes), Actor, Permit, Ct)).Value;
        var stream = new GeneratedStream(EmbeddedPhotoPolicy.MaxBytes + 4096);
        var result = await h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, session.SessionId, Guid.NewGuid(), new Source(() => stream)), Actor, Permit, Ct);
        result.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.TooLarge);
        stream.BytesRead.ShouldBeLessThanOrEqualTo(EmbeddedPhotoPolicy.MaxBytes + 1);
        (await h.Db.PhotoUploadReceipts.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task LostCompletionResponseRecoversReceiptAndRejectsResend()
    {
        await using var h = await Harness.Create();
        var begin = h.Begin(3) with { DisplayFileName = "../../phone.heic" };
        var session = (await h.Repository(h.Db).BeginAsync(begin, Actor, Permit, Ct)).Value;
        var committed = await h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, session.SessionId, Guid.NewGuid(), new Source(() => new MemoryStream([1,2,3]))), Actor, Permit, Ct);
        committed.IsSuccess.ShouldBeTrue();
        await using var restarted = h.Fixture.Context();
        var recovered = await h.Repository(restarted).GetOutcomeAsync(new(h.WorkspaceId, session.SessionId), Actor, Permit, Ct);
        recovered.Value.Receipt!.ReceiptId.ShouldBe(committed.Value.Receipt!.ReceiptId);
        var resend = new Source(() => throw new InvalidOperationException("Must not read a completed body"));
        var rejected = await h.Repository(restarted).WriteAsync(new(h.WorkspaceId, session.SessionId, Guid.NewGuid(), resend), Actor, Permit, Ct);
        rejected.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
        resend.OpenCount.ShouldBe(0);
        var original = await restarted.PhotoOriginals.SingleAsync(Ct);
        original.StorageKey.ShouldNotContain("phone");
        original.StorageKey.ShouldNotContain("..");
        original.SourceSha256.ShouldBe(Convert.ToHexString(SHA256.HashData([1,2,3])));
        await using var stored = await h.Provider.ReadAsync(original.StorageKey, Ct);
        stored.ShouldNotBeNull();
        using var output = new MemoryStream();
        await stored.Content.CopyToAsync(output, Ct);
        output.ToArray().ShouldBe(new byte[] { 1,2,3 });
        (await restarted.PhotoJobs.CountAsync(Ct)).ShouldBe(1);
        (await restarted.PhotoOriginals.CountAsync(Ct)).ShouldBe(1);
        (await restarted.MediaAssets.SingleAsync(Ct)).BlobState.ShouldBe(Cmsify.Core.Domain.Enums.MediaBlobState.Available);
        (await restarted.PhotoRevisions.SingleAsync(Ct)).State.ShouldBe(EmbeddedPhotoState.Queued);
    }

    [Fact]
    public async Task ConcurrentWriterHasOneFence()
    {
        await using var h = await Harness.Create();
        var session = (await h.Repository(h.Db).BeginAsync(h.Begin(3), Actor, Permit, Ct)).Value;
        var gate = new GateSource();
        var key = Guid.NewGuid();
        var first = h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, session.SessionId, key, gate), Actor, Permit, Ct);
        await gate.Opened.Task.WaitAsync(Ct);
        try
        {
            await using var other = h.Fixture.Context();
            var source = new Source(() => new MemoryStream([1,2,3]));
            var second = await h.Repository(other).WriteAsync(new(h.WorkspaceId, session.SessionId, key, source), Actor, Permit, Ct);
            second.Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Conflict);
            source.OpenCount.ShouldBe(0);
        }
        finally { gate.Resume.TrySetResult(); }
        (await first).IsSuccess.ShouldBeTrue();
        (await h.Db.PhotoUploadAttempts.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredRestartUsesNewObjectAndStaleWriterCannotCommit()
    {
        await using var h = await Harness.Create();
        var session = (await h.Repository(h.Db).BeginAsync(h.Begin(3), Actor, Permit, Ct)).Value;
        var gate = new GateSource(); var key = Guid.NewGuid();
        var stale = h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, session.SessionId, key, gate), Actor, Permit, Ct);
        await gate.Opened.Task.WaitAsync(Ct);
        try
        {
            h.Clock.Now += TimeSpan.FromMinutes(3);
            await using var restarted = h.Fixture.Context();
            var replacement = await h.Repository(restarted).WriteAsync(new(h.WorkspaceId, session.SessionId, key, new Source(() => new MemoryStream([4,5,6]))), Actor, Permit, Ct);
            replacement.IsSuccess.ShouldBeTrue();
        }
        finally { gate.Resume.TrySetResult(); }
        (await stale).IsFailure.ShouldBeTrue();
        await using var check = h.Fixture.Context();
        (await check.PhotoUploadAttempts.Select(x => x.StorageKey).Distinct().CountAsync(Ct)).ShouldBe(2);
        (await check.PhotoOriginals.SingleAsync(Ct)).SourceSha256.ShouldBe(Convert.ToHexString(SHA256.HashData([4,5,6])));
        (await check.PhotoUploadReceipts.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task AuthorizationRevokedDuringStreamCannotAcknowledge()
    {
        await using var h = await Harness.Create();
        var session = (await h.Repository(h.Db).BeginAsync(h.Begin(3), Actor, Permit, Ct)).Value;
        var gate = new GateSource(); bool allowed = true;
        var writing = h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, session.SessionId, Guid.NewGuid(), gate), Actor,
            (_, _) => Task.FromResult(allowed), Ct);
        await gate.Opened.Task.WaitAsync(Ct);
        allowed = false; gate.Resume.TrySetResult();
        (await writing).Errors[0].Code.ShouldBe(EmbeddedPhotoErrors.Forbidden);
        (await h.Db.PhotoOriginals.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task WrongWorkspaceAndDeniedActorOpenNoBody()
    {
        await using var h = await Harness.Create();
        var session = (await h.Repository(h.Db).BeginAsync(h.Begin(3), Actor, Permit, Ct)).Value;
        var source = new Source(() => throw new InvalidOperationException());
        var wrong = await h.Repository(h.Db).WriteAsync(new(Guid.NewGuid(), session.SessionId, Guid.NewGuid(), source), Actor, Permit, Ct);
        wrong.IsFailure.ShouldBeTrue();
        var denied = await h.Repository(h.Db).WriteAsync(new(h.WorkspaceId, session.SessionId, Guid.NewGuid(), source), Actor, (_, _) => Task.FromResult(false), Ct);
        denied.IsFailure.ShouldBeTrue();
        source.OpenCount.ShouldBe(0);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public EmbeddedPhotoFixture Fixture { get; private init; } = null!;
        public Cmsify.Infrastructure.Persistence.CmsifyDbContext Db { get; private init; } = null!;
        public ManualClock Clock { get; } = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cmsify-photo-test", Guid.NewGuid().ToString("N"));
        public LocalFileStorageProvider Provider { get; private set; } = null!;
        public Guid WorkspaceId => Fixture.Workspace.Id;
        public EmbeddedPhotoUploadRepository Repository(Cmsify.Infrastructure.Persistence.CmsifyDbContext db) =>
            new(db, new EmbeddedPhotoStorage(Provider, "local"), Options.Create(new EmbeddedPhotoOptions { Enabled = true, PolicyId = "synthetic-photo-v1" }), new TestCapability(), Clock);
        public BeginEmbeddedPhotoUploadRequest Begin(long length) => new(WorkspaceId, Guid.NewGuid(), Guid.NewGuid(), "phone.heic", length, "synthetic-photo-v1");
        public static async Task<Harness> Create()
        {
            var fixture = await EmbeddedPhotoFixture.Create();
            var h = new Harness { Fixture = fixture, Db = fixture.Context() };
            h.Provider = new LocalFileStorageProvider(Options.Create(new LocalStorageOptions { RootPath = h.Root }));
            return h;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); await Fixture.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TestCapability : IEmbeddedPhotoProcessorCapability
    {
        public Task<EmbeddedPhotoCapability> GetAsync(CancellationToken ct) => Task.FromResult(
            new EmbeddedPhotoCapability(true, true, "synthetic-photo-v1", "synthetic-only-1", "TEST-ONLY-NO-NATIVE-QUALIFICATION", null));
    }
    private sealed class Source(Func<Stream> factory) : IEmbeddedPhotoByteSource
    {
        public int OpenCount { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); OpenCount++; return ValueTask.FromResult(factory()); }
    }
    private sealed class GateSource : IEmbeddedPhotoByteSource
    {
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Stream> OpenReadAsync(CancellationToken ct)
        { Opened.TrySetResult(); await Resume.Task.WaitAsync(ct); return new MemoryStream([1,2,3]); }
    }
    private sealed class GeneratedStream(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { var read = (int)Math.Min(count, length - BytesRead); Array.Clear(buffer, offset, read); BytesRead += read; return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); var read = (int)Math.Min(buffer.Length, length - BytesRead); buffer.Span[..read].Clear(); BytesRead += read; return ValueTask.FromResult(read); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
