using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cmsify.Infrastructure.Persistence;

/// <summary>
/// Shared database write scope for the bounded legacy API mutation paths. A failed
/// operation rolls back its own writes, including saves preceding archival.
/// Caller transactions retain commit ownership and any work before the savepoint.
/// </summary>
internal sealed class LegacyWriteScope : IAsyncDisposable
{
    private readonly IDbContextTransaction _transaction;
    private readonly string? _savepointName;
    private readonly CmsifyDbContext _context;
    private readonly EntryCheckpoint[] _checkpoint;
    private bool _completed;

    private LegacyWriteScope(CmsifyDbContext context, IDbContextTransaction transaction, string? savepointName, EntryCheckpoint[] checkpoint)
    {
        _context = context;
        _transaction = transaction;
        _savepointName = savepointName;
        _checkpoint = checkpoint;
    }

    public static async Task<LegacyWriteScope> BeginAsync(CmsifyDbContext context, string savepointPrefix, string unsupportedMessage, CancellationToken cancellationToken)
    {
        var checkpoint = context.ChangeTracker.Entries().Select(EntryCheckpoint.Capture).ToArray();
        if (context.Database.CurrentTransaction is not { } callerTransaction)
            return new(context, await context.Database.BeginTransactionAsync(cancellationToken), null, checkpoint);

        if (!callerTransaction.SupportsSavepoints)
            throw new NotSupportedException(unsupportedMessage);

        var savepointName = $"{savepointPrefix}_{Guid.NewGuid():N}";
        await callerTransaction.CreateSavepointAsync(savepointName, cancellationToken);
        return new(context, callerTransaction, savepointName, checkpoint);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_savepointName is null)
            await _transaction.CommitAsync(cancellationToken);
        else
            await _transaction.ReleaseSavepointAsync(_savepointName, cancellationToken);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed)
            {
                // Cleanup must still execute when the operation's token was cancelled.
                if (_savepointName is null)
                    await _transaction.RollbackAsync(CancellationToken.None);
                else
                {
                    await _transaction.RollbackToSavepointAsync(_savepointName, CancellationToken.None);
                    await _transaction.ReleaseSavepointAsync(_savepointName, CancellationToken.None);
                }
                RestoreTracker();
            }
        }
        finally
        {
            if (_savepointName is null) await _transaction.DisposeAsync();
        }
    }

    private void RestoreTracker()
    {
        // Earlier saves accept tracked changes even when their database writes are
        // subsequently rolled back. Keep caller entries and restore their pending
        // intent; detach only entities first tracked during this operation.
        var retained = _checkpoint.Select(checkpoint => checkpoint.Entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var checkpoint in _checkpoint) checkpoint.RestoreValues();
        foreach (var checkpoint in _checkpoint) checkpoint.RestoreNavigations();
        // Let EF reconcile its relationship snapshots before detaching the removed
        // operation graph; otherwise a later save can rediscover it as an orphan.
        _context.ChangeTracker.DetectChanges();
        foreach (var entry in _context.ChangeTracker.Entries().ToArray())
            if (!retained.Contains(entry.Entity)) entry.State = EntityState.Detached;
        foreach (var checkpoint in _checkpoint) checkpoint.RestoreState();
    }

    // Private to these bounded legacy Cmsify paths, not an application unit-of-work or
    // a general tracker snapshot API. Cmsify uses flat scalar properties and CLR
    // reference/collection navigations; configured comparers snapshot mutable values.
    private sealed record EntryCheckpoint(EntityEntry Entry, EntityState State,
        PropertyCheckpoint[] Properties, NavigationCheckpoint[] Navigations)
    {
        public static EntryCheckpoint Capture(EntityEntry entry) => new(entry, entry.State,
            entry.Properties.Select(property => new PropertyCheckpoint(property.Metadata.Name,
                Snapshot(property, property.CurrentValue), Snapshot(property, property.OriginalValue),
                property.IsModified, property.IsTemporary)).ToArray(),
            entry.Navigations.Select(navigation => new NavigationCheckpoint(navigation.Metadata.Name,
                navigation.CurrentValue, navigation.Metadata.IsCollection
                    ? ((IEnumerable?)navigation.CurrentValue)?.Cast<object>().ToArray() : null, navigation.IsLoaded)).ToArray());

        private static object? Snapshot(PropertyEntry property, object? value) =>
            property.Metadata.GetValueComparer()?.Snapshot(value) ?? value;

        public void RestoreValues()
        {
            Entry.State = EntityState.Unchanged;
            foreach (var property in Properties)
            {
                Entry.Property(property.Name).CurrentValue = property.Current;
                Entry.Property(property.Name).OriginalValue = property.Original;
            }
        }

        public void RestoreNavigations()
        {
            foreach (var snapshot in Navigations)
            {
                var navigation = Entry.Navigation(snapshot.Name);
                navigation.CurrentValue = snapshot.Value;
                if (snapshot.Items is not null)
                {
                    var accessor = navigation.Metadata.GetCollectionAccessor()!;
                    foreach (var item in ((IEnumerable)navigation.CurrentValue!).Cast<object>().ToArray())
                        accessor.RemoveStandalone(navigation.CurrentValue!, item);
                    foreach (var item in snapshot.Items) accessor.AddStandalone(navigation.CurrentValue!, item);
                }
                navigation.IsLoaded = snapshot.IsLoaded;
            }
        }

        public void RestoreState()
        {
            Entry.State = State;
            foreach (var property in Properties)
            {
                Entry.Property(property.Name).IsModified = property.IsModified;
                Entry.Property(property.Name).IsTemporary = property.IsTemporary;
            }
        }
    }

    private sealed record PropertyCheckpoint(string Name, object? Current, object? Original, bool IsModified, bool IsTemporary);
    private sealed record NavigationCheckpoint(string Name, object? Value, object[]? Items, bool IsLoaded);
}
