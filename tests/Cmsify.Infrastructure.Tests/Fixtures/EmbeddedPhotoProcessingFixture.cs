using System.Buffers.Binary;
using System.Security.Cryptography;
using Cmsify.Core.EmbeddedMedia;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.EmbeddedMedia;
using Cmsify.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using SyntaxCircus.Common;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests.Fixtures;

// Synthetic container headers test orchestration, not compressed pixels or native processing.
internal sealed class EmbeddedPhotoProcessingFixture : IAsyncDisposable
{
    public EmbeddedPhotoFixture Database { get; private init; } = null!;
    public Guid WorkspaceId => Database.Workspace.Id;
    public Guid ActorId { get; } = Guid.NewGuid();
    public Clock Time { get; } = new();
    public TestCapability Capability { get; } = new();
    public TestJobAuthorization Authorization { get; } = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cmsify-photo-process-test", Guid.NewGuid().ToString("N"));
    public EmbeddedPhotoOptions Settings { get; } = new() { Enabled = true, ProcessingEnabled = true, PolicyId = "synthetic-photo-v1" };
    public EmbeddedPhotoStorage Storage { get; private set; } = null!;
    public static CancellationToken Ct => EmbeddedPhotoFixture.Ct;
    public static Task<bool> Permit(EmbeddedPhotoOperation _, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }
    public CmsifyDbContext Context() => Database.Context();
    public EmbeddedPhotoProcessingRepository Repository(CmsifyDbContext db) => new(db, Storage, Options.Create(Settings), Capability, Authorization, Time);
    public EmbeddedPhotoProcessingProcessor Worker(CmsifyDbContext db, IEmbeddedPhotoProcessor processor) =>
        new(Repository(db), Storage, processor, Capability, Options.Create(Settings), Time);

