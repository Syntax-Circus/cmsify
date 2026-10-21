using System.Security.Cryptography;
using Cmsify.Core.EmbeddedMedia;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Storage;

/// <summary>Private CMS-owned objects. No public access URL is issued by this adapter.</summary>
public sealed class EmbeddedPhotoStorage(IStorageProvider provider, string providerName)
{
    public string ProviderName { get; } = ValidateName(providerName);
    public IStorageProvider Provider { get; } = provider;

    public static string AttemptKey(Guid workspaceId, Guid assetId, Guid token) =>
        $"cmsify/media/photos/{workspaceId:N}/{assetId:N}/uploads/{token:N}.original";

    public async Task<StoredPhotoBytes> StoreBoundedAsync(string key, Stream source, long declaredLength,
        string contentType, CancellationToken cancellationToken)
    {
        if (!EmbeddedPhotoPolicy.ValidOriginalLimit(declaredLength))
            throw new PhotoByteValidationException(EmbeddedPhotoErrors.TooLarge);
        using var bounded = new PhotoBoundedReadStream(source, declaredLength,
            declaredLength < EmbeddedPhotoPolicy.MaxBytes ? EmbeddedPhotoErrors.LengthMismatch : EmbeddedPhotoErrors.TooLarge);
        var stored = await Provider.StoreAsync(new StoreObjectRequest(key, bounded, contentType), cancellationToken);
        if (!bounded.EndObserved || bounded.BytesRead != declaredLength)
            throw new PhotoByteValidationException(EmbeddedPhotoErrors.LengthMismatch);
        var metadata = await Provider.GetMetadataAsync(key, cancellationToken);
        if (stored.Key != key || stored.SizeBytes != bounded.BytesRead || metadata?.SizeBytes != bounded.BytesRead)
            throw new IOException("Photo storage did not acknowledge the complete object.");
        return new(ProviderName, key, bounded.BytesRead, bounded.CompleteHash());
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 50) throw new ArgumentException("Storage provider name is invalid.", nameof(name));
        return name;
    }
}

public sealed record StoredPhotoBytes(string Provider, string StorageKey, long Length, string Sha256);

internal sealed class PhotoByteValidationException(string code) : IOException
{
    public string Code { get; } = code;
}

/// <summary>Counts and hashes as the provider reads; one extra byte detects overflow without buffering the file.</summary>
internal sealed class PhotoBoundedReadStream(Stream source, long maxBytes, string overflowCode = EmbeddedPhotoErrors.TooLarge) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long BytesRead { get; private set; }
    public bool EndObserved { get; private set; }
    public string CompleteHash() => Convert.ToHexString(_hash.GetHashAndReset());
    private int Allowed(int requested) => (int)Math.Min(requested, maxBytes - BytesRead + 1);
    private int Observe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) EndObserved = true;
        BytesRead += bytes.Length;
        if (BytesRead > maxBytes) throw new PhotoByteValidationException(overflowCode);
        _hash.AppendData(bytes);
        return bytes.Length;
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        var read = source.Read(buffer, offset, Allowed(count));
        return Observe(buffer.AsSpan(offset, read));
    }
    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        var read = source.Read(buffer[..Allowed(buffer.Length)]);
        return Observe(buffer[..read]);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        var read = await source.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken);
        return Observe(buffer.Span[..read]);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
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
