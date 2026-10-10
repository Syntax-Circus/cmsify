using Cmsify.Core.EmbeddedMedia;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Storage;

internal sealed class StoredPhotoByteSource(IStorageProvider provider, string key) : IEmbeddedPhotoByteSource
{
    public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        var read = await provider.ReadAsync(key, cancellationToken);
        if (read is null) throw new IOException("Private photo object is unavailable.");
        return new OwnedPhotoReadStream(read);
    }
}
internal sealed class OwnedPhotoReadStream(StorageReadResult read) : Stream
{
    private bool _disposed;
    public override int Read(byte[] buffer, int offset, int count) => read.Content.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => read.Content.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => read.Content.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.Content.ReadAsync(buffer, ct);
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; read.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; await read.DisposeAsync(); }
        GC.SuppressFinalize(this);
    }
    public override bool CanRead => read.Content.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => read.SizeBytes;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
