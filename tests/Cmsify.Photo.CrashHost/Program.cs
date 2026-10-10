using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using SyntaxCircus.Storage;
using System.Buffers.Binary;
using System.Security.Cryptography;
using SyntaxCircus.Common;

// Disposable synthetic orchestration host. No image decoder or native qualification.
if (args.Length != 9) return 2;
var stage = args[0];
if (stage is not ("after-stage" or "before-receipt" or "after-receipt" or
    "processing-after-claim" or "processing-during-variants" or "processing-after-manifest" or
    "reference-after-protection" or "reference-after-release" or "reference-race-protect" or "reference-race-delete")) return 2;
var root = args[2]; var workspace = Guid.Parse(args[3]); var asset = Guid.Parse(args[4]);
var operation = Guid.Parse(args[5]); var writeKey = Guid.Parse(args[6]); var actor = Guid.Parse(args[7]);
var marker = args[8];
var dbOptions = new DbContextOptionsBuilder<CmsifyDbContext>().UseNpgsql(args[1])
    .UseSyntaxCircusSnakeCaseNamingConvention().Options;
await using var db = new CmsifyDbContext(dbOptions);
var provider = new CrashBoundaryStorage(
    new LocalFileStorageProvider(Options.Create(new LocalStorageOptions { RootPath = root })), stage, marker);
if (stage.StartsWith("reference-", StringComparison.Ordinal))
{
    static Task<bool> ReferencePermit(EmbeddedPhotoOperation _, CancellationToken ct) => Task.FromResult(true);
    var intent = Path.Combine(root, operation.ToString("N") + ".intent");
    var referenceRepository = new EmbeddedMediaReferenceRepository(db, new(provider, "local"),
        [new RegisteredEmbeddedMediaRetentionGuard("consumer", new DurableSyntheticGuard(intent))], TimeProvider.System);
    if (stage.StartsWith("reference-race-", StringComparison.Ordinal))
    {
        await File.WriteAllTextAsync(marker, "waiting");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(Path.Combine(root, "reference-race.go"))) await Task.Delay(25, timeout.Token);
    }
    if (stage == "reference-race-delete")
    {
        var media = await db.MediaAssets.AsNoTracking().SingleAsync(x => x.Id == asset);
        var deleted = await new MediaAssetDeletionRepository(db, Options.Create(new MediaOperationalOptions()), TimeProvider.System)
            .DeleteAsync(new(workspace, asset, media.UpdatedAt.UtcTicks / 10), actor, _ => Task.FromResult(true), CancellationToken.None);
        await File.WriteAllTextAsync(marker + ".result", deleted.IsSuccess ? "success" : deleted.Errors[0].Code);
        return 0;
    }
    if (stage == "reference-after-release")
    {
        DurableSyntheticGuard.Write(intent, "retired");
        var released = await referenceRepository.ReleaseAsync(new(workspace, "consumer", operation, writeKey, 1), actor, ReferencePermit, CancellationToken.None);
        if (released.IsFailure) return 8;
        await CrashBoundaryStorage.PauseAsync(marker);
        return 9;
    }
    DurableSyntheticGuard.Write(intent, "pending");
    var manifest = await db.PhotoManifests.AsNoTracking().SingleAsync(x => x.AssetId == asset && x.Revision == 1);
    var protectedSet = await referenceRepository.ProtectAsync(new(workspace, "consumer", operation, writeKey,
        [new(asset, 1, manifest.ManifestSha256)]), actor, ReferencePermit, CancellationToken.None);
    if (stage == "reference-race-protect")
    {
        await File.WriteAllTextAsync(marker + ".result", protectedSet.IsSuccess ? "success" : protectedSet.Errors[0].Code);
        return 0;
    }
    if (protectedSet.IsFailure) return 8;
    await CrashBoundaryStorage.PauseAsync(marker);
    return 9;
}
if (stage.StartsWith("processing-", StringComparison.Ordinal))
{
    var settings = Options.Create(new EmbeddedPhotoOptions { Enabled = true, ProcessingEnabled = true, PolicyId = "synthetic-photo-v1" });
    var storage = new EmbeddedPhotoStorage(provider, "local");
    var processRepository = new EmbeddedPhotoProcessingRepository(db, storage, settings, new SyntheticCapability(),
        new SyntheticJobAuthorization(), TimeProvider.System);
    var worker = new EmbeddedPhotoProcessingProcessor(processRepository, storage, new SyntheticProcessor(stage, marker),
        new SyntheticCapability(), settings, TimeProvider.System);
    if (!await worker.ProcessNextAsync(actor, CancellationToken.None)) return 6;
    var manifest = await db.PhotoManifests.SingleAsync();
    await File.WriteAllTextAsync(Path.Combine(root, "manifest-id"), manifest.ManifestSha256);
    await CrashBoundaryStorage.PauseAsync(marker);
    return 7;
}
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
internal sealed class DurableSyntheticGuard(string intent) : IEmbeddedMediaRetentionGuard
{
    public static void Write(string path, string state)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        file.Write(System.Text.Encoding.UTF8.GetBytes(state)); file.Flush(flushToDisk: true);
    }
    public async Task<EmbeddedMediaRetentionDecision> CheckAsync(EmbeddedMediaRetentionRequest request, CancellationToken ct) =>
        File.Exists(intent) && await File.ReadAllTextAsync(intent, ct) == "retired"
            ? EmbeddedMediaRetentionDecision.ReleaseAllowed : EmbeddedMediaRetentionDecision.Unknown;
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
        if (stage == "after-stage" || stage == "processing-during-variants" && request.Key.Contains("/revisions/", StringComparison.Ordinal))
            await PauseAsync(marker);
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

internal sealed class SyntheticJobAuthorization : IEmbeddedPhotoProcessingAuthorization
{
    public Task<bool> CanExecuteAsync(Guid actorId, EmbeddedPhotoOperation operation, CancellationToken cancellationToken) =>
        Task.FromResult(actorId != Guid.Empty);
}
internal sealed class SyntheticProcessor(string stage, string marker) : IEmbeddedPhotoProcessor
{
    public async Task<Result<EmbeddedPhotoCandidate>> ProcessAsync(EmbeddedPhotoProcessingInput input, CancellationToken cancellationToken)
    {
        if (stage == "processing-after-claim") await CrashBoundaryStorage.PauseAsync(marker);
        var variants = EmbeddedPhotoPolicy.RequiredVariants.Select(requirement =>
        {
            var fit = EmbeddedPhotoPolicy.Fit(640, 480, requirement.Bound);
            var bytes = new byte[28];
            "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 20);
            "WEBPVP8L"u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 8);
            bytes[20] = 0x2f;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), (uint)(fit.Width - 1) | (uint)(fit.Height - 1) << 14);
            bytes[25] = 1; bytes[26] = 1; bytes[27] = 1;
            return new EmbeddedPhotoCandidateVariant(new(requirement.VariantKey, EmbeddedPhotoPolicy.WebpContentType,
                fit.Width, fit.Height, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))), new SyntheticBytes(bytes));
        }).ToArray();
        return Result<EmbeddedPhotoCandidate>.Success(new(new("HEIC", 640, 480, 8, "sRGB", false, true), variants));
    }
}
internal sealed class SyntheticBytes(byte[] bytes) : IEmbeddedPhotoByteSource
{
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<Stream>(new MemoryStream(bytes, false));
}
