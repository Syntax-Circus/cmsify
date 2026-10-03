using System.Data.Common;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

// Execute the production auth helper using independent file-backed SQLite contexts
// and migrated PostgreSQL. EnsureCreated is a bounded test adaptation only.
public sealed class ApiClientTouchProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualTouch_RejectsAnIndependentStaleTrackedSave(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var clientId = await SeedAsync(database);
        await using var editor = database.Context();
        var stale = await editor.ApiClients.SingleAsync(client => client.Id == clientId, Ct);
        await using (var writer = database.Context())
            await TouchAsync(writer, clientId);

        stale.Name = "stale overwrite";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => editor.SaveChangesAsync(Ct));
        await using var verification = database.Context();
        var persisted = await verification.ApiClients.SingleAsync(client => client.Id == clientId, Ct);
        persisted.Name.ShouldBe("client");
        persisted.LastUsedAt.ShouldBe(Now);
        if (!postgres) verification.Entry(persisted).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualTouch_PreservesDueBoundaryFiltersWorkspacesAndUntouchedFields(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var id = await SeedAsync(database);
        await using (var setup = database.Context())
        {
            var source = await setup.ApiClients.SingleAsync(Ct);
            source.LastUsedAt = Now.AddMinutes(-5);
            // These rules belong to credential verification, not this persistence repair.
            source.IsActive = false;
            source.ExpiresAt = Now.AddDays(-1);
            var otherWorkspace = new Workspace { Name = "other", Slug = "other" };
            setup.Add(otherWorkspace);
            setup.Add(new ApiClient { Name = "other", TokenHash = "hash", CreatedByUserId = source.CreatedByUserId, WorkspaceId = otherWorkspace.Id });
            setup.Add(new ApiClient { Name = "deleted", TokenHash = "hash", CreatedByUserId = source.CreatedByUserId, IsDeleted = true });
            await setup.SaveChangesAsync(Ct);
        }
        var baseline = await SnapshotAsync(database);
        var commands = new ObserveCommands();
        await using (var writer = database.Context(commands))
        {
            await TouchAsync(writer, id, Now.AddMinutes(-5)); // equality is due
            writer.ChangeTracker.Entries().ShouldBeEmpty();
        }
        commands.Materialized.ShouldBe(0);
        commands.TouchUpdates.ShouldBe(1);
        await using var read = database.Context();
        foreach (var client in await read.ApiClients.IgnoreQueryFilters().ToListAsync(Ct))
        {
            var before = baseline[client.Id];
            foreach (var property in read.Entry(client).Properties)
            {
                if (client.Id == id && property.Metadata.Name == nameof(ApiClient.LastUsedAt))
                    property.CurrentValue.ShouldBe(Now);
                else if (client.Id == id && property.Metadata.Name == "xmin")
                {
                    if (postgres) property.CurrentValue.ShouldNotBe(before[property.Metadata.Name]);
                    else property.CurrentValue.ShouldBe((uint)before[property.Metadata.Name]! + 1u);
                }
                else property.CurrentValue.ShouldBe(before[property.Metadata.Name], property.Metadata.Name);
            }
        }
        var after = await SnapshotAsync(database);
        await using (var writer = database.Context())
        {
            await TouchAsync(writer, id, null); // database recent, stale/null caller snapshot
            await TouchAsync(writer, id, null, Now.AddMinutes(-10)); // delayed older touch
            await TouchAsync(writer, Guid.NewGuid()); // missing ID
            await TouchAsync(writer, after.Single(pair => (string)pair.Value["Name"]! == "deleted").Key);
        }
        await AssertSnapshotAsync(database, after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecentCallerSnapshot_SkipsDatabaseEvenWhenDatabaseIsDueAndTokenCancelled(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var id = await SeedAsync(database);
        var baseline = await SnapshotAsync(database);
        var commands = new ObserveCommands();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using (var context = database.Context(commands))
            await TouchAsync(context, id, Now.AddMinutes(-5).AddTicks(1), ct: cancellation.Token);
        commands.Commands.ShouldBe(0);
        await AssertSnapshotAsync(database, baseline);
    }

    [Fact]
    public async Task SqliteTouch_PreservesNonzeroOffsetInstant()
    {
        await using var database = await Database.CreateAsync(false);
        var id = await SeedAsync(database);
        var offsetNow = Now.ToOffset(TimeSpan.FromHours(-7)).AddTicks(3);
        await using (var writer = database.Context()) await TouchAsync(writer, id, now: offsetNow);
        await using var read = database.Context();
        (await read.ApiClients.SingleAsync(Ct)).LastUsedAt.ShouldBe(offsetNow);
    }

    [Fact]
    public async Task SqliteTouch_ExhaustionRejectsOnlySelectedDueRow()
    {
        await using var database = await Database.CreateAsync(false);
        var id = await SeedAsync(database);
        await using (var setup = database.Context())
            await setup.ApiClients.ExecuteUpdateAsync(setters => setters.SetProperty(client => EF.Property<uint>(client, "xmin"), uint.MaxValue), Ct);
        var baseline = await SnapshotAsync(database);
        await using (var writer = database.Context())
            await Should.ThrowAsync<OverflowException>(() => TouchAsync(writer, id));
        await AssertSnapshotAsync(database, baseline);
        await using (var writer = database.Context())
        {
            await TouchAsync(writer, Guid.NewGuid());
            await TouchAsync(writer, id, Now); // fast-path exhausted row
            await writer.ApiClients.ExecuteUpdateAsync(setters => setters.SetProperty(client => client.LastUsedAt, Now), Ct);
            await TouchAsync(writer, id); // not-due exhausted row
        }
        await using (var setup = database.Context())
        {
            var source = await setup.ApiClients.SingleAsync(Ct);
            var other = new ApiClient { Name = "due", TokenHash = "hash", CreatedByUserId = source.CreatedByUserId };
            setup.Add(other);
            await setup.SaveChangesAsync(Ct);
            id = other.Id;
        }
        await using (var writer = database.Context()) await TouchAsync(writer, id); // unrelated exhaustion
        await using var read = database.Context();
        var touched = await read.ApiClients.SingleAsync(client => client.Id == id, Ct);
        read.Entry(touched).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
    }

    [Fact]
    public async Task SqliteTouch_LastAvailableRevisionSucceedsThenNextDueTouchRejectsWithoutWrapping()
    {
        await using var database = await Database.CreateAsync(false);
        var id = await SeedAsync(database);
        await using (var setup = database.Context())
            await setup.ApiClients.ExecuteUpdateAsync(setters => setters.SetProperty(client => EF.Property<uint>(client, "xmin"), uint.MaxValue - 1u), Ct);
        await using (var writer = database.Context()) await TouchAsync(writer, id);
        var baseline = await SnapshotAsync(database);
        baseline[id]["xmin"].ShouldBe(uint.MaxValue);
        await using (var writer = database.Context())
        {
            await Should.ThrowAsync<OverflowException>(() => TouchAsync(writer, id, now: Now.AddMinutes(5)));
            writer.Database.CurrentTransaction.ShouldBeNull();
        }
        await AssertSnapshotAsync(database, baseline);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Touch_PreservesCallerTrackedStateAndCommitOwnership(bool postgres, bool commit)
    {
        await using var database = await Database.CreateAsync(postgres);
        var id = await SeedAsync(database);
        var baseline = await SnapshotAsync(database);
        await using (var writer = database.Context())
        {
            await using var caller = await writer.Database.BeginTransactionAsync(Ct);
            var tracked = await writer.ApiClients.SingleAsync(Ct);
            tracked.Description = "pending edit";
            var pending = new Workspace { Name = "pending", Slug = "pending" };
            writer.Add(pending);
            writer.ChangeTracker.DetectChanges();
            var originalToken = writer.Entry(tracked).Property<uint>("xmin").OriginalValue;
            await TouchAsync(writer, id);
            writer.Database.CurrentTransaction.ShouldBeSameAs(caller);
            writer.Entry(tracked).State.ShouldBe(EntityState.Modified);
            writer.Entry(tracked).Property(client => client.Description).IsModified.ShouldBeTrue();
            tracked.Description.ShouldBe("pending edit");
            tracked.LastUsedAt.ShouldBeNull();
            writer.Entry(tracked).Property<uint>("xmin").OriginalValue.ShouldBe(originalToken);
            writer.Entry(tracked).Property<uint>("xmin").CurrentValue.ShouldBe(originalToken);
            writer.Entry(pending).State.ShouldBe(EntityState.Added);
            if (commit) await caller.CommitAsync(Ct);
            else await caller.RollbackAsync(Ct);
        }
        await using var read = database.Context();
        var persisted = await read.ApiClients.SingleAsync(Ct);
        persisted.LastUsedAt.ShouldBe(commit ? Now : null);
        persisted.Description.ShouldBe("untouched");
        (await read.Workspaces.CountAsync(Ct)).ShouldBe(1);
        if (!commit) await AssertSnapshotAsync(database, baseline);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SqliteTouch_LateFailureOrCancellationRollsBackAndPreservesCallerWork(bool callerOwned, bool cancel)
    {
        await using var database = await Database.CreateAsync(false);
        var id = await SeedAsync(database);
        var baseline = await SnapshotAsync(database);
        using var cancellation = new CancellationTokenSource();
        var fault = new FailAfterTouch(cancel ? cancellation : null);
        await using (var writer = database.Context(fault))
        {
            await using var caller = callerOwned ? await writer.Database.BeginTransactionAsync(Ct) : null;
            if (callerOwned)
                await writer.Workspaces.ExecuteUpdateAsync(setters => setters.SetProperty(workspace => workspace.Description, "earlier caller write"), Ct);
            var tracked = await writer.ApiClients.SingleAsync(Ct);
            tracked.Description = "pending edit";
            writer.ChangeTracker.DetectChanges();
            var token = writer.Entry(tracked).Property<uint>("xmin").OriginalValue;
            if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => TouchAsync(writer, id, ct: cancellation.Token));
            else await Should.ThrowAsync<InvalidOperationException>(() => TouchAsync(writer, id));
            fault.Executed.ShouldBeTrue();
            writer.Database.CurrentTransaction.ShouldBeSameAs(caller);
            tracked.Description.ShouldBe("pending edit");
            writer.Entry(tracked).State.ShouldBe(EntityState.Modified);
            writer.Entry(tracked).Property<uint>("xmin").OriginalValue.ShouldBe(token);
            writer.Entry(tracked).Property<uint>("xmin").CurrentValue.ShouldBe(token);
            if (caller is not null) await caller.CommitAsync(Ct);
        }
        await AssertSnapshotAsync(database, baseline);
        await using var read = database.Context();
        (await read.Workspaces.SingleAsync(Ct)).Description.ShouldBe(callerOwned ? "earlier caller write" : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledDueTouch_DoesNotMutate(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var id = await SeedAsync(database);
        var baseline = await SnapshotAsync(database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using (var writer = database.Context())
            await Should.ThrowAsync<OperationCanceledException>(() => TouchAsync(writer, id, ct: cancellation.Token));
        await AssertSnapshotAsync(database, baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentCompetingTouches_WriteOncePerInterval(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var id = await SeedAsync(database);
        var pause = new PauseGuard();
        var commands = new ObserveCommands();
        await using var first = database.Context(postgres ? [commands] : [commands, pause]);
        await using var second = database.Context(commands);
        // Independent contexts race with the same null caller snapshot. The
        // database predicate, rather than the snapshot, decides the single winner.
        var firstTouch = Task.Run(() => TouchAsync(first, id), Ct);
        if (!postgres) await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        var secondTouch = Task.Run(() => TouchAsync(second, id), Ct);
        try
        {
            if (!postgres)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
                secondTouch.IsCompleted.ShouldBeFalse();
            }
        }
        finally { pause.Release.TrySetResult(); }
        await Task.WhenAll(firstTouch, secondTouch).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        commands.AffectedRows.ShouldBe(1);
        await using var read = database.Context();
        var persisted = await read.ApiClients.SingleAsync(Ct);
        persisted.LastUsedAt.ShouldBe(Now);
        if (!postgres) read.Entry(persisted).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteCallerDeferredTransaction_ReservesWriterBeforeGuardAndRetainsOwnership(bool commit)
    {
        await using var database = await Database.CreateAsync(false);
        var id = await SeedAsync(database);
        var pause = new PauseGuard();
        await using var writer = database.Context(pause);
        await writer.Database.OpenConnectionAsync(Ct);
        await using var deferred = ((SqliteConnection)writer.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var caller = await writer.Database.UseTransactionAsync(deferred, Ct);
        var touching = Task.Run(() => TouchAsync(writer, id), Ct);
        await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await using var contender = database.Context();
        // Guard is held before the real update. A second writer must already wait,
        // proving the zero-row reservation works for a caller's deferred transaction.
        var competing = Task.Run(() => TouchAsync(contender, id), Ct);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
            competing.IsCompleted.ShouldBeFalse();
        }
        finally { pause.Release.TrySetResult(); }
        await touching.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        writer.Database.CurrentTransaction.ShouldBeSameAs(caller);
        if (commit) await deferred.CommitAsync(Ct);
        else await deferred.RollbackAsync(Ct);
        await competing.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await using var read = database.Context();
        var persisted = await read.ApiClients.SingleAsync(Ct);
        persisted.LastUsedAt.ShouldBe(Now);
        read.Entry(persisted).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceOperation_ReturnsExactAffectedRows(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var id = await SeedAsync(database);
        await using var writer = database.Context();
        (await writer.TouchApiClientLastUsedIfDueAsync(id, Now, Interval, Ct)).ShouldBe(1);
        (await writer.TouchApiClientLastUsedIfDueAsync(id, Now, Interval, Ct)).ShouldBe(0);
        (await writer.TouchApiClientLastUsedIfDueAsync(Guid.NewGuid(), Now, Interval, Ct)).ShouldBe(0);
        (await writer.TouchApiClientLastUsedIfDueAsync(id, Now.AddMinutes(-1), Interval, Ct)).ShouldBe(0);
        (await writer.TouchApiClientLastUsedIfDueAsync(id, Now.AddMinutes(5), Interval, Ct)).ShouldBe(1);
        writer.ChangeTracker.Entries().ShouldBeEmpty();
        await using var read = database.Context();
        (await read.ApiClients.SingleAsync(Ct)).LastUsedAt.ShouldBe(Now.AddMinutes(5));
    }

    private sealed class PauseGuard : DbCommandInterceptor
    {
        public TaskCompletionSource Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("row_version", StringComparison.OrdinalIgnoreCase) && !Selected.Task.IsCompleted)
            {
                Selected.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
    }

    private static async Task<Dictionary<Guid, Dictionary<string, object?>>> SnapshotAsync(Database database)
    {
        await using var read = database.Context();
        return (await read.ApiClients.IgnoreQueryFilters().ToListAsync(Ct)).ToDictionary(client => client.Id,
            client => read.Entry(client).Properties.ToDictionary(property => property.Metadata.Name, property => property.CurrentValue));
    }

    private static async Task AssertSnapshotAsync(Database database, Dictionary<Guid, Dictionary<string, object?>> expected)
    {
        var actual = await SnapshotAsync(database);
        actual.Keys.Order().ShouldBe(expected.Keys.Order());
        foreach (var (id, properties) in expected)
            foreach (var (name, value) in properties) actual[id][name].ShouldBe(value, name);
    }

    private sealed class ObserveCommands : DbCommandInterceptor, IMaterializationInterceptor
    {
        private int _affectedRows;
        public int AffectedRows => _affectedRows;
        public int Commands { get; private set; }
        public int TouchUpdates { get; private set; }
        public int Materialized { get; private set; }
        public object InitializedInstance(MaterializationInterceptionData data, object entity) { Materialized++; return entity; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            Commands++;
            if (IsTouch(command)) TouchUpdates++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Commands++; return ValueTask.FromResult(result); }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken ct = default)
        {
            if (IsTouch(command)) Interlocked.Add(ref _affectedRows, result);
            return ValueTask.FromResult(result);
        }
    }

    private static bool IsTouch(DbCommand command) => command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        && command.CommandText.Contains("last_used_at", StringComparison.OrdinalIgnoreCase);

    private sealed class FailAfterTouch(CancellationTokenSource? cancellation) : DbCommandInterceptor
    {
        public bool Executed { get; private set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken ct = default)
        {
            if (IsTouch(command))
            {
                Executed = true;
                if (cancellation is not null) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                throw new InvalidOperationException("Injected failure after database update.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static Task TouchAsync(CmsifyDbContext context, Guid id, DateTimeOffset? snapshot = null,
        DateTimeOffset? now = null, CancellationToken? ct = null) =>
        CmsifyOpaqueBearerAuthenticationHandler.TouchApiClientIfStaleAsync(context, id, snapshot, now ?? Now, Interval, ct ?? Ct);

    private static async Task<Guid> SeedAsync(Database database)
    {
        await using var context = database.Context();
        var workspace = new Workspace { Name = "workspace", Slug = "workspace" };
        var user = new User { Email = "touch@example.test", DisplayName = "editor", PasswordHash = "hash", Role = UserRole.Admin };
        var client = new ApiClient { Name = "client", Description = "untouched", TokenHash = "hash", Role = UserRole.Reader, WorkspaceId = workspace.Id, CreatedByUserId = user.Id };
        context.AddRange(workspace, user, client);
        await context.SaveChangesAsync(Ct);
        return client.Id;
    }

    private sealed class Database(string? path, PostgreSqlContainer? postgres, DbContextOptions<CmsifyDbContext> options) : IAsyncDisposable
    {
        public CmsifyDbContext Context(params IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<CmsifyDbContext>(options).AddInterceptors(interceptors).Options);

        public static async Task<Database> CreateAsync(bool usePostgres)
        {
            string? path = null;
            PostgreSqlContainer? postgres = null;
            var builder = new DbContextOptionsBuilder<CmsifyDbContext>();
            if (usePostgres)
            {
                postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("touch").WithUsername("cmsify").WithPassword("cmsify").Build();
                await postgres.StartAsync(Ct);
                builder.UseNpgsql(postgres.GetConnectionString()).UseSyntaxCircusSnakeCaseNamingConvention();
            }
            else
            {
                path = Path.Combine(Path.GetTempPath(), $"cmsify-api-touch-{Guid.NewGuid():N}.db");
                builder.UseSqlite($"Data Source={path};Pooling=False;Default Timeout=15").UseSnakeCaseNamingConvention();
            }
            var database = new Database(path, postgres, builder.Options);
            await using var setup = database.Context();
            if (usePostgres) await setup.Database.MigrateAsync(Ct);
            else await setup.Database.EnsureCreatedAsync(Ct);
            return database;
        }

        public async ValueTask DisposeAsync()
        {
            if (postgres is not null) await postgres.DisposeAsync();
            if (path is not null)
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
