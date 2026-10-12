using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.EmbeddedFiles;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SyntaxCircus.Common;
using SyntaxCircus.Storage;

internal static class EmbeddedFileQualification
{
    private const string Consumer="dog-documents";
    private static readonly byte[] _pdf=Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic package qualification\n%%EOF\n");
    private static string Digest=>Convert.ToHexStringLower(SHA256.HashData(_pdf));
    internal static async Task RunAsync(IServiceProvider services,CancellationToken ct)
    {
        var actor=Guid.NewGuid();Guid workspace;
        await using(var setup=services.CreateAsyncScope())
        {
            setup.ServiceProvider.GetRequiredService<ActorScope>().Actor=new(actor,null,UserRole.Admin,null,true,true);
            workspace=Value(await setup.ServiceProvider.GetRequiredService<IWorkspacesCreateRequestHandler>().HandleAsync(new("File package workspace","file-package-workspace",null),ct)).Id;
        }
        var configuration=services.GetRequiredService<IConfiguration>();var control=new Authority { Workspace=workspace };
        await using var app=Build(configuration,control);
        var asset=Guid.NewGuid();var beginKey=Guid.NewGuid();var writeKey=Guid.NewGuid();Guid session;EmbeddedFileReceipt receipt;
        await using(var scope=app.Services.CreateAsyncScope())
        {
            Bind(scope.ServiceProvider,actor);var p=scope.ServiceProvider;
            var request=new BeginEmbeddedFileUploadRequest(workspace,asset,beginKey,Consumer,"../pedigree.pdf",_pdf.Length,Digest,EmbeddedFileRules.PolicyId);
            var started=Value(await p.GetRequiredService<IBeginEmbeddedFileUploadRequestHandler>().HandleAsync(request,ct));session=started.SessionId;
            Require(Value(await p.GetRequiredService<IBeginEmbeddedFileUploadRequestHandler>().HandleAsync(request,ct)).SessionId==session,"Intent replay changed session.");
            Require((await p.GetRequiredService<IBeginEmbeddedFileUploadRequestHandler>().HandleAsync(request with { DisplayFileName="health.pdf" },ct)).IsFailure,"Changed intent accepted.");
            receipt=Value(await p.GetRequiredService<IWriteEmbeddedFileUploadRequestHandler>().HandleAsync(new(workspace,session,writeKey,new Bytes()),ct)).Receipt!;
            Require(receipt.SourceSha256==Digest && receipt.Length==_pdf.Length,"Receipt byte proof differs.");
            control.PublicReceipt=receipt.ReceiptId;control.PublicAsset=asset;
        }
        await using(var fresh=app.Services.CreateAsyncScope())
        {
            var p=fresh.ServiceProvider;Bind(p,actor);
            var replay=Value(await p.GetRequiredService<IWriteEmbeddedFileUploadRequestHandler>().HandleAsync(new(workspace,session,writeKey,new Unopened()),ct));
            Require(replay.Receipt==receipt,"Replay changed immutable receipt.");
            var identity=new EmbeddedFileIdentity(asset,receipt.ReceiptId,receipt.Length,receipt.SourceSha256);var sink=new Sink();
            var read=new ReadEmbeddedFileRequest(workspace,Consumer,identity,sink);
            Value(await p.GetRequiredService<IReadEmbeddedFileRequestHandler>().HandleAsync(read,ct));Require(sink.Hash==Digest,"Read bytes differ.");
            var foreign=new Sink();Require((await p.GetRequiredService<IReadEmbeddedFileRequestHandler>().HandleAsync(read with { File=identity with { ReceiptId=Guid.NewGuid() },Sink=foreign },ct)).IsFailure && foreign.Count==0,"Foreign receipt exposed bytes.");
            var revoked=new RevokingSink(()=>control.Allow=false);
            Require((await p.GetRequiredService<IReadEmbeddedFileRequestHandler>().HandleAsync(read with { Sink=revoked },ct)).IsFailure && revoked.Count==1,"Revocation did not stop next read.");control.Allow=true;
            p.GetRequiredService<ActorScope>().Actor=CurrentActorInfo.Anonymous;
            Value(await p.GetRequiredService<IReadEmbeddedFileRequestHandler>().HandleAsync(read,ct));
            Require((await p.GetRequiredService<IBeginEmbeddedFileUploadRequestHandler>().HandleAsync(new(workspace,Guid.NewGuid(),Guid.NewGuid(),Consumer,"private.pdf",_pdf.Length,Digest,EmbeddedFileRules.PolicyId),ct)).IsFailure,"Anonymous mutation accepted.");Bind(p,actor);
            var protect=new ProtectEmbeddedFileReferencesRequest(workspace,Consumer,Guid.NewGuid(),Guid.NewGuid(),[identity]);
            Value(await p.GetRequiredService<IProtectEmbeddedFileReferencesRequestHandler>().HandleAsync(protect,ct));
            Value(await p.GetRequiredService<IProtectEmbeddedFileReferencesRequestHandler>().HandleAsync(protect,ct));
            var release=new ReleaseEmbeddedFileReferencesRequest(workspace,Consumer,protect.ReferenceKey,Guid.NewGuid(),1);
            control.DeniedAsset=asset;
            Require((await p.GetRequiredService<IProtectEmbeddedFileReferencesRequestHandler>().HandleAsync(protect,ct)).Errors[0].Code==EmbeddedFileErrors.Denied,"File revocation ignored on protect replay.");
            Require((await p.GetRequiredService<IGetEmbeddedFileReferenceOutcomeRequestHandler>().HandleAsync(new(workspace,Consumer,protect.ReferenceKey),ct)).Errors[0].Code==EmbeddedFileErrors.Denied,"File revocation ignored on outcome.");
            Require((await p.GetRequiredService<IReleaseEmbeddedFileReferencesRequestHandler>().HandleAsync(release,ct)).Errors[0].Code==EmbeddedFileErrors.Denied,"File revocation ignored on release.");
            control.DeniedAsset=Guid.Empty;
            Require((await p.GetRequiredService<IReleaseEmbeddedFileReferencesRequestHandler>().HandleAsync(release,ct)).IsFailure,"Retention guard denial ignored.");
            control.Release=true;Value(await p.GetRequiredService<IReleaseEmbeddedFileReferencesRequestHandler>().HandleAsync(release,ct));
            Value(await p.GetRequiredService<IReleaseEmbeddedFileReferencesRequestHandler>().HandleAsync(release,ct));
            Require((await p.GetRequiredService<IReleaseEmbeddedFileReferencesRequestHandler>().HandleAsync(release with { OperationKey=Guid.NewGuid() },ct)).IsFailure,"Changed release intent accepted.");
            Require(await p.GetRequiredService<CmsifyDbContext>().FileUploadReceipts.CountAsync(x=>x.AssetId==asset,ct)==1,"Duplicate receipt.");
        }
        foreach(var boundary in new[] {"before-store","after-store","after-receipt"})await Crash(app.Services,configuration,workspace,actor,boundary,ct);
        Console.WriteLine("PASS embedded PDF exact upload/read/replay, receipt isolation, anonymous read/mutation denial, streaming revocation, retention/release and three actual process restart boundaries.");
    }
    private static WebApplication Build(IConfiguration configuration,Authority control,string? boundary=null,string? marker=null)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions { Args=[] });builder.Configuration.Sources.Clear();builder.Configuration.AddConfiguration(configuration);builder.Logging.ClearProviders();
        builder.Host.UseDefaultServiceProvider(o=>{o.ValidateOnBuild=true;o.ValidateScopes=true;});
        builder.Services.AddScoped<ActorScope>();builder.Services.AddScoped<ICurrentActor,Subject>();
        builder.Services.AddSingleton(control);builder.Services.AddSingleton<IEmbeddedFileAuthorizationService>(control);
        builder.Services.AddSingleton(new RegisteredEmbeddedFileRetentionGuard(Consumer,control));
        builder.Services.AddCmsifyInfrastructure(builder.Configuration,new() { UseHostCurrentActorForAudit=true,Workers=CmsifyWorkers.None });
        if(boundary is not null)
        {
            var descriptor=builder.Services.Last(x=>x.ServiceType==typeof(IStorageProvider));builder.Services.Remove(descriptor);
            builder.Services.AddScoped<IStorageProvider>(p=>new BoundaryStorage((IStorageProvider)(descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(p) ?? ActivatorUtilities.CreateInstance(p,descriptor.ImplementationType!)),boundary,marker!));
        }
        builder.Services.AddCmsifyEmbeddedFiles(o=>o.Enabled=true);return builder.Build();
    }
    internal static async Task RunChildAsync(string[] args)
    {
        Require(args.Length==7,"Invalid file child arguments.");
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["ConnectionStrings:Cmsify"]=Environment.GetEnvironmentVariable("CMSIFY_CONSUMER_POSTGRES"),
            ["Storage:Local:BasePath"]=Path.Combine(Directory.GetCurrentDirectory(),"storage"),
            ["Secrets:ActiveKeyId"]="file-test",["Secrets:EncryptionKeys:file-test"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }).Build();
        await using var app=Build(configuration,new() { Workspace=Guid.Parse(args[1]) },args[0],args[6]);await using var scope=app.Services.CreateAsyncScope();var p=scope.ServiceProvider;Bind(p,Guid.Parse(args[5]));
        var workspace=Guid.Parse(args[1]);var asset=Guid.Parse(args[2]);
        var started=Value(await p.GetRequiredService<IBeginEmbeddedFileUploadRequestHandler>().HandleAsync(new(workspace,asset,Guid.Parse(args[3]),Consumer,"crash.pdf",_pdf.Length,Digest,EmbeddedFileRules.PolicyId),CancellationToken.None));
        Value(await p.GetRequiredService<IWriteEmbeddedFileUploadRequestHandler>().HandleAsync(new(workspace,started.SessionId,Guid.Parse(args[4]),new Bytes()),CancellationToken.None));
        await Pause(args[6]);
    }
    private static async Task Crash(IServiceProvider services,IConfiguration configuration,Guid workspace,Guid actor,string boundary,CancellationToken ct)
    {
        var asset=Guid.NewGuid();var begin=Guid.NewGuid();var write=Guid.NewGuid();var marker=Path.Combine(Directory.GetCurrentDirectory(),"file-crash-"+Guid.NewGuid().ToString("N"));
        var info=new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true };
        info.Environment["CMSIFY_CONSUMER_POSTGRES"]=configuration.GetConnectionString("Cmsify");
        foreach(var arg in new[] {typeof(EmbeddedFileQualification).Assembly.Location,"--file-crash",boundary,workspace.ToString(),asset.ToString(),begin.ToString(),write.ToString(),actor.ToString(),marker})info.ArgumentList.Add(arg);
        using var child=Process.Start(info)!;var stdout=child.StandardOutput.ReadToEndAsync(ct);var stderr=child.StandardError.ReadToEndAsync(ct);
        try
        {
            var timer=Stopwatch.StartNew();while(!File.Exists(marker) && !child.HasExited && timer.Elapsed<TimeSpan.FromSeconds(25))await Task.Delay(25,ct);
            Require(File.Exists(marker) && !child.HasExited,"File child did not reach durable boundary.");child.Kill(true);await child.WaitForExitAsync(ct);Require(child.ExitCode!=0,"File child was not terminated.");
            await using var scope=services.CreateAsyncScope();var p=scope.ServiceProvider;Bind(p,actor);var db=p.GetRequiredService<CmsifyDbContext>();
            var intent=Value(await p.GetRequiredService<IGetEmbeddedFileUploadIntentRequestHandler>().HandleAsync(new(workspace,Consumer,asset,begin),ct));
            if(boundary!="after-receipt")
            {
                Require(intent.Receipt is null,"Killed upload had a receipt before commit.");
                await db.FileUploadSessions.Where(x=>x.Id==intent.SessionId).ExecuteUpdateAsync(x=>x.SetProperty(s=>s.LeaseUntil,DateTimeOffset.UtcNow.AddSeconds(-1)),ct);
                var outcome=Value(await p.GetRequiredService<IGetEmbeddedFileUploadOutcomeRequestHandler>().HandleAsync(new(workspace,intent.SessionId),ct));Require(outcome.State==EmbeddedFileUploadState.Interrupted && outcome.Receipt is null,"Lookup recovered an upload.");
            }
            var replay=Value(await p.GetRequiredService<IWriteEmbeddedFileUploadRequestHandler>().HandleAsync(new(workspace,intent.SessionId,write,boundary=="after-receipt"?new Unopened():new Bytes()),ct));
            Require(replay.Receipt!.SourceSha256==Digest && await db.FileUploadReceipts.CountAsync(x=>x.AssetId==asset,ct)==1,"Restart lost exact one receipt.");
        }
        finally { if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync(CancellationToken.None);}await stdout;await stderr;if(File.Exists(marker))File.Delete(marker); }
    }
    private static async Task Pause(string marker)
    { using(var file=new FileStream(marker,FileMode.Create,FileAccess.Write,FileShare.Read)){file.Write("boundary"u8);file.Flush(true);}await Task.Delay(Timeout.InfiniteTimeSpan); }
    private static void Bind(IServiceProvider p,Guid actor)=>p.GetRequiredService<ActorScope>().Actor=new(actor,null,UserRole.Admin,null,true,true);
    private static T Value<T>(Result<T> result){Require(result.IsSuccess,result.IsFailure?result.Errors[0].Code:"success");return result.Value;}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class Authority : IEmbeddedFileAuthorizationService,IEmbeddedFileRetentionGuard
    {
        public bool Allow=true,Release;
        public Guid Workspace,PublicReceipt,PublicAsset,DeniedAsset;
        public Task<bool> CanExecuteAsync(EmbeddedFileOperation op,CancellationToken ct)=>Task.FromResult(Allow && op.WorkspaceId==Workspace && op.AssetId!=DeniedAsset &&
            (op.ConsumerNamespace is null || op.ConsumerNamespace==Consumer) && (op.ActorId is not null || op.Kind==EmbeddedFileOperationKind.ReadFile && op.ReceiptId==PublicReceipt && op.AssetId==PublicAsset));
        public Task<EmbeddedFileRetentionDecision> CheckAsync(EmbeddedFileRetentionRequest request,CancellationToken ct)=>Task.FromResult(new EmbeddedFileRetentionDecision(Release));
    }
    private sealed class Subject(ActorScope scope) : ICurrentActor
    {
        public Guid? UserId=>scope.Actor.UserId;public Guid? ApiClientId=>scope.Actor.ApiClientId;
        public UserRole Role=>scope.Actor.Role;public Guid? WorkspaceId=>scope.Actor.WorkspaceId;
        public bool IsAuthenticated=>scope.Actor.IsAuthenticated;public bool IsSuperAdmin=>scope.Actor.IsSuperAdmin;
    }
    private sealed class Bytes : IEmbeddedFileByteSource { public ValueTask<Stream> OpenReadAsync(CancellationToken ct)=>ValueTask.FromResult<Stream>(new MemoryStream(_pdf)); }
    private sealed class Unopened : IEmbeddedFileByteSource { public ValueTask<Stream> OpenReadAsync(CancellationToken ct)=>throw new InvalidOperationException("Completed source reopened."); }
    private sealed class Sink : IEmbeddedFileByteSink
    { public string? Hash;public long Count;public async Task WriteAsync(Stream source,EmbeddedFileReceipt descriptor,CancellationToken ct){using var output=new MemoryStream();await source.CopyToAsync(output,ct);Count=output.Length;Hash=Convert.ToHexStringLower(SHA256.HashData(output.ToArray()));} }
    private sealed class RevokingSink(Action revoke) : IEmbeddedFileByteSink
    { public int Count;public async Task WriteAsync(Stream source,EmbeddedFileReceipt descriptor,CancellationToken ct){var b=new byte[1];Count+=await source.ReadAsync(b,ct);revoke();Count+=await source.ReadAsync(b,ct);} }
    private sealed class BoundaryStorage(IStorageProvider inner,string boundary,string marker) : IStorageProvider
    {
        public async Task<StoredObject> StoreAsync(StoreObjectRequest request,CancellationToken cancellationToken=default)
        { if(boundary=="before-store")await Pause(marker);var value=await inner.StoreAsync(request,cancellationToken);if(boundary=="after-store")await Pause(marker);return value; }
        public Task<StorageObjectMetadata?> GetMetadataAsync(string key,CancellationToken cancellationToken=default)=>inner.GetMetadataAsync(key,cancellationToken);
        public Task<StorageReadResult?> ReadAsync(string key,CancellationToken cancellationToken=default)=>inner.ReadAsync(key,cancellationToken);
        public Task<bool> ExistsAsync(string key,CancellationToken cancellationToken=default)=>inner.ExistsAsync(key,cancellationToken);
        public Task DeleteAsync(string key,CancellationToken cancellationToken=default)=>inner.DeleteAsync(key,cancellationToken);
        public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest request,CancellationToken cancellationToken=default)=>inner.ListAsync(request,cancellationToken);
        public Task<string> GetAccessUrlAsync(string key,TimeSpan? expiry=null,CancellationToken cancellationToken=default)=>inner.GetAccessUrlAsync(key,expiry,cancellationToken);
    }
}
