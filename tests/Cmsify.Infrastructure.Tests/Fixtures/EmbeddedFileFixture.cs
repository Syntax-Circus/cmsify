using System.Security.Cryptography;
using System.Text;
using Cmsify.Core.EmbeddedFiles;
using Cmsify.Infrastructure.Extensions;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedFiles;
using Cmsify.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests.Fixtures;

internal sealed class EmbeddedFileFixture : IAsyncDisposable
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static readonly byte[] Pdf=Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic document fixture\n%%EOF\n");
    public static string Digest => Convert.ToHexStringLower(SHA256.HashData(Pdf));
    public EmbeddedPhotoFixture Database { get; private init; }=null!;
    public CmsifyDbContext Db { get; private init; }=null!;
    public Guid WorkspaceId => Database.Workspace.Id;
    public Guid ActorId { get; }=Guid.NewGuid();
    public string Root { get; }=Path.Combine(Path.GetTempPath(),"cmsify-files",Guid.NewGuid().ToString("N"));
    public LocalFileStorageProvider Provider { get; private set; }=null!;
    public EmbeddedFileOptions Settings { get; }=new() { Enabled=true };
    public EmbeddedFileUploadRepository Repository(CmsifyDbContext? db=null) => new(db??Db,new EmbeddedFileStorage(Provider,"local"),Options.Create(Settings),TimeProvider.System);
    public BeginEmbeddedFileUploadRequest Begin() => new(WorkspaceId,Guid.NewGuid(),Guid.NewGuid(),"dog-documents","pedigree.pdf",Pdf.Length,Digest,"pdf-v1");
    public static Task<bool> Permit(EmbeddedFileOperation _,CancellationToken ct) { ct.ThrowIfCancellationRequested();return Task.FromResult(true); }
    public static async Task<EmbeddedFileFixture> Create()
    {
        var database=await EmbeddedPhotoFixture.Create();
        var fixture=new EmbeddedFileFixture { Database=database,Db=database.Context() };
        fixture.Provider=new(Options.Create(new LocalStorageOptions { RootPath=fixture.Root }));
        return fixture;
    }
    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();await Database.DisposeAsync();
        if(Directory.Exists(Root))Directory.Delete(Root,true);
    }
    public sealed class Source(byte[] bytes) : IEmbeddedFileByteSource
    {
        public ValueTask<Stream> OpenReadAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested();return ValueTask.FromResult<Stream>(new MemoryStream(bytes)); }
    }
}
