using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using SyntaxCircus.Storage;

// Disposable synthetic orchestration host. No image decoder or native qualification.
if (args.Length != 9) return 2;
var stage = args[0];
if (stage is not ("after-stage" or "before-receipt" or "after-receipt")) return 2;
var root = args[2]; var workspace = Guid.Parse(args[3]); var asset = Guid.Parse(args[4]);
var operation = Guid.Parse(args[5]); var writeKey = Guid.Parse(args[6]); var actor = Guid.Parse(args[7]);
var marker = args[8];
var dbOptions = new DbContextOptionsBuilder<CmsifyDbContext>().UseNpgsql(args[1])
    .UseSyntaxCircusSnakeCaseNamingConvention().Options;
await using var db = new CmsifyDbContext(dbOptions);
var provider = new CrashBoundaryStorage(
    new LocalFileStorageProvider(Options.Create(new LocalStorageOptions { RootPath = root })), stage, marker);
var repository = new EmbeddedPhotoUploadRepository(db, new EmbeddedPhotoStorage(provider, "local"),
    Options.Create(new EmbeddedPhotoOptions { Enabled = true, PolicyId = "synthetic-photo-v1" }),
    new SyntheticCapability(), TimeProvider.System);
static Task<bool> Permit(EmbeddedPhotoOperation _, CancellationToken ct) => Task.FromResult(true);
var begin = await repository.BeginAsync(new(workspace, asset, operation, "synthetic.heic", 3, "synthetic-photo-v1"),
    actor, Permit, CancellationToken.None);
if (begin.IsFailure) return 3;
var written = await repository.WriteAsync(new(workspace, begin.Value.SessionId, writeKey, new SyntheticSource()),
    actor, Permit, CancellationToken.None);
if (written.IsFailure) return 4;
await File.WriteAllTextAsync(Path.Combine(root, "receipt-id"), written.Value.Receipt!.ReceiptId.ToString());
await CrashBoundaryStorage.PauseAsync(marker);
return 5;

internal sealed class SyntheticSource : IEmbeddedPhotoByteSource
{
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new MemoryStream([1,2,3]));
}
internal sealed class SyntheticCapability : IEmbeddedPhotoProcessorCapability
{
    public Task<EmbeddedPhotoCapability> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
        new EmbeddedPhotoCapability(true, true, "synthetic-photo-v1", "synthetic-only-1", "TEST-ONLY-NO-NATIVE-QUALIFICATION", null));
}
internal sealed class CrashBoundaryStorage(IStorageProvider inner, string stage, string marker) : IStorageProvider
{
    private int _metadataCalls;
    public static async Task PauseAsync(string marker)
    {
        await File.WriteAllTextAsync(marker, "boundary reached; parent must terminate this real process");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    public async Task<StoredObject> StoreAsync(StoreObjectRequest request, CancellationToken cancellationToken = default)
    {
        var result = await inner.StoreAsync(request, cancellationToken);
        if (stage == "after-stage") await PauseAsync(marker);
        return result;
    }
    public async Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await inner.GetMetadataAsync(key, cancellationToken);
        if (++_metadataCalls == 2 && stage == "before-receipt") await PauseAsync(marker);
        return result;
    }
    public Task<StorageReadResult?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => inner.ExistsAsync(key, cancellationToken);
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
    public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest request, CancellationToken cancellationToken = default) => inner.ListAsync(request, cancellationToken);
    public Task<string> GetAccessUrlAsync(string key, TimeSpan? expiry = null, CancellationToken cancellationToken = default) => inner.GetAccessUrlAsync(key, expiry, cancellationToken);
}
