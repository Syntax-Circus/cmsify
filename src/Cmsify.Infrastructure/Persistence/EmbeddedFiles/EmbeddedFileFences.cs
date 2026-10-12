using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cmsify.Core.EmbeddedFiles;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.EmbeddedFiles;

internal static class EmbeddedFileFences
{
    internal static string Fingerprint(object value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    internal static void Provider(CmsifyDbContext db){if(!db.Database.IsNpgsql())throw new NotSupportedException("Embedded files require PostgreSQL.");}
    internal static EmbeddedFileOperation Operation(FileUploadSessionRecord s,EmbeddedFileOperationKind kind)=>new(kind,s.WorkspaceId,s.AssetId,s.Id,ConsumerNamespace:s.ConsumerNamespace,ActorId:s.ActorId);
    internal static EmbeddedFileReceipt Receipt(FileUploadReceiptRecord r)=>new(r.Id,r.WorkspaceId,r.AssetId,r.ConsumerNamespace,r.Length,r.SourceSha256,EmbeddedFileRules.MediaType);
    internal static async Task<FileUploadSessionRecord?> SessionAsync(CmsifyDbContext db,Guid id,CancellationToken ct)
    {
        var fresh=await db.FileUploadSessions.FromSqlInterpolated($"SELECT * FROM embedded_file_upload_sessions WHERE id={id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
        if(fresh is null)return null;
        var local=db.FileUploadSessions.Local.FirstOrDefault(x=>x.Id==id);
        if(local is null){db.FileUploadSessions.Attach(fresh);return fresh;}
        db.Entry(local).CurrentValues.SetValues(fresh);db.Entry(local).OriginalValues.SetValues(fresh);db.Entry(local).State=EntityState.Unchanged;return local;
    }
}
