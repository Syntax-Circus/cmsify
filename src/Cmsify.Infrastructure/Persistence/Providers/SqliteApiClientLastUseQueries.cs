using Microsoft.EntityFrameworkCore;

namespace Cmsify.Infrastructure.Persistence.Providers;

internal sealed class SqliteApiClientLastUseQueries(CmsifyDbContext context) : IApiClientLastUseQueries
{
    public async Task<int> TouchIfDueAsync(Guid clientId, DateTimeOffset now, TimeSpan touchInterval, CancellationToken ct)
    {
        var caller = context.Database.CurrentTransaction;
        // Microsoft.Data.Sqlite's default transaction is non-deferred: reserve
        // the writer before the overflow read and retain it through the update.
        await using var owned = caller is null ? await context.Database.BeginTransactionAsync(ct) : null;
        var transaction = caller ?? owned!;
        var savepoint = caller is null ? null : $"api_client_last_use_{Guid.NewGuid():N}";
        if (savepoint is not null)
        {
            if (!transaction.SupportsSavepoints)
                throw new NotSupportedException("API-client last-use persistence requires caller transaction savepoints.");
            await transaction.CreateSavepointAsync(savepoint, ct);
        }
        try
        {
            if (caller is not null)
            {
                // A caller may have started a deferred transaction. A zero-row
                // write reserves its writer without mutating or tracking any row.
                await context.ApiClients.Where(client => false).ExecuteUpdateAsync(setters => setters
                    .SetProperty(client => EF.Property<uint>(client, "xmin"), client => EF.Property<uint>(client, "xmin")), ct);
            }
            var due = context.ApiClients.Where(client => client.Id == clientId &&
                (!client.LastUsedAt.HasValue || client.LastUsedAt.Value <= now - touchInterval));
            if (await due.AnyAsync(client => EF.Property<uint>(client, "xmin") == uint.MaxValue, ct))
                throw new OverflowException("An API-client concurrency token is exhausted.");
            // ExecuteUpdate bypasses tracked token generation. Both assignments
            // are one statement, guarded against wrapping under the writer lock.
            var affected = await due.ExecuteUpdateAsync(setters => setters
                .SetProperty(client => client.LastUsedAt, now)
                .SetProperty(client => EF.Property<uint>(client, "xmin"), client => EF.Property<uint>(client, "xmin") + 1u), ct);
            if (savepoint is null) await transaction.CommitAsync(ct);
            else await transaction.ReleaseSavepointAsync(savepoint, ct);
            return affected;
        }
        catch
        {
            // Cancelled operations still roll back. Caller work before the
            // savepoint remains caller-owned; no tracked entry is ever saved.
            if (savepoint is null) await transaction.RollbackAsync(CancellationToken.None);
            else
            {
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            }
            throw;
        }
    }
}
