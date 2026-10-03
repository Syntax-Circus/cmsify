using Cmsify.Core.Domain.Entities;
using Cmsify.Infrastructure.Persistence.Repositories;

namespace Cmsify.Infrastructure.Persistence.Providers;

// Infrastructure SQL only. The repository owns transactions, leases and state changes.
internal interface IMediaReconciliationQueries
{
    Task<List<MediaDeletionIntent>> LockDeletionIntentsAsync(DateTimeOffset now, int limit, CancellationToken ct);
    Task<MediaDeletionIntent?> LockFencedIntentAsync(MediaDeletionClaim claim, DateTimeOffset now, CancellationToken ct);
    Task<List<Guid>> LockStaleUploadIdsAsync(DateTimeOffset cutoff, int limit, CancellationToken ct);
    Task InsertCheckpointAsync(string provider, string prefix, DateTimeOffset now, CancellationToken ct);
    Task<MediaReconciliationCheckpoint?> LockCheckpointAsync(string provider, string prefix, DateTimeOffset now, CancellationToken ct);
    Task<MediaReconciliationCheckpoint?> LockFencedCheckpointAsync(MediaCheckpointClaim claim, DateTimeOffset now, CancellationToken ct);
    Task InsertOrphanIntentAsync(string provider, string storageKey, DateTimeOffset now, CancellationToken ct);
}

internal static class MediaReconciliationQueries
{
    public static IMediaReconciliationQueries Create(CmsifyDbContext context) => context.Database.ProviderName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => new PostgresMediaReconciliationQueries(context),
        "Microsoft.EntityFrameworkCore.Sqlite" => new SqliteMediaReconciliationQueries(context),
        _ => throw new NotSupportedException($"Unsupported media reconciliation provider: {context.Database.ProviderName}")
    };
}
