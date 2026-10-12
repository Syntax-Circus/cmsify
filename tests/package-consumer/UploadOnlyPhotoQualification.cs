using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Media;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SyntaxCircus.Common;
using static EmbeddedPhotoHttpAdapter;

// Package-only intake qualification. No qualified capability or image processor is registered.
internal static class UploadOnlyPhotoQualification
{
    internal static async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        Guid workspace;
        await using (var setup = services.CreateAsyncScope())
        {
            setup.ServiceProvider.GetRequiredService<ActorScope>().Actor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Admin, null, true, true);
            workspace = Value(await setup.ServiceProvider.GetRequiredService<IWorkspacesCreateRequestHandler>()
                .HandleAsync(new("Upload-only package workspace", "upload-only-package-workspace", null), ct)).Id;
        }
        var configuration = services.GetRequiredService<IConfiguration>();
        await using var app = Build(configuration);
        var asset = Guid.NewGuid(); var beginKey = Guid.NewGuid(); var writeKey = Guid.NewGuid(); var actor = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N"); var opened = 0;
        var expected = Convert.ToHexStringLower(SHA256.HashData([1,2,3]));
        void Bind(IServiceProvider p) => BindSubject(p, workspace, asset, actor);
        app.MapPost("/fixture/upload-only/begin", async (HttpContext context, IBeginEmbeddedPhotoUploadRequestHandler handler) =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + token) { context.Response.StatusCode = 401; return; }
            Bind(context.RequestServices);
            var result = await handler.HandleAsync(new(workspace, asset, beginKey, "../phone.heic", 3, "phone-sdr-webp-v1")
                { ExpectedSourceSha256 = expected }, context.RequestAborted);
            // Deliberately lose the Begin acknowledgement; later lookup must recover this exact intent.
            context.Response.StatusCode = result.IsSuccess ? 503 : 409;
        });
        app.MapPost("/fixture/upload-only/write", async (HttpContext context, IGetEmbeddedPhotoUploadIntentRequestHandler lookup, IWriteEmbeddedPhotoUploadRequestHandler write) =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + token) { context.Response.StatusCode = 401; return; }
            Bind(context.RequestServices);
            var existing = await lookup.HandleAsync(new(workspace, asset, beginKey), context.RequestAborted);
            if (existing.IsFailure || existing.Value.State == EmbeddedPhotoUploadState.Completed) { context.Response.StatusCode = 409; return; }
            var result = await write.HandleAsync(new(workspace, existing.Value.SessionId, writeKey,
                new BodySource(context.Request, () => Interlocked.Increment(ref opened))), context.RequestAborted);
            // Deliberately lose completion response after the receipt was committed.
            context.Response.StatusCode = result.IsSuccess ? 503 : 409;
        });
        app.MapGet("/fixture/upload-only/original", async (HttpContext context, IReadEmbeddedPhotoOriginalRequestHandler read) =>
        {
            if (context.Request.Headers.Authorization != "Bearer " + token) { context.Response.StatusCode = 401; return; }
            Bind(context.RequestServices); context.Response.ContentType = "application/octet-stream";
            var result = await read.HandleAsync(new(workspace, asset, new ResponseSink(context.Response)), context.RequestAborted);
            if (result.IsFailure && !context.Response.HasStarted) context.Response.StatusCode = 404;
        });
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            using (var denied = await client.PostAsync("/fixture/upload-only/write", new ByteArrayContent([1,2,3]), ct))
                Require(denied.StatusCode == HttpStatusCode.Unauthorized && opened == 0, "Anonymous intake opened a body.");
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using (var begun = await client.PostAsync("/fixture/upload-only/begin", new ByteArrayContent([]), ct))
                Require(begun.StatusCode == HttpStatusCode.ServiceUnavailable, "Begin response loss was not exercised.");
            Guid session;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                Bind(scope.ServiceProvider);
                Require((await scope.ServiceProvider.GetRequiredService<IEmbeddedPhotoUploadReadiness>().CheckAsync(ct)).Ready, "Upload-only prerequisites failed.");
                Require(!(await scope.ServiceProvider.GetRequiredService<IEmbeddedPhotoReadiness>().CheckAsync(ct)).Ready, "Missing processor unexpectedly qualified.");
                var recovered = Value(await scope.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoUploadIntentRequestHandler>().HandleAsync(new(workspace, asset, beginKey), ct));
                session = recovered.SessionId;
                Require(recovered.State == EmbeddedPhotoUploadState.Pending && recovered.Receipt is null, "Lost Begin lookup allocated another session.");
                var changed = await scope.ServiceProvider.GetRequiredService<IBeginEmbeddedPhotoUploadRequestHandler>().HandleAsync(new(workspace, asset, beginKey, "../phone.heic", 3, "phone-sdr-webp-v1")
                    { ExpectedSourceSha256 = new('a', 64) }, ct);
                Require(changed.IsFailure, "Same Begin key accepted changed digest.");
            }
            using (var mismatch = await client.PostAsync("/fixture/upload-only/write", new ByteArrayContent([4,5,6]), ct))
                Require(mismatch.StatusCode == HttpStatusCode.Conflict && opened == 1, "Changed bytes were acknowledged.");
            using (var written = await client.PostAsync("/fixture/upload-only/write", new ByteArrayContent([1,2,3]), ct))
                Require(written.StatusCode == HttpStatusCode.ServiceUnavailable && opened == 2, "Completion response loss was not exercised.");
            EmbeddedPhotoUploadReceipt receipt;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                Bind(scope.ServiceProvider);
                var recovered = Value(await scope.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoUploadIntentRequestHandler>().HandleAsync(new(workspace, asset, beginKey), ct));
                Require(recovered.SessionId == session && recovered.Receipt is not null, "Completion lookup lost original intent/receipt.");
                receipt = recovered.Receipt!;
                var status = Value(await scope.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoRequestHandler>().HandleAsync(new(workspace, asset, 1), ct));
                Require(status.State == EmbeddedPhotoState.AwaitingProcessing && status.ProcessorVersion is null && status.Manifest is null, "Private intake became an image manifest.");
                Require(!await scope.ServiceProvider.GetRequiredService<IProcessEmbeddedPhotoQueueRequestHandler>().HandleAsync(new(), ct), "Disabled processing claimed work.");
                var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
                Require(await db.PhotoJobs.CountAsync(x => x.AssetId == asset, ct) == 0 && await db.PhotoUploadReceipts.CountAsync(x => x.AssetId == asset, ct) == 1, "Intake created jobs/duplicate receipt.");
                var resend = await scope.ServiceProvider.GetRequiredService<IWriteEmbeddedPhotoUploadRequestHandler>().HandleAsync(new(workspace, session, writeKey, new Unopened()), ct);
                Require(resend.IsFailure, "Completed receipt reopened a byte source.");
                var media = await db.MediaAssets.AsNoTracking().SingleAsync(x => x.Id == asset, ct);
                var deleted = await scope.ServiceProvider.GetRequiredService<IDeleteMediaAssetRequestHandler>().HandleAsync(new(workspace, asset, media.UpdatedAt.UtcTicks), ct);
                Require(deleted.IsFailure && deleted.Errors[0].Code == EmbeddedPhotoErrors.Retained, "CMS writer deleted retained intake.");
                var denied = await scope.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoUploadIntentRequestHandler>().HandleAsync(new(workspace, Guid.NewGuid(), beginKey), ct);
                Require(denied.IsFailure, "Cross-asset lookup authorized.");
            }
            var downloaded = await client.GetByteArrayAsync("/fixture/upload-only/original", ct);
            Require(Convert.ToHexString(SHA256.HashData(downloaded)).Equals(receipt.SourceSha256, StringComparison.OrdinalIgnoreCase) && downloaded.Length == receipt.Length, "Downloaded original differs from receipt.");
            await CrashAndRecover(app.Services, configuration, workspace, actor, ct);
            Console.WriteLine("PASS UPLOAD-ONLY package HTTP bytes, independent readiness, lost Begin/completion recovery, pinned digest, no jobs/manifest, retained original download and real child-process crash. INTAKE ONLY; NO DECODER QUALIFICATION.");
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    private static WebApplication Build(IConfiguration configuration)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Development });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddConfiguration(configuration);
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; });
        builder.Services.AddSingleton(new PhotoConsumerRetentionStore(Path.Combine(Directory.GetCurrentDirectory(), "upload-only-intents")));
        builder.Services.AddScoped<Subject>(); builder.Services.AddScoped<ICurrentActor>(p => p.GetRequiredService<Subject>());
        builder.Services.AddScoped<IEmbeddedPhotoAuthorizationService, ResourceAuthority>();
        builder.Services.AddCmsifyInfrastructure(builder.Configuration, new() { UseHostCurrentActorForAudit = true, Workers = CmsifyWorkers.None });
        builder.Services.Replace(ServiceDescriptor.Scoped<IWorkspaceAuthorizationService, WorkspaceAuthority>());
        builder.Services.AddCmsifyEmbeddedPhotos(o => { o.Enabled = true; o.UploadOnlyEnabled = true; });
        return builder.Build();
    }

    internal static async Task RunChildAsync(string[] args)
    {
        Require(args.Length == 6, "Invalid intake crash child arguments.");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["ConnectionStrings:Cmsify"] = Environment.GetEnvironmentVariable("CMSIFY_CONSUMER_POSTGRES"),
            ["Storage:Local:BasePath"] = Path.Combine(Directory.GetCurrentDirectory(), "storage"),
            ["Secrets:ActiveKeyId"] = "package-test", ["Secrets:EncryptionKeys:package-test"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }).Build();
        await using var app = Build(configuration); await using var scope = app.Services.CreateAsyncScope();
        var workspace = Guid.Parse(args[0]); var asset = Guid.Parse(args[1]); var beginKey = Guid.Parse(args[2]); var writeKey = Guid.Parse(args[3]); var actor = Guid.Parse(args[4]);
        BindSubject(scope.ServiceProvider, workspace, asset, actor);
        var started = Value(await scope.ServiceProvider.GetRequiredService<IBeginEmbeddedPhotoUploadRequestHandler>()
            .HandleAsync(new(workspace, asset, beginKey, "crash.heic", 3, "phone-sdr-webp-v1") { ExpectedSourceSha256 = Convert.ToHexStringLower(SHA256.HashData([1,2,3])) }, CancellationToken.None));
        var completed = Value(await scope.ServiceProvider.GetRequiredService<IWriteEmbeddedPhotoUploadRequestHandler>()
            .HandleAsync(new(workspace, started.SessionId, writeKey, new Bytes()), CancellationToken.None));
        await File.WriteAllTextAsync(args[5], completed.Receipt!.ReceiptId.ToString());
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task CrashAndRecover(IServiceProvider services, IConfiguration configuration, Guid workspace, Guid actor, CancellationToken ct)
    {
        var asset = Guid.NewGuid(); var beginKey = Guid.NewGuid(); var marker = Path.Combine(Directory.GetCurrentDirectory(), "upload-only-crash-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["CMSIFY_CONSUMER_POSTGRES"] = configuration.GetConnectionString("Cmsify");
        foreach (var arg in new[] { typeof(UploadOnlyPhotoQualification).Assembly.Location, "--upload-only-crash", workspace.ToString(), asset.ToString(), beginKey.ToString(), Guid.NewGuid().ToString(), actor.ToString(), marker }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!; var stdout = child.StandardOutput.ReadToEndAsync(ct); var stderr = child.StandardError.ReadToEndAsync(ct);
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker) && !child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(25)) await Task.Delay(25, ct);
            Require(File.Exists(marker) && !child.HasExited, "Package crash child did not reach committed receipt.");
            child.Kill(true); await child.WaitForExitAsync(ct); Require(child.ExitCode != 0, "Crash child was not terminated.");
            _ = await stdout; _ = await stderr;
            await using var fresh = services.CreateAsyncScope(); BindSubject(fresh.ServiceProvider, workspace, asset, actor);
            var recovered = Value(await fresh.ServiceProvider.GetRequiredService<IGetEmbeddedPhotoUploadIntentRequestHandler>().HandleAsync(new(workspace, asset, beginKey), ct));
            Require(recovered.Receipt!.ReceiptId == Guid.Parse(await File.ReadAllTextAsync(marker, ct)), "Restart changed receipt identity.");
            var sink = new HashSink();
            Value(await fresh.ServiceProvider.GetRequiredService<IReadEmbeddedPhotoOriginalRequestHandler>().HandleAsync(new(workspace, asset, sink), ct));
            Require(sink.Sha256!.Equals(recovered.Receipt.SourceSha256, StringComparison.OrdinalIgnoreCase), "Crash lost original bytes.");
            var db = fresh.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var media = await db.MediaAssets.AsNoTracking().SingleAsync(x => x.Id == asset, ct);
            var deletion = await fresh.ServiceProvider.GetRequiredService<IDeleteMediaAssetRequestHandler>().HandleAsync(new(workspace, asset, media.UpdatedAt.UtcTicks), ct);
            Require(deletion.IsFailure && deletion.Errors[0].Code == EmbeddedPhotoErrors.Retained, "Crash-recovered intake lost retention.");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(CancellationToken.None); }
            _ = await stdout; _ = await stderr;
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    private static void BindSubject(IServiceProvider p, Guid workspace, Guid asset, Guid actor)
    { var subject = p.GetRequiredService<Subject>(); subject.Workspace = workspace; subject.Asset = asset; subject.UserId = actor; }
    private static T Value<T>(Result<T> result) { Require(result.IsSuccess, result.IsFailure ? result.Errors[0].Code : "success"); return result.Value; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Bytes : IEmbeddedPhotoByteSource
    { public ValueTask<Stream> OpenReadAsync(CancellationToken ct) => ValueTask.FromResult<Stream>(new MemoryStream([1,2,3])); }
    private sealed class Unopened : IEmbeddedPhotoByteSource
    { public ValueTask<Stream> OpenReadAsync(CancellationToken ct) => throw new InvalidOperationException("Completed source was reopened."); }
    private sealed class HashSink : IEmbeddedPhotoByteSink
    {
        internal string? Sha256;
        public async Task WriteAsync(Stream source, CancellationToken ct) => Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
    }
}
