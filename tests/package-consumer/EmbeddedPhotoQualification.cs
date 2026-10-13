using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SyntaxCircus.Common;
using static EmbeddedPhotoHttpAdapter;

internal static class EmbeddedPhotoQualification
{
    internal static async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        Guid workspace;
        await using (var setup = services.CreateAsyncScope())
        {
            setup.ServiceProvider.GetRequiredService<ActorScope>().Actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Admin, null, true, true);
            workspace = Value(await setup.ServiceProvider.GetRequiredService<IWorkspacesCreateRequestHandler>()
                .HandleAsync(new("Photo package workspace", "photo-package-workspace", null), ct)).Id;
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Development });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddConfiguration(services.GetRequiredService<IConfiguration>());
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; });
        var journalRoot = Path.Combine(Directory.GetCurrentDirectory(), "photo-host-intents");
        var journal = new PhotoConsumerRetentionStore(journalRoot);
        builder.Services.AddSingleton(journal);
        var clock = new PhotoClock(); var faults = new Faults();
        builder.Services.AddSingleton<TimeProvider>(clock); builder.Services.AddSingleton(faults);
        builder.Services.AddScoped<Subject>();
        builder.Services.AddScoped<ICurrentActor>(p => p.GetRequiredService<Subject>());
        builder.Services.AddScoped<IEmbeddedPhotoAuthorizationService, ResourceAuthority>();
        builder.Services.AddSingleton<IEmbeddedPhotoProcessorCapability, Capability>();
        builder.Services.AddScoped<IEmbeddedPhotoProcessor, Processor>();
        builder.Services.AddSingleton(new RegisteredEmbeddedMediaRetentionGuard(PhotoConsumerRetentionStore.Namespace, journal));
        builder.Services.AddCmsifyInfrastructure(builder.Configuration, new() { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None });
        builder.Services.Replace(ServiceDescriptor.Scoped<IWorkspaceAuthorizationService, WorkspaceAuthority>());
        builder.Services.AddCmsifyEmbeddedPhotos(o => { o.Enabled = true; o.ProcessingEnabled = true; o.PolicyId = "synthetic-photo-v1"; });
        await using var app = builder.Build();
        var asset = Guid.NewGuid(); var subjectId = Guid.NewGuid(); var token = Guid.NewGuid().ToString("N");
        var beginKey = Guid.NewGuid(); var writeKey = Guid.NewGuid(); Guid sessionId = Guid.Empty;
        var opened = 0;
        void Bind(IServiceProvider p) { var actor = p.GetRequiredService<Subject>(); actor.Workspace = workspace; actor.Asset = asset; actor.UserId = subjectId; }
        app.MapPost("/fixture/upload", async (HttpContext context, IBeginEmbeddedPhotoUploadRequestHandler begin, IWriteEmbeddedPhotoUploadRequestHandler write) =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + token) { context.Response.StatusCode = 401; return; }
            if (context.Request.ContentLength is not { } length) { context.Response.StatusCode = 400; return; }
            Bind(context.RequestServices);
            var started = await begin.HandleAsync(new(workspace, asset, beginKey, "../phone.heic", length, "synthetic-photo-v1"), context.RequestAborted);
            if (started.IsFailure) { context.Response.StatusCode = 409; return; }
            sessionId = started.Value.SessionId;
            var written = await write.HandleAsync(new(workspace, sessionId, writeKey, new BodySource(context.Request, () => Interlocked.Increment(ref opened))), context.RequestAborted);
            context.Response.StatusCode = written.IsSuccess ? 204 : 409;
        });
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            using (var anonymous = await client.PostAsync("/fixture/upload", new StreamContent(new MemoryStream([1,2,3])), ct))
                Require(anonymous.StatusCode == HttpStatusCode.Unauthorized && opened == 0, "Anonymous upload opened a body.");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using (var upload = await client.PostAsync("/fixture/upload", new StreamContent(new MemoryStream([1,2,3])), ct))
                Require(upload.StatusCode == HttpStatusCode.NoContent && opened == 1, "Authenticated streaming upload failed.");
            await using (var fresh = app.Services.CreateAsyncScope())
            {
                Bind(fresh.ServiceProvider);
                Require((await fresh.ServiceProvider.GetRequiredService<IEmbeddedPhotoReadiness>().CheckAsync(ct)).Ready, "Synthetic prerequisite readiness false.");
                var outcome = Value(await fresh.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoUploadOutcomeRequestHandler>()
                    .HandleAsync(new(workspace, sessionId), ct));
                Require(outcome.State == EmbeddedPhotoUploadState.Completed && outcome.Receipt is not null, "Fresh-scope upload recovery lost receipt.");
                var resend = await fresh.ServiceProvider.GetRequiredService<IWriteEmbeddedPhotoUploadRequestHandler>()
                    .HandleAsync(new(workspace, sessionId, writeKey, new UnopenedSource()), ct);
                Require(resend.IsFailure, "Completed upload accepted another body.");
                await UpgradeLegacyPhotos(app.Services, workspace, asset, subjectId, beginKey, sessionId, outcome.Receipt!.ReceiptId, ct);
                Require(await fresh.ServiceProvider.GetRequiredService<IProcessEmbeddedPhotoQueueRequestHandler>().HandleAsync(new(), ct), "Synthetic processing did not run.");
            }
            EmbeddedPhotoManifest first;
            await using (var read = app.Services.CreateAsyncScope())
            {
                Bind(read.ServiceProvider);
                var status = Value(await read.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoRequestHandler>().HandleAsync(new(workspace, asset, 1), ct));
                Require(status.State == EmbeddedPhotoState.Ready && status.Manifest is not null, "Ready lacks public manifest identity.");
                first = Value(await read.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoManifestRequestHandler>()
                    .HandleAsync(new(workspace, asset, 1, status.Manifest!.ManifestSha256), ct));
                Require(first.Variants.Select(x => x.VariantKey).SequenceEqual(new[] { "320", "640", "1280" }), "Responsive set differs.");
            }
            var reference = Guid.NewGuid(); journal.Reserve(workspace, reference, [first.Identity]);
            EmbeddedMediaReferenceOutcome protectedSet;
            await using (var protect = app.Services.CreateAsyncScope())
            {
                Bind(protect.ServiceProvider);
                protectedSet = Value(await protect.ServiceProvider.GetRequiredService<IProtectEmbeddedMediaReferencesRequestHandler>()
                    .HandleAsync(new(workspace, PhotoConsumerRetentionStore.Namespace, reference, Guid.NewGuid(), [first.Identity]), ct));
                var release = await protect.ServiceProvider.GetRequiredService<IReleaseEmbeddedMediaReferencesRequestHandler>()
                    .HandleAsync(new(workspace, PhotoConsumerRetentionStore.Namespace, reference, Guid.NewGuid(), 1), ct);
                Require(release.IsFailure && release.Errors[0].Code == EmbeddedPhotoErrors.Retained, "Pending consumer intent released.");
            }
            var restartedJournal = new PhotoConsumerRetentionStore(journalRoot);
            Require(await restartedJournal.CheckAsync(new(workspace, PhotoConsumerRetentionStore.Namespace, reference, 1, [first.Identity]), ct)
                == EmbeddedMediaRetentionDecision.Unknown, "Pending consumer intent did not survive store reconstruction.");
            journal.Commit(workspace, reference);
            await using (var publication = app.Services.CreateAsyncScope())
            {
                Bind(publication.ServiceProvider);
                var actor = publication.ServiceProvider.GetRequiredService<Subject>(); actor.PublicReference = reference;
                var reader = publication.ServiceProvider.GetRequiredService<IReadEmbeddedPhotoVariantRequestHandler>(); var sink = new Sink();
                var variant = Value(await reader.HandleAsync(new(workspace, first.Identity, "320",
                    new(EmbeddedPhotoReadKind.ConsumerReference, PhotoConsumerRetentionStore.Namespace, reference, protectedSet.ProtectionRevision), sink), ct));
                Require(sink.Length == variant.Length, "Published exact variant stream differs.");
                Require((await publication.ServiceProvider.GetRequiredService<IReadEmbeddedPhotoOriginalRequestHandler>()
                    .HandleAsync(new(workspace, asset, new Sink()), ct)).IsFailure, "Consumer publication authority read original.");
                Require((await reader.HandleAsync(new(workspace, first.Identity, "320",
                    new(EmbeddedPhotoReadKind.ConsumerReference, PhotoConsumerRetentionStore.Namespace, Guid.NewGuid(), 1), new Sink()), ct)).IsFailure, "Guessed reference authorized a read.");
            }
            await using (var rotation = app.Services.CreateAsyncScope())
            {
                Bind(rotation.ServiceProvider);
                Value(await rotation.ServiceProvider.GetRequiredService<ICreateEmbeddedPhotoRevisionRequestHandler>()
                    .HandleAsync(new(workspace, asset, Guid.NewGuid(), 1, new(1), "synthetic-photo-v1"), ct));
                faults.TransientFailures = 3;
                var processor = rotation.ServiceProvider.GetRequiredService<IProcessEmbeddedPhotoQueueRequestHandler>();
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    Require(await processor.HandleAsync(new(), ct), "Transient attempt was not claimed.");
                    clock.Now += TimeSpan.FromSeconds(attempt == 0 ? 5 : 30);
                }
                var failed = Value(await rotation.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoRequestHandler>().HandleAsync(new(workspace, asset, 2), ct));
                Require(failed.State == EmbeddedPhotoState.Failed, "Three-attempt budget did not become Failed.");
                var retry = rotation.ServiceProvider.GetRequiredService<IRetryEmbeddedPhotoProcessingRequestHandler>();
                var retryRequest = new RetryEmbeddedPhotoProcessingRequest(workspace, asset, 2, Guid.NewGuid(), 0);
                Require(Value(await retry.HandleAsync(retryRequest, ct)).RetryGeneration == 1 &&
                    Value(await retry.HandleAsync(retryRequest, ct)).RetryGeneration == 1, "Retry generation replay differs.");
                Require(await rotation.ServiceProvider.GetRequiredService<IProcessEmbeddedPhotoQueueRequestHandler>().HandleAsync(new(), ct), "Rotation not processed.");
                var preserved = Value(await rotation.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoManifestRequestHandler>()
                    .HandleAsync(new(workspace, asset, 1, first.Identity.ManifestSha256), ct));
                Require(preserved.Identity == first.Identity && preserved.Variants.SequenceEqual(first.Variants), "Rotation changed old manifest.");
            }
            journal.Retire(workspace, reference); var releaseKey = Guid.NewGuid();
            await using (var release = app.Services.CreateAsyncScope())
            {
                Bind(release.ServiceProvider); var handler = release.ServiceProvider.GetRequiredService<IReleaseEmbeddedMediaReferencesRequestHandler>();
                var request = new ReleaseEmbeddedMediaReferencesRequest(workspace, PhotoConsumerRetentionStore.Namespace, reference, releaseKey, 1);
                var receipt = Value(await handler.HandleAsync(request, ct)).ReleaseReceipt;
                Require(receipt is not null && Value(await handler.HandleAsync(request, ct)).ReleaseReceipt == receipt, "Terminal release replay differs.");
            }
            await UploadOnlyEnrollment(app.Services, workspace, subjectId, ct);
            Console.WriteLine("PASS PHOTO authenticated HTTP streaming, receipt recovery/no resend, ready discovery, immutable rotation, private/public authority, durable pending/retired retention, terminal replay. SYNTHETIC ONLY.");
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
    private static async Task UploadOnlyEnrollment(IServiceProvider services, Guid workspace, Guid actor, CancellationToken ct)
    {
        var asset = Guid.NewGuid(); var key = Guid.NewGuid();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmbeddedPhotoOptions>>().Value;
        // Disposable consumer mode switch: qualify old intake without rebuilding application services.
        var previous = options.UploadOnlyEnabled; options.UploadOnlyEnabled = true;
        try
        {
            await using var scope = services.CreateAsyncScope(); var p = scope.ServiceProvider;
            var subject = p.GetRequiredService<Subject>(); subject.Workspace = workspace; subject.Asset = asset; subject.UserId = actor;
            var digest = Convert.ToHexString(SHA256.HashData([1,2,3]));
            var begun = Value(await p.GetRequiredService<IBeginEmbeddedPhotoUploadRequestHandler>()
                .HandleAsync(new(workspace, asset, Guid.NewGuid(), "old-original.png", 3, options.PolicyId) { ExpectedSourceSha256 = digest }, ct));
            var uploaded = Value(await p.GetRequiredService<IWriteEmbeddedPhotoUploadRequestHandler>()
                .HandleAsync(new(workspace, begun.SessionId, Guid.NewGuid(), new Bytes([1,2,3])), ct));
            var receipt = uploaded.Receipt!;
            var db = p.GetRequiredService<CmsifyDbContext>();
            Require(!await db.PhotoJobs.AnyAsync(x => x.AssetId == asset, ct), "Upload-only intake was automatically queued.");
            var enrollment = new EnrollEmbeddedPhotoUploadRequest(workspace, asset, receipt.ReceiptId, receipt.SourceSha256, 1, key, options.PolicyId);
            var handler = p.GetRequiredService<IEnrollEmbeddedPhotoUploadRequestHandler>();
            var queued = Value(await handler.HandleAsync(enrollment, ct));
            Require(queued.Revision == 2 && queued.State == EmbeddedPhotoState.Queued, "Enrollment did not create revision2.");
            Require(Value(await handler.HandleAsync(enrollment, ct)) == queued, "Enrollment replay changed result.");
            Require((await handler.HandleAsync(enrollment with { SourceSha256 = new string('a',64) }, ct)).IsFailure, "Changed enrollment proof replayed.");
            Value(await p.GetRequiredService<IGetEmbeddedPhotoOperationOutcomeRequestHandler>()
                .HandleAsync(new(workspace, asset, key, EmbeddedPhotoOperationKind.EnrollUpload), ct));
            Require(await db.PhotoJobs.CountAsync(x => x.AssetId == asset, ct) == 1, "Enrollment duplicated jobs.");
            var sink = new Sink();
            Value(await p.GetRequiredService<IReadEmbeddedPhotoOriginalRequestHandler>().HandleAsync(new(workspace, asset, sink), ct));
            Require(sink.Length == 3, "Original changed during enrollment.");
            Require(await p.GetRequiredService<IProcessEmbeddedPhotoQueueRequestHandler>().HandleAsync(new(), ct), "Enrolled work was not processed.");
            var ready = Value(await p.GetRequiredService<IGetEmbeddedPhotoRequestHandler>().HandleAsync(new(workspace, asset, 2), ct));
            Require(ready.State == EmbeddedPhotoState.Ready, "Enrolled revision was not Ready.");
            var outcome = Value(await p.GetRequiredService<IGetEmbeddedPhotoOperationOutcomeRequestHandler>()
                .HandleAsync(new(workspace, asset, key, EmbeddedPhotoOperationKind.EnrollUpload), ct));
            Require(outcome == queued && Value(await handler.HandleAsync(enrollment, ct)) == queued, "Ready state replaced immutable enrollment outcome.");
            Require((await db.PhotoRevisions.AsNoTracking().SingleAsync(x => x.AssetId == asset && x.Revision == 1, ct)).State == EmbeddedPhotoState.AwaitingProcessing, "Original history was rewritten.");
            Console.WriteLine("PASS PHOTO upload-only enrollment/no resend, stable receipt/original/history, single queued revision, exact read-only outcome after Ready. SYNTHETIC ONLY.");
        }
        finally { options.UploadOnlyEnabled = previous; }
    }
    private static async Task UpgradeLegacyPhotos(IServiceProvider services, Guid workspace, Guid asset,
        Guid actor, Guid beginKey, Guid session, Guid receipt, CancellationToken ct)
    {
        var pendingAsset = Guid.NewGuid(); var pendingKey = Guid.NewGuid();
        await using (var seed = services.CreateAsyncScope())
        {
            var subject = seed.ServiceProvider.GetRequiredService<Subject>();
            subject.Workspace = workspace; subject.Asset = pendingAsset; subject.UserId = actor;
            Value(await seed.ServiceProvider.GetRequiredService<IBeginEmbeddedPhotoUploadRequestHandler>()
                .HandleAsync(new(workspace, pendingAsset, pendingKey, "legacy-pending.heic", 3, "synthetic-photo-v1"), ct));
            var db = seed.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            // Owned disposable fixture: retain populated legacy rows while removing only
            // amendment schema. Run before upload-only completion creates any intake hold.
            await db.GetService<IMigrator>().MigrateAsync("20261010162757_AddEmbeddedPhotoFoundation", ct);
            Require(await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM information_schema.columns WHERE table_name = 'embedded_photo_upload_sessions' AND column_name IN ('upload_only', 'expected_source_sha256')").SingleAsync(ct) == 0, "Legacy schema was not exercised.");
            await db.Database.MigrateAsync(ct); await db.Database.MigrateAsync(ct);
            var rows = await db.PhotoUploadSessions.AsNoTracking().Where(x => x.WorkspaceId == workspace).ToArrayAsync(ct);
            Require(rows.Length == 2 && rows.All(x => !x.UploadOnly && x.ExpectedSourceSha256 is null && x.ProcessorVersion == "synthetic-only-1"), "Upgrade reinterpreted legacy mode/version.");
        }
        await using var fresh = services.CreateAsyncScope();
        var bound = fresh.ServiceProvider.GetRequiredService<Subject>();
        bound.Workspace = workspace; bound.Asset = asset; bound.UserId = actor;
        var replay = Value(await fresh.ServiceProvider.GetRequiredService<IBeginEmbeddedPhotoUploadRequestHandler>()
            .HandleAsync(new(workspace, asset, beginKey, "../phone.heic", 3, "synthetic-photo-v1"), ct));
        Require(replay.SessionId == session, "Upgrade changed completed session identity.");
        var recovered = Value(await fresh.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoUploadIntentRequestHandler>()
            .HandleAsync(new(workspace, asset, beginKey), ct));
        Require(recovered.Receipt!.ReceiptId == receipt, "Upgrade changed completed receipt identity.");
        bound.Asset = pendingAsset;
        var pending = Value(await fresh.ServiceProvider.GetRequiredService<IBeginEmbeddedPhotoUploadRequestHandler>()
            .HandleAsync(new(workspace, pendingAsset, pendingKey, "legacy-pending.heic", 3, "synthetic-photo-v1"), ct));
        Value(await fresh.ServiceProvider.GetRequiredService<IWriteEmbeddedPhotoUploadRequestHandler>()
            .HandleAsync(new(workspace, pending.SessionId, Guid.NewGuid(), new Bytes([1,2,3])), ct));
        var upgraded = fresh.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        Require(await upgraded.PhotoJobs.CountAsync(x => x.WorkspaceId == workspace, ct) == 2 &&
            await upgraded.PhotoUploadReceipts.CountAsync(x => x.WorkspaceId == workspace, ct) == 2, "Legacy pending completion lost jobs/receipts.");
        Console.WriteLine("PASS populated legacy photo schema upgrade/reapply, immutable receipt replay and pending qualified completion.");
    }

    private static T Value<T>(Result<T> result) { Require(result.IsSuccess, result.IsFailure ? result.Errors[0].Code : "success"); return result.Value; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class UnopenedSource : IEmbeddedPhotoByteSource
    {
        public ValueTask<Stream> OpenReadAsync(CancellationToken ct) => throw new InvalidOperationException("Completed body was opened.");
    }
    private sealed class Capability : IEmbeddedPhotoProcessorCapability
    {
        public Task<EmbeddedPhotoCapability> GetAsync(CancellationToken ct) => Task.FromResult(new EmbeddedPhotoCapability(true, true, "synthetic-photo-v1", "synthetic-only-1", "TEST-ONLY-NO-NATIVE-QUALIFICATION", null));
    }
    private sealed class Bytes(byte[] bytes) : IEmbeddedPhotoByteSource
    {
        public ValueTask<Stream> OpenReadAsync(CancellationToken ct) => ValueTask.FromResult<Stream>(new MemoryStream(bytes, false));
    }
    private sealed class PhotoClock : TimeProvider
    {
        internal DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Faults { internal int TransientFailures; }
    private sealed class Processor(Faults faults) : IEmbeddedPhotoProcessor
    {
        public Task<Result<EmbeddedPhotoCandidate>> ProcessAsync(EmbeddedPhotoProcessingInput input, CancellationToken ct)
        {
            if (faults.TransientFailures > 0)
            {
                faults.TransientFailures--;
                return Task.FromResult(Result<EmbeddedPhotoCandidate>.Failure(new(EmbeddedPhotoErrors.ProcessingFailed, "Synthetic transient failure.", ResultErrorKind.Conflict)));
            }
            var (width, height) = input.Instructions.QuarterTurnsClockwise % 2 == 0 ? (640, 480) : (480, 640);
            var variants = EmbeddedPhotoPolicy.RequiredVariants.Select(requirement =>
            {
                var fit = EmbeddedPhotoPolicy.Fit(width, height, requirement.Bound); var bytes = new byte[28];
                "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 20);
                "WEBPVP8L"u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 8);
                bytes[20] = 0x2f; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), (uint)(fit.Width - 1) | (uint)(fit.Height - 1) << 14);
                return new EmbeddedPhotoCandidateVariant(new(requirement.VariantKey, EmbeddedPhotoPolicy.WebpContentType, fit.Width, fit.Height,
                    bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))), new Bytes(bytes));
            }).ToArray();
            return Task.FromResult(Result<EmbeddedPhotoCandidate>.Success(new(new("HEIC", 640, 480, 8, "sRGB", false, true), variants)));
        }
    }
}
