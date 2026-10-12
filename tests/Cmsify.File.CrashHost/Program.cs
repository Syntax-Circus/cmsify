using System.Security.Cryptography;
using System.Text;
using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedFiles;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using SyntaxCircus.Storage;

if(args.Length!=8 || args[0] is not ("before-store" or "after-store" or "after-protect" or "after-release"))return 2;
var options=new DbContextOptionsBuilder<CmsifyDbContext>().UseNpgsql(args[1]).UseSyntaxCircusSnakeCaseNamingConvention().Options;
await using var db=new CmsifyDbContext(options);
var provider=new BoundaryStorage(new LocalFileStorageProvider(Options.Create(new LocalStorageOptions { RootPath=args[2] })),args[0],args[7]);
if(args[0] is "after-protect" or "after-release")
{
    var workspace=Guid.Parse(args[3]);var reference=Guid.Parse(args[4]);var key=Guid.Parse(args[5]);var actor=Guid.Parse(args[6]);
    var references=new EmbeddedFileReferenceRepository(db,new(provider,"local"),Options.Create(new EmbeddedFileOptions { Enabled=true }),[new("dog-documents",new ReleaseGuard())],TimeProvider.System);
    if(args[0]=="after-protect")
    {
        var receipt=await db.FileUploadReceipts.SingleAsync(x=>x.WorkspaceId==workspace);
        var protectedSet=await references.ProtectAsync(new(workspace,"dog-documents",reference,key,[new(receipt.AssetId,receipt.Id,receipt.Length,receipt.SourceSha256)]),actor,(_,_)=>Task.FromResult(true),CancellationToken.None);
        if(protectedSet.IsFailure)return 4;
    }
    else if((await references.ReleaseAsync(new(workspace,"dog-documents",reference,key,1),actor,(_,_)=>Task.FromResult(true),CancellationToken.None)).IsFailure)return 5;
    await provider.Pause();return 6;
}
var repository=new EmbeddedFileUploadRepository(db,new(provider,"local"),Options.Create(new EmbeddedFileOptions { Enabled=true }),TimeProvider.System);
var result=await repository.WriteAsync(new(Guid.Parse(args[3]),Guid.Parse(args[4]),Guid.Parse(args[5]),new Source()),Guid.Parse(args[6]),(_,_)=>Task.FromResult(true),CancellationToken.None);
return result.IsSuccess?0:3;

internal sealed class Source : IEmbeddedFileByteSource
{
    private static readonly byte[] _bytes=Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic document fixture\n%%EOF\n");
    public ValueTask<Stream> OpenReadAsync(CancellationToken ct)=>ValueTask.FromResult<Stream>(new MemoryStream(_bytes));
}
internal sealed class BoundaryStorage(IStorageProvider inner,string boundary,string marker) : IStorageProvider
{
    public async Task Pause()
    {
        using(var file=new FileStream(marker,FileMode.Create,FileAccess.Write,FileShare.Read))
        { file.Write(Encoding.ASCII.GetBytes(boundary));file.Flush(flushToDisk:true); }
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    public async Task<StoredObject> StoreAsync(StoreObjectRequest request,CancellationToken cancellationToken=default)
    {
        if(boundary=="before-store")await Pause();
        var result=await inner.StoreAsync(request,cancellationToken);
        if(boundary=="after-store")await Pause();
        return result;
    }
    public Task<StorageObjectMetadata?> GetMetadataAsync(string key,CancellationToken cancellationToken=default)=>inner.GetMetadataAsync(key,cancellationToken);
    public Task<StorageReadResult?> ReadAsync(string key,CancellationToken cancellationToken=default)=>inner.ReadAsync(key,cancellationToken);
    public Task<bool> ExistsAsync(string key,CancellationToken cancellationToken=default)=>inner.ExistsAsync(key,cancellationToken);
    public Task DeleteAsync(string key,CancellationToken cancellationToken=default)=>inner.DeleteAsync(key,cancellationToken);
    public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest request,CancellationToken cancellationToken=default)=>inner.ListAsync(request,cancellationToken);
    public Task<string> GetAccessUrlAsync(string key,TimeSpan? expiry=null,CancellationToken cancellationToken=default)=>inner.GetAccessUrlAsync(key,expiry,cancellationToken);
}
internal sealed class ReleaseGuard : IEmbeddedFileRetentionGuard
{
    public Task<EmbeddedFileRetentionDecision> CheckAsync(EmbeddedFileRetentionRequest request,CancellationToken ct)=>Task.FromResult(new EmbeddedFileRetentionDecision(true));
}