    public static async Task<EmbeddedPhotoProcessingFixture> Create()
    {
        var f = new EmbeddedPhotoProcessingFixture { Database = await EmbeddedPhotoFixture.Create() };
        f.Storage = new(new LocalFileStorageProvider(Options.Create(new LocalStorageOptions { RootPath = f.Root })), "local");
        return f;
    }
    public async Task<Guid> Upload()
    {
        await using var db = Context();
        var repository = new EmbeddedPhotoUploadRepository(db, Storage, Options.Create(Settings), Capability, Time);
        var asset = Guid.NewGuid();
        var begin = await repository.BeginAsync(new(WorkspaceId, asset, Guid.NewGuid(), "synthetic.heic", 3, Settings.PolicyId), ActorId, Permit, Ct);
        if (begin.IsFailure) throw new InvalidOperationException(begin.Errors[0].Code);
        var result = await repository.WriteAsync(new(WorkspaceId, begin.Value.SessionId, Guid.NewGuid(), new Bytes([1,2,3])), ActorId, Permit, Ct);
        if (result.IsFailure) throw new InvalidOperationException(result.Errors[0].Code);
        return asset;
    }
    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class TestCapability : IEmbeddedPhotoProcessorCapability
    {
        public bool Available { get; set; } = true;
        public Task<EmbeddedPhotoCapability> GetAsync(CancellationToken ct) => Task.FromResult(
            new EmbeddedPhotoCapability(Available, true, "synthetic-photo-v1", "synthetic-only-1", "TEST-ONLY-NO-NATIVE-QUALIFICATION", null));
    }
    internal sealed class TestJobAuthorization : IEmbeddedPhotoProcessingAuthorization
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> CanExecuteAsync(Guid actorId, EmbeddedPhotoOperation operation, CancellationToken ct) => Task.FromResult(Allowed && actorId != Guid.Empty);
    }
    internal sealed class Bytes(byte[] bytes) : IEmbeddedPhotoByteSource
    {
        public ValueTask<Stream> OpenReadAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(new MemoryStream(bytes, false)); }
    }
    internal sealed class CandidateProcessor(string? defect = null, int width = 640, int height = 480, bool alpha = false, byte marker = 1) : IEmbeddedPhotoProcessor
    {
        public int CleanupCount { get; private set; }
        public Task<Result<EmbeddedPhotoCandidate>> ProcessAsync(EmbeddedPhotoProcessingInput input, CancellationToken ct)
        {
            var metadata = new EmbeddedPhotoSourceMetadata(defect == "metadata" ? "PDF" : "HEIC", width, height,
                defect == "bitDepth" ? 16 : 8, "sRGB", alpha, true);
            var dimensions = input.Instructions.QuarterTurnsClockwise % 2 == 0 ? (width, height) : (height, width);
            var variants = EmbeddedPhotoPolicy.RequiredVariants.Select(requirement =>
            {
                var fit = EmbeddedPhotoPolicy.Fit(dimensions.Item1, dimensions.Item2, requirement.Bound);
                var bytes = Container(fit.Width, fit.Height, alpha, marker);
                if (defect == "color") bytes = WithMetadata(bytes);
                if (defect == "container") bytes[0] = 0;
                if (defect == "alpha") bytes[24] ^= 0x10;
                var descriptor = new EmbeddedPhotoVariantDescriptor(requirement.VariantKey, EmbeddedPhotoPolicy.WebpContentType,
                    fit.Width, fit.Height, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
                if (defect == "checksum") descriptor = descriptor with { Sha256 = new('0', 64) };
                if (defect == "oversized") descriptor = descriptor with { Length = EmbeddedPhotoPolicy.MaxBytes + 1 };
                if (defect == "dimensions") descriptor = descriptor with { Width = fit.Width + 1 };
                if (defect == "content") descriptor = descriptor with { ContentType = "image/jpeg" };
                if (defect == "path") descriptor = descriptor with { VariantKey = "../../claimed-path" };
                return new EmbeddedPhotoCandidateVariant(descriptor, new Bytes(bytes));
            }).ToList();
            if (defect == "missing") variants.RemoveAt(2);
            if (defect == "duplicate") variants[2] = variants[0];
            if (defect == "extra") variants.Add(variants[0]);
            return Task.FromResult(Result<EmbeddedPhotoCandidate>.Success(new(metadata, variants,
                () => { CleanupCount++; return ValueTask.CompletedTask; })));
        }
        private static byte[] Container(int width, int height, bool alpha, byte marker)
        {
            var bytes = new byte[28];
            "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 20);
            "WEBPVP8L"u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 8);
            bytes[20] = 0x2f;
            uint bits = (uint)(width - 1) | (uint)(height - 1) << 14 | (alpha ? 1u << 28 : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), bits);
            bytes[25] = marker; bytes[26] = marker; bytes[27] = marker;
            return bytes;
        }
        private static byte[] WithMetadata(byte[] original)
        {
            var bytes = new byte[original.Length + 10]; original.CopyTo(bytes, 0);
            "ICCP"u8.CopyTo(bytes.AsSpan(original.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(original.Length + 4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
            return bytes;
        }
    }
    internal sealed class FailingProcessor(string code = EmbeddedPhotoErrors.ProcessingFailed) : IEmbeddedPhotoProcessor
    {
        public Task<Result<EmbeddedPhotoCandidate>> ProcessAsync(EmbeddedPhotoProcessingInput input, CancellationToken ct) =>
            Task.FromResult(Result<EmbeddedPhotoCandidate>.Failure(new(code, "synthetic failure", ResultErrorKind.Validation)));
    }
    internal sealed class GateProcessor : IEmbeddedPhotoProcessor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Result<EmbeddedPhotoCandidate>> ProcessAsync(EmbeddedPhotoProcessingInput input, CancellationToken ct)
        {
            Started.TrySetResult(); await Resume.Task.WaitAsync(ct);
            return await new CandidateProcessor(marker: 9).ProcessAsync(input, ct);
        }
    }
}
