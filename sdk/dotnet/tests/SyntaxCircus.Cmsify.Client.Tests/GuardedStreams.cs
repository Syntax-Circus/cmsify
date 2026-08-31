using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace SyntaxCircus.Cmsify.Client.Tests;

internal sealed class GuardedNonSeekableReadStream(long length, int maximumReadRequest) : Stream
{
    private long position;

    public long BytesRead => position;
    public int ReadOperationCount { get; private set; }
    public int MaximumObservedReadRequest { get; private set; }
    public bool WasDisposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = ObserveReadRequest(buffer.Length);
        buffer[..count].Fill(0x5a);
        position += count;
        return count;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }

    private int ObserveReadRequest(int requestedBytes)
    {
        if (requestedBytes > maximumReadRequest)
        {
            throw new IOException(
                $"Read request of {requestedBytes} bytes exceeded the {maximumReadRequest}-byte ceiling.");
        }

        MaximumObservedReadRequest = Math.Max(MaximumObservedReadRequest, requestedBytes);
        ReadOperationCount++;
        return (int)Math.Min(requestedBytes, length - position);
    }
}

internal sealed class GuardedWriteStream(int maximumWriteRequest) : Stream
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly TaskCompletionSource firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstWrite => firstWrite.Task;
    public TimeSpan? TimeToFirstWrite { get; private set; }
    public long BytesWritten { get; private set; }
    public int WriteOperationCount { get; private set; }
    public int MaximumObservedWriteRequest { get; private set; }
    public bool WasDisposed { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        ObserveWrite(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => ObserveWrite(buffer);

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObserveWrite(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }

    private void ObserveWrite(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > maximumWriteRequest)
        {
            throw new IOException(
                $"Write of {buffer.Length} bytes exceeded the {maximumWriteRequest}-byte ceiling.");
        }

        if (TimeToFirstWrite is null)
        {
            TimeToFirstWrite = stopwatch.Elapsed;
            firstWrite.TrySetResult();
        }

        BytesWritten += buffer.Length;
        WriteOperationCount++;
        MaximumObservedWriteRequest = Math.Max(MaximumObservedWriteRequest, buffer.Length);
    }
}

internal sealed class CoordinatedStreamingContent(int chunkBytes, int chunkCount) : HttpContent
{
    private readonly TaskCompletionSource firstChunkProduced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource releaseRemainingChunks = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstChunkProduced => firstChunkProduced.Task;
    public bool ProductionCompleted { get; private set; }

    public void ReleaseRemainingChunks() => releaseRemainingChunks.TrySetResult();

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamCoreAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken) =>
        SerializeToStreamCoreAsync(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = (long)chunkBytes * chunkCount;
        return true;
    }

    private async Task SerializeToStreamCoreAsync(Stream stream, CancellationToken cancellationToken)
    {
        var chunk = new byte[chunkBytes];
        await stream.WriteAsync(chunk, cancellationToken);
        firstChunkProduced.TrySetResult();
        await releaseRemainingChunks.Task.WaitAsync(cancellationToken);
        for (var index = 1; index < chunkCount; index++)
        {
            await stream.WriteAsync(chunk, cancellationToken);
        }
        ProductionCompleted = true;
    }
}

internal static class ClientCapacityReportFragmentWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task WriteAsync(
        string reportDirectory,
        string fileName,
        object fragment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(fragment);

        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, fileName);
        var temporaryPath = Path.Combine(reportDirectory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, fragment, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, reportPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
