using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.EmbeddedContent;

internal static class EmbeddedOperationLocks
{
    internal static Task LockAsync(CmsifyDbContext context, string identity, CancellationToken cancellationToken)
    {
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }
}
