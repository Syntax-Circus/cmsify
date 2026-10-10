using System.Buffers.Binary;
using Cmsify.Core.EmbeddedMedia;

namespace Cmsify.Infrastructure.Storage;

/// <summary>
/// Bounded structural check of CMS derivatives. This does not validate compressed pixels,
/// color conversion or decoder safety; those still require the qualified isolated adapter.
/// Structure: https://developers.google.com/speed/webp/docs/riff_container
/// </summary>
internal static class EmbeddedPhotoWebpContainer
{
    internal static async Task ValidateAsync(Stream stream, EmbeddedPhotoVariantDescriptor descriptor, bool expectedAlpha,
        CancellationToken cancellationToken)
    {
        var header = new byte[12]; await Exact(stream, header, cancellationToken);
        Require(header.AsSpan(0,4).SequenceEqual("RIFF"u8) && header.AsSpan(8,4).SequenceEqual("WEBP"u8) &&
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) + 8L == descriptor.Length);
        long remaining = descriptor.Length - 12;
        bool extended = false, alphaChunk = false, image = false, extendedAlpha = false, imageAlpha = false;
        int? canvasWidth = null, canvasHeight = null;
        int chunks = 0;
        while (remaining > 0)
        {
            Require(++chunks <= 3 && remaining >= 8 && !image);
            var chunk = new byte[8]; await Exact(stream, chunk, cancellationToken);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            var padded = length + (long)(length & 1);
            remaining -= 8; Require(padded <= remaining);
            var id = System.Text.Encoding.ASCII.GetString(chunk, 0, 4);
            int prefixLength = id switch { "VP8X" => 10, "VP8 " => 10, "VP8L" => 5, "ALPH" => 1, _ => 0 };
            Require(prefixLength > 0 && length >= prefixLength);
            var prefix = new byte[prefixLength]; await Exact(stream, prefix, cancellationToken);
            switch (id)
            {
                case "VP8X":
                    Require(chunks == 1 && length == 10 && !extended &&
                        (prefix[0] & ~0x10) == 0 && prefix[1] == 0 && prefix[2] == 0 && prefix[3] == 0);
                    extended = true; extendedAlpha = (prefix[0] & 0x10) != 0;
                    canvasWidth = Read24(prefix.AsSpan(4)) + 1; canvasHeight = Read24(prefix.AsSpan(7)) + 1;
                    break;
                case "ALPH":
                    Require(extended && extendedAlpha && !alphaChunk && !image);
                    Require((prefix[0] & 0xc0) == 0 && (prefix[0] & 3) <= 1 && ((prefix[0] >> 4) & 3) <= 1);
                    alphaChunk = true;
                    break;
                case "VP8L":
                    Require(!alphaChunk && prefix[0] == 0x2f);
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(1));
                    Require(bits >> 29 == 0);
                    imageAlpha = (bits & (1u << 28)) != 0;
                    Dimensions((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1,
                        descriptor, canvasWidth, canvasHeight);
                    image = true;
                    break;
                case "VP8 ":
                    Require((prefix[0] & 1) == 0 && prefix.AsSpan(3,3).SequenceEqual(new byte[] { 0x9d,0x01,0x2a }));
                    imageAlpha = alphaChunk;
                    Dimensions(BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(6)) & 0x3fff,
                        BinaryPrimitives.ReadUInt16LittleEndian(prefix.AsSpan(8)) & 0x3fff,
                        descriptor, canvasWidth, canvasHeight);
                    image = true;
                    break;
            }
            var skip = length - prefixLength;
            var buffer = new byte[8192];
            while (skip > 0)
            {
                var count = (int)Math.Min(skip, buffer.Length);
                await Exact(stream, buffer.AsMemory(0,count), cancellationToken); skip -= count;
            }
            if ((length & 1) != 0) { var pad = new byte[1]; await Exact(stream, pad, cancellationToken); Require(pad[0] == 0); }
            remaining -= padded;
        }
        Require(image && imageAlpha == expectedAlpha && (!extended || extendedAlpha == imageAlpha));
        var extra = new byte[1]; Require(await stream.ReadAsync(extra, cancellationToken) == 0);
    }
    private static int Read24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
    private static void Dimensions(int width, int height, EmbeddedPhotoVariantDescriptor descriptor, int? canvasWidth, int? canvasHeight) =>
        Require(width == descriptor.Width && height == descriptor.Height &&
            (!canvasWidth.HasValue || canvasWidth == width && canvasHeight == height));
    private static void Require(bool condition)
    {
        if (!condition) throw new PhotoByteValidationException(EmbeddedPhotoErrors.InvalidCandidate);
    }
    private static async Task Exact(Stream stream, Memory<byte> bytes, CancellationToken ct)
    {
        try { await stream.ReadExactlyAsync(bytes, ct); }
        catch (EndOfStreamException) { throw new PhotoByteValidationException(EmbeddedPhotoErrors.InvalidCandidate); }
    }
}
