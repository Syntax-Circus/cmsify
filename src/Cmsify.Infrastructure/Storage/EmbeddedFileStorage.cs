using System.Security.Cryptography;
using Cmsify.Core.EmbeddedFiles;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Storage;

public sealed class EmbeddedFileStorage(IStorageProvider provider,string providerName)
{
    public IStorageProvider Provider { get; }=provider;
    public string ProviderName { get; }=EmbeddedFileRules.Named(providerName)?providerName:throw new ArgumentException("Invalid storage provider name.");
    public static string AttemptKey(Guid workspace,Guid asset,Guid token) => $"cmsify/media/files/{workspace:N}/{asset:N}/uploads/{token:N}.original";
    internal async Task<(long Length,string Sha256)> StoreAsync(string key,Stream source,long length,Func<CancellationToken,Task<bool>> guard,CancellationToken ct)
    {
        using var bounded=new EmbeddedFileBoundedReadStream(source,length,guard);
        var stored=await Provider.StoreAsync(new StoreObjectRequest(key,bounded,EmbeddedFileRules.MediaType),ct);
        var proof=bounded.Proof();var metadata=await Provider.GetMetadataAsync(key,ct);
        if(stored.Key!=key || stored.SizeBytes!=proof.Length || metadata?.SizeBytes!=proof.Length)throw new IOException("File storage did not acknowledge complete bytes.");
        return proof;
    }
}
internal sealed class EmbeddedFileInvalidBytesException : IOException;
internal sealed class EmbeddedFileAuthorityLostException : IOException;

internal sealed class EmbeddedFileBoundedReadStream(Stream source,long expectedLength,Func<CancellationToken,Task<bool>> guard,string? expectedDigest=null) : Stream
{
    private readonly IncrementalHash _hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _prefix=new byte[5];
    private long _length;
    private bool _ended;
    public (long Length,string Sha256) Proof()
    {
        if(!_ended || _length!=expectedLength || !_prefix.AsSpan().SequenceEqual("%PDF-"u8))throw new EmbeddedFileInvalidBytesException();
        var digest=Convert.ToHexStringLower(_hash.GetHashAndReset());
        if(expectedDigest is not null && digest!=expectedDigest)throw new EmbeddedFileInvalidBytesException();
        return (_length,digest);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
    {
        if(buffer.IsEmpty)return 0;
        if(!await guard(ct))throw new EmbeddedFileAuthorityLostException();
        var allowed=(int)Math.Min(buffer.Length,Math.Min(expectedLength,EmbeddedFileRules.MaxBytes)-_length+1);
        var count=await source.ReadAsync(buffer[..allowed],ct);
        if(!await guard(ct))throw new EmbeddedFileAuthorityLostException();
        for(var i=0;i<count && _length+i<_prefix.Length;i++)_prefix[_length+i]=buffer.Span[i];
        _length+=count;if(_length>expectedLength || _length>EmbeddedFileRules.MaxBytes)throw new EmbeddedFileInvalidBytesException();
        _hash.AppendData(buffer.Span[..count]);if(count==0)_ended=true;return count;
    }
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct)=>ReadAsync(buffer.AsMemory(offset,count),ct).AsTask();
    public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer,offset,count,CancellationToken.None).GetAwaiter().GetResult();
    public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();public override long Position { get=>throw new NotSupportedException();set=>throw new NotSupportedException(); }
    public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if(disposing)_hash.Dispose();base.Dispose(disposing); }
}
