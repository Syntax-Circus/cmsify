using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SyntaxCircus.EntityFrameworkCore.Postgres;

namespace Cmsify.Infrastructure.Tests;

[Collection(OperationalMetricsTestGroup.Name)]
public sealed class SqliteWebhookSecretRotationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string LegacyKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
    private const string OldKey = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";
    private const string CurrentKey = "QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl8=";
    private static readonly DateTimeOffset BeforeRotation = DateTimeOffset.Parse("2020-01-01T00:00:00Z");

    [Fact]
    public async Task RotateBatch_RewrapsLegacyAndOldKeysIncludesDeletedAndRestartsAtCursor()
    {
        await using var database = await Database.CreateAsync();
        var active = Protector().Protect("active");
        await SeedAsync(database, (Legacy("legacy"), false), (Old("old"), false), (active, false), (Old("deleted"), true));
        SecretRotationBatchResult first;
        await using (var context = database.Context())
            first = await Processor(context, batchSize: 2).RotateBatchAsync(null, Ct);
        first.ShouldBe(new SecretRotationBatchResult(Id(2), 2, 2, 0, 0, false));
        // A new context/processor resumes the saved cursor; this is not a process-crash test.
        await using (var restart = database.Context())
            (await Processor(restart, batchSize: 2).RotateBatchAsync(first.NextCursor, Ct))
                .ShouldBe(new SecretRotationBatchResult(Id(4), 1, 1, 0, 0, true));
        await using var verification = database.Context();
        var endpoints = await verification.WebhookEndpoints.IgnoreQueryFilters().OrderBy(endpoint => endpoint.Id).ToListAsync(Ct);
        endpoints.Select(endpoint => Protector().Unprotect(endpoint.Secret)).ShouldBe(new[] { "legacy", "old", "active", "deleted" });
        endpoints[2].Secret.ShouldBe(active);
        foreach (var endpoint in endpoints.Where(endpoint => endpoint.Id != Id(3)))
        {
            endpoint.Secret.ShouldStartWith("v2.key_current.");
            endpoint.UpdatedAt.ShouldBeGreaterThan(BeforeRotation);
            verification.Entry(endpoint).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
        }
        verification.Entry(endpoints[2]).Property<uint>("xmin").CurrentValue.ShouldBe(1u);
        (await Processor(verification).CountRemainingAsync(Ct)).ShouldBeEmpty();
        (await Processor(verification).RotateBatchAsync(Id(4), Ct)).ShouldBe(new SecretRotationBatchResult(Id(4), 0, 0, 0, 0, true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public async Task RotateBatch_EnforcesBatchBoundsAndOrderedCursor(int batchSize)
    {
        await using var database = await Database.CreateAsync();
        await SeedAsync(database, Enumerable.Repeat((Old("secret"), false), 502).ToArray());
        await using var context = database.Context();
        var result = await Processor(context, batchSize: batchSize).RotateBatchAsync(Id(1), Ct);
        result.ShouldBe(new SecretRotationBatchResult(Id(batchSize + 1), batchSize, batchSize, 0, 0, false));
        await using var verification = database.Context();
        (await verification.WebhookEndpoints.SingleAsync(endpoint => endpoint.Id == Id(1), Ct)).Secret.ShouldStartWith("v2.key_old.");
        (await Processor(verification).CountRemainingAsync(Ct)).Single().Count.ShouldBe(502 - batchSize);
    }

    [Fact]
    public async Task RotateBatch_EmptyInvalidBoundsAndCanceledWorkLeaveDatabaseUntouched()
    {
        await using var database = await Database.CreateAsync();
        await using var context = database.Context();
        (await Processor(context).RotateBatchAsync(null, Ct)).ShouldBe(new SecretRotationBatchResult(null, 0, 0, 0, 0, true));
        foreach (var bound in new[] { 0, 501 })
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Processor(context, batchSize: bound).RotateBatchAsync(null, Ct));
        await SeedAsync(database, (Old("untouched"), false));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Processor(context).RotateBatchAsync(null, canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => Processor(context).CountRemainingAsync(canceled.Token));
        (await context.WebhookEndpoints.SingleAsync(Ct)).Secret.ShouldStartWith("v2.key_old.");
    }

    [Fact]
    public async Task RotateBatch_FailuresAdvanceCursorAndEmitOnlyBoundedDiagnostics()
    {
        await using var database = await Database.CreateAsync();
        const string unknownKey = "unconfigured-sensitive-key";
        var unknown = $"v2.{unknownKey}.AQIDBAUGBwgJCgsM.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==";
        var tampered = Old("authentication").Split('.');
        var tag = Convert.FromBase64String(tampered[3]);
        tag[0] ^= 1;
        tampered[3] = Convert.ToBase64String(tag);
        await SeedAsync(database, (unknown, false), ("v2.key_old.bad.bad.bad", false),
            ("v3.sensitive", false), (Legacy("missing legacy"), false), (string.Join('.', tampered), false), (Old("later"), false));
        var logger = new CapturingLogger();
        using var listener = new MeterListener();
        var failures = new List<(string Version, string Key, string Reason)>();
        listener.InstrumentPublished = (instrument, measurementListener) =>
        {
            if (instrument.Name == "cmsify.webhook.secret.decrypt_failures") measurementListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var labels = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString());
            failures.Add((labels["version"]!, labels["key_id"]!, labels["reason"]!));
        });
        listener.Start();
        await using var context = database.Context();
        var worker = Processor(context, batchSize: 5, includeLegacy: false, logger: logger);
        var first = await worker.RotateBatchAsync(null, Ct);
        first.ShouldBe(new SecretRotationBatchResult(Id(5), 5, 0, 0, 5, false));
        (await worker.RotateBatchAsync(first.NextCursor, Ct)).Rotated.ShouldBe(1);
        failures.Select(failure => failure.Reason).Order().ShouldBe(new[] { "authentication", "configuration", "malformed_ciphertext", "unknown_key", "unknown_version" });
        failures.ShouldAllBe(failure => failure.Version == "v1" || failure.Version == "v2" || failure.Version == "unknown");
        failures.ShouldAllBe(failure => failure.Key == "key_old" || failure.Key == "unknown");
        logger.States.Count.ShouldBe(5);
        logger.States.ShouldAllBe(state => state.Any(label => label.Key == "EndpointId") && state.Any(label => label.Key == "Reason"));
        string.Join(' ', logger.States.SelectMany(state => state).Select(label => label.Value)).ShouldNotContain(unknownKey);
        string.Join(' ', logger.States.SelectMany(state => state).Select(label => label.Value)).ShouldNotContain(unknown);
    }

    [Fact]
    public async Task CountRemaining_AggregatesOnServerWithSanitizedLabelsAndExactActivePrefixExclusion()
    {
        await using var database = await Database.CreateAsync();
        // Include case variants: SQLite LIKE is case insensitive, but existing labels/prefixes are not.
        var samples = new[] { (Legacy("legacy"), false), (Old("old"), true),
            ("v2.private-key.invalid", false), ("unknown-sensitive-ciphertext", false),
            (Protector().Protect("active"), false), ("v2.key_currentExtra.invalid", false),
            ("v2.KEY_OLD.invalid", false), ("V1.invalid", false), ("V2.key_current.invalid", false) };
        await SeedAsync(database, Enumerable.Range(0, 100).SelectMany(_ => samples).ToArray());
        var aggregate = new AggregateObserver();
        await using var context = database.Context(aggregate);
        var counts = await Processor(context).CountRemainingAsync(Ct);
        counts.ShouldBe(new[] { new SecretCiphertextCount("unknown", "unknown", 300), new("v1", "legacy", 100),
            new("v2", "key_old", 100), new("v2", "unknown", 300) });
        aggregate.Commands.Count.ShouldBe(1);
        aggregate.Commands.Single().ShouldContain("GROUP BY", Case.Insensitive);
        aggregate.Columns.ShouldBe(3);
        aggregate.ColumnNames.ShouldNotContain("secret");
    }

    [Fact]
    public async Task RotateBatch_CompetingIndependentConnectionsWaitForWriterThenSelectDisjointRows()
    {
        await using var database = await Database.CreateAsync();
        await SeedAsync(database, Enumerable.Repeat((Old("secret"), false), 4).ToArray());
        var pause = new PauseSelection();
        var attempted = new ObserveTransactionStart();
        await using var firstContext = database.Context(pause);
        await using var secondContext = database.Context(attempted);
        firstContext.Database.GetDbConnection().ShouldNotBeSameAs(secondContext.Database.GetDbConnection());
        var first = Task.Run(() => Processor(firstContext, batchSize: 2).RotateBatchAsync(null, Ct), Ct);
        Task<SecretRotationBatchResult>? second = null;
        try
        {
            await pause.Selected.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            pause.Transaction.ShouldNotBeNull();
            second = Task.Run(() => Processor(secondContext, batchSize: 2).RotateBatchAsync(null, Ct), Ct);
            await attempted.Starting.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            await Task.Delay(250, Ct);
            second.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            pause.Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        (await first).ShouldBe(new SecretRotationBatchResult(Id(2), 2, 2, 0, 0, false));
        (await second!).ShouldBe(new SecretRotationBatchResult(Id(4), 2, 2, 0, 0, false));
        await using var verification = database.Context();
        (await Processor(verification).CountRemainingAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RotateBatch_OriginalSecretPredicatePreservesReplacementAndReportsSkipped()
    {
        await using var database = await Database.CreateAsync();
        await SeedAsync(database, (Old("original"), false));
        await using var context = database.Context();
        var replacement = Protector().Protect("replacement");
        var protector = new BeforeProtect(Protector(), () => context.Database.ExecuteSqlInterpolated($"UPDATE webhook_endpoints SET secret = {replacement} WHERE id = {Id(1)}"));
        (await Processor(context, protector: protector).RotateBatchAsync(null, Ct)).ShouldBe(new SecretRotationBatchResult(Id(1), 1, 0, 1, 0, true));
        await using var verification = database.Context();
        (await verification.WebhookEndpoints.SingleAsync(Ct)).Secret.ShouldBe(replacement);
        verification.Entry(await verification.WebhookEndpoints.SingleAsync(Ct)).Property<uint>("xmin").CurrentValue.ShouldBe(1u);
    }

    [Fact]
    public async Task RotateBatch_FencesStaleTrackedSaveWithoutOverwritingRotatedSecret()
    {
        await using var database = await Database.CreateAsync();
        await SeedAsync(database, (Old("original"), false));
        await using var stale = database.Context();
        var endpoint = await stale.WebhookEndpoints.SingleAsync(Ct);
        await using (var rotation = database.Context())
            (await Processor(rotation).RotateBatchAsync(null, Ct)).Rotated.ShouldBe(1);
        endpoint.Name = "stale name";
        endpoint.Secret = Old("stale secret");
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Ct));
        await using var verification = database.Context();
        var saved = await verification.WebhookEndpoints.SingleAsync(Ct);
        saved.Name.ShouldBe("endpoint-1");
        Protector().Unprotect(saved.Secret).ShouldBe("original");
        verification.Entry(saved).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
    }

    [Fact]
    public async Task RotateBatch_ExhaustedRevisionRejectsWholeBatchWithoutPartialWrites()
    {
        await using var database = await Database.CreateAsync();
        await SeedAsync(database, (Old("first"), false), (Old("exhausted"), false));
        await using (var setup = database.Context())
            await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_endpoints SET row_version = {(long)uint.MaxValue} WHERE id = {Id(2)}", Ct);
        await using (var rotation = database.Context())
            await Should.ThrowAsync<OverflowException>(() => Processor(rotation).RotateBatchAsync(null, Ct));
        await using var verification = database.Context();
        var saved = await verification.WebhookEndpoints.OrderBy(endpoint => endpoint.Id).ToListAsync(Ct);
        saved.ShouldAllBe(endpoint => endpoint.Secret.StartsWith("v2.key_old."));
        saved.ShouldAllBe(endpoint => endpoint.UpdatedAt == BeforeRotation);
        verification.Entry(saved[0]).Property<uint>("xmin").CurrentValue.ShouldBe(1u);
        verification.Entry(saved[1]).Property<uint>("xmin").CurrentValue.ShouldBe(uint.MaxValue);
    }

    [Fact]
    public async Task RotateBatch_LastRevisionAdvancesWithoutWrappingAndActiveExhaustedRowsAreExcluded()
    {
        await using var database = await Database.CreateAsync();
        await SeedAsync(database, (Old("last revision"), false), (Protector().Protect("active"), false));
        await using (var setup = database.Context())
        {
            await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_endpoints SET row_version = {(long)uint.MaxValue - 1} WHERE id = {Id(1)}", Ct);
            await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_endpoints SET row_version = {(long)uint.MaxValue} WHERE id = {Id(2)}", Ct);
        }
        await using (var rotation = database.Context())
            (await Processor(rotation).RotateBatchAsync(null, Ct)).Rotated.ShouldBe(1);
        await using var verification = database.Context();
        var saved = await verification.WebhookEndpoints.OrderBy(endpoint => endpoint.Id).ToListAsync(Ct);
        saved.Select(endpoint => verification.Entry(endpoint).Property<uint>("xmin").CurrentValue).ShouldBe(new[] { uint.MaxValue, uint.MaxValue });
        Protector().Unprotect(saved[0].Secret).ShouldBe("last revision");
    }

    [Fact]
    public async Task RotateBatch_DatabaseUpdateFailureRollsBackWholeBatch()
    {
        await using var database = await Database.CreateAsync();
        await SeedAsync(database, (Old("first"), false), (Old("second"), false));
        await using (var setup = database.Context())
            await setup.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_second BEFORE UPDATE OF secret ON webhook_endpoints WHEN OLD.name = 'endpoint-2' BEGIN SELECT RAISE(ABORT, 'synthetic update failure'); END", Ct);
        await using (var rotation = database.Context())
        {
            var exception = await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(() => Processor(rotation).RotateBatchAsync(null, Ct));
            exception.Message.ShouldContain("synthetic update failure");
        }
        await using var verification = database.Context();
        var saved = await verification.WebhookEndpoints.ToListAsync(Ct);
        saved.ShouldAllBe(endpoint => endpoint.Secret.StartsWith("v2.key_old.") && endpoint.UpdatedAt == BeforeRotation);
        saved.ShouldAllBe(endpoint => verification.Entry(endpoint).Property<uint>("xmin").CurrentValue == 1u);
    }

    private static Guid Id(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:D12}");
    private static SecretProtectionOptions OptionsFor(string active = "key_current", int batch = 100, bool includeLegacy = true) => new()
    {
        ActiveKeyId = active, EncryptionKey = includeLegacy ? LegacyKey : null,
        EncryptionKeys = new(StringComparer.Ordinal) { ["key_old"] = OldKey, ["key_current"] = CurrentKey },
        Rotation = new() { BatchSize = batch }
    };
    private static AesSecretProtector Protector(string active = "key_current", bool includeLegacy = true) => new(Options.Create(OptionsFor(active, includeLegacy: includeLegacy)));
    private static string Old(string value) => Protector("key_old").Protect(value);
    private static WebhookSecretRotationProcessor Processor(CmsifyDbContext context, int batchSize = 100, bool includeLegacy = true,
        ILogger<WebhookSecretRotationProcessor>? logger = null, ISecretProtector? protector = null) =>
        new(context, protector ?? Protector(includeLegacy: includeLegacy), Options.Create(OptionsFor(batch: batchSize, includeLegacy: includeLegacy)), logger ?? NullLogger<WebhookSecretRotationProcessor>.Instance);

    private static string Legacy(string value)
    {
        var nonce = Convert.FromBase64String("AQIDBAUGBwgJCgsM");
        var plaintext = Encoding.UTF8.GetBytes(value);
        var encrypted = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(LegacyKey), tag.Length);
        aes.Encrypt(nonce, plaintext, encrypted, tag);
        return $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(encrypted)}";
    }

    private static async Task SeedAsync(Database database, params (string Secret, bool Deleted)[] values)
    {
        await using var context = database.Context();
        var workspace = new Workspace { Name = "Rotation", Slug = "rotation" };
        var user = new User { Email = "rotation@example.test", DisplayName = "Rotation", PasswordHash = "synthetic", Role = UserRole.Admin };
        context.AddRange(workspace, user);
        context.AddRange(values.Select((value, index) => new WebhookEndpoint
        {
            Id = Id(index + 1), WorkspaceId = workspace.Id, CreatedByUserId = user.Id,
            Name = $"endpoint-{index + 1}", Url = "https://example.test/hook", Secret = value.Secret,
            IsDeleted = value.Deleted, DeletedAt = value.Deleted ? BeforeRotation : null, UpdatedAt = BeforeRotation
        }));
        await context.SaveChangesAsync(Ct);
    }

    private sealed class Database(string path) : IAsyncDisposable
    {
        public static async Task<Database> CreateAsync()
        {
            var database = new Database(Path.Combine(Path.GetTempPath(), $"cmsify-rotation-{Guid.NewGuid():N}.db"));
            await using var context = database.Context();
            await context.Database.EnsureCreatedAsync(Ct);
            return database;
        }
        public CmsifyDbContext Context(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Default Timeout=15").UseSyntaxCircusSnakeCaseNamingConvention().AddInterceptors(interceptors).Options);
        public async ValueTask DisposeAsync()
        {
            await using var context = Context();
            await context.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    private sealed class BeforeProtect(ISecretProtector inner, Action action) : ISecretProtector
    {
        public string Protect(string secret) { action(); return inner.Protect(secret); }
        public string Unprotect(string secret) => inner.Unprotect(secret);
    }
    private sealed class CapturingLogger : ILogger<WebhookSecretRotationProcessor>
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> States { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            States.Add(state as IReadOnlyList<KeyValuePair<string, object?>> ?? []);
    }
    private sealed class AggregateObserver : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public int Columns { get; private set; }
        public string[] ColumnNames { get; private set; } = [];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            Columns = result.FieldCount;
            ColumnNames = Enumerable.Range(0, Columns).Select(result.GetName).ToArray();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class PauseSelection : DbCommandInterceptor
    {
        private int _paused;
        public TaskCompletionSource Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DbTransaction? Transaction { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("webhook_endpoints", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Transaction = command.Transaction;
                Selected.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
    private sealed class ObserveTransactionStart : DbTransactionInterceptor
    {
        public TaskCompletionSource Starting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Starting.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
