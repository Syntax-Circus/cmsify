using System.Data.Common;
using System.Diagnostics.Metrics;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests;

public sealed class WebhookSecretRotationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public ValueTask InitializeAsync() => new(postgres.StartAsync());

    public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task RotateBatch_IsBoundedIncludesSoftDeletedAndLeavesActiveCiphertextUntouched()
    {
        await using var setup = await CreateContextAsync();
        var protector = CreateProtector();
        var endpoints = await SeedEndpointsAsync(
            setup,
            ("legacy", CreateLegacyCiphertext("legacy"), false),
            ("old", CreateOldCiphertext("old"), false),
            ("active", protector.Protect("active"), false),
            ("deleted", CreateOldCiphertext("deleted"), true));
        var activeBefore = endpoints.Single(endpoint => endpoint.Name == "active").Secret;

        await using var workerContext = await CreateContextAsync();
        var worker = CreateProcessor(workerContext, protector, batchSize: 2);

        var first = await worker.RotateBatchAsync(null, CancellationToken.None);
        var second = await worker.RotateBatchAsync(first.NextCursor, CancellationToken.None);

        Assert.Equal(2, first.Selected);
        Assert.Equal(2, first.Rotated);
        Assert.NotNull(first.NextCursor);
        Assert.False(first.ReachedEnd);
        Assert.Equal(1, second.Selected);
        Assert.True(second.ReachedEnd);
        await using var verification = await CreateContextAsync();
        Assert.Equal(activeBefore, await verification.WebhookEndpoints.Where(endpoint => endpoint.Name == "active").Select(endpoint => endpoint.Secret).SingleAsync());
        Assert.True(await verification.WebhookEndpoints.IgnoreQueryFilters().Where(endpoint => endpoint.Name == "deleted").Select(endpoint => endpoint.Secret).SingleAsync() is { } deleted && deleted.StartsWith("v2.key_current.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RotateBatch_RepeatedPassesArePlaintextStableAndReachZeroOldRows()
    {
        await using var setup = await CreateContextAsync();
        var protector = CreateProtector();
        await SeedEndpointsAsync(
            setup,
            ("legacy", CreateLegacyCiphertext("legacy"), false),
            ("old", CreateOldCiphertext("old"), false),
            ("active", protector.Protect("active"), false),
            ("deleted", CreateOldCiphertext("deleted"), true));

        await using var workerContext = await CreateContextAsync();
        var worker = CreateProcessor(workerContext, protector, batchSize: 2);
        Guid? cursor = null;
        SecretRotationBatchResult result;
        do
        {
            result = await worker.RotateBatchAsync(cursor, CancellationToken.None);
            cursor = result.NextCursor;
        }
        while (!result.ReachedEnd);

        Assert.Equal(0, (await worker.CountRemainingAsync(CancellationToken.None)).Sum(count => count.Count));
        var finalPass = await worker.RotateBatchAsync(null, CancellationToken.None);
        Assert.Equal(0, finalPass.Selected);
        Assert.True(finalPass.ReachedEnd);

        await using var verification = await CreateContextAsync();
        var secrets = await verification.WebhookEndpoints.IgnoreQueryFilters().OrderBy(endpoint => endpoint.Name).Select(endpoint => endpoint.Secret).ToListAsync();
        Assert.Equal(["active", "deleted", "legacy", "old"], secrets.Select(protector.Unprotect).OrderBy(value => value).ToArray());
    }

    [Fact]
    public async Task RotateBatch_RewrapsMaximumLegacyCiphertextWith64CharacterActiveKeyAndDoesNotStarveLaterRows()
    {
        var activeKeyId = new string('a', 64);
        var protector = CreateProtector(activeKeyId);
        var maximumLegacyPlaintext = new string('x', 714);
        var maximumLegacyCiphertext = CreateLegacyCiphertext(maximumLegacyPlaintext);
        Assert.Equal(997, maximumLegacyCiphertext.Length);
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await using (var setup = await CreateContextAsync())
        {
            await SeedEndpointsAsync(
                setup,
                ("maximum", maximumLegacyCiphertext, false, firstId),
                ("later", CreateLegacyCiphertext("later"), false, secondId));
        }

        await using var workerContext = await CreateContextAsync();
        var worker = CreateProcessor(workerContext, protector, batchSize: 1, activeKeyId: activeKeyId);
        var first = await worker.RotateBatchAsync(null, CancellationToken.None);
        var second = await worker.RotateBatchAsync(first.NextCursor, CancellationToken.None);

        Assert.Equal(1, first.Rotated);
        Assert.Equal(1, second.Rotated);
        await using var verification = await CreateContextAsync();
        var values = await verification.WebhookEndpoints.OrderBy(endpoint => endpoint.Id).Select(endpoint => endpoint.Secret).ToListAsync();
        Assert.Equal([maximumLegacyPlaintext, "later"], values.Select(protector.Unprotect).ToArray());
    }

    [Fact]
    public async Task ConcurrentProcessors_ClaimDisjointLockedRows()
    {
        var pause = new PauseAfterRotationSelectionInterceptor();
        await using (var setup = await CreateContextAsync())
        {
            var protector = CreateProtector();
            await SeedEndpointsAsync(
                setup,
                ("one", CreateOldCiphertext("one"), false),
                ("two", CreateOldCiphertext("two"), false),
                ("three", CreateOldCiphertext("three"), false),
                ("four", CreateOldCiphertext("four"), false));
        }

        var protectorForWorkers = CreateProtector();
        await using var firstContext = await CreateContextAsync(pause);
        await using var secondContext = await CreateContextAsync();
        var first = CreateProcessor(firstContext, protectorForWorkers, batchSize: 2);
        var second = CreateProcessor(secondContext, protectorForWorkers, batchSize: 2);
        var firstTask = first.RotateBatchAsync(null, CancellationToken.None);
        SecretRotationBatchResult? firstResult = null;
        SecretRotationBatchResult? secondResult = null;
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondResult = await second.RotateBatchAsync(null, CancellationToken.None);
        }
        finally
        {
            pause.Release.TrySetResult();
            firstResult = await firstTask;
        }

        Assert.Equal(2, firstResult!.Selected);
        Assert.Equal(2, secondResult!.Selected);
        Assert.Equal(4, firstResult.Rotated + secondResult.Rotated);
    }

    [Fact]
    public async Task ConcurrentSigningSecretUpdate_PreservesTheNewPlaintext()
    {
        var pause = new PauseAfterRotationSelectionInterceptor();
        var protector = CreateProtector();
        Guid endpointId;
        await using (var setup = await CreateContextAsync())
        {
            endpointId = (await SeedEndpointsAsync(setup, ("race", CreateOldCiphertext("old-secret"), false))).Single().Id;
        }

        await using var rotationContext = await CreateContextAsync(pause);
        await using var updateContext = await CreateContextAsync();
        await using var observerContext = await CreateContextAsync();
        var worker = CreateProcessor(rotationContext, protector, batchSize: 1);
        var rotating = worker.RotateBatchAsync(null, CancellationToken.None);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task update = Task.CompletedTask;
        Exception? primaryFailure = null;
        SecretRotationBatchResult? rotation = null;
        try
        {
            await updateContext.Database.OpenConnectionAsync();
            var updaterBackendPid = await updateContext.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            update = Task.Run(async () =>
            {
                await updateContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE webhook_endpoints
                    SET secret = {protector.Protect("new-secret")}, updated_at = CURRENT_TIMESTAMP
                    WHERE id = {endpointId}
                    """);
            });
            await WaitForLockWaitAsync(observerContext, updaterBackendPid, update);
            Assert.False(update.IsCompleted);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            rotation = await ReleaseAndObserveAsync(pause, rotating, update, primaryFailure);
        }

        Assert.Equal(new SecretRotationBatchResult(endpointId, 1, 1, 0, 0, false), rotation);

        await using var verification = await CreateContextAsync();
        var ciphertext = await verification.WebhookEndpoints.Where(endpoint => endpoint.Id == endpointId).Select(endpoint => endpoint.Secret).SingleAsync();
        Assert.Equal("new-secret", protector.Unprotect(ciphertext));
    }

    [Fact]
    public async Task UndecryptableLowId_AdvancesCursorAndDoesNotStarveLaterRows()
    {
        var protector = CreateProtector();
        var invalidId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var validId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await using (var setup = await CreateContextAsync())
        {
            await SeedEndpointsAsync(
                setup,
                ("invalid", "v2.removed_key.AQIDBAUGBwgJCgsM.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==", false, invalidId),
                ("valid", CreateOldCiphertext("valid-secret"), false, validId));
        }

        await using var workerContext = await CreateContextAsync();
        var worker = CreateProcessor(workerContext, protector, batchSize: 1);
        var failed = await worker.RotateBatchAsync(null, CancellationToken.None);
        var later = await worker.RotateBatchAsync(failed.NextCursor, CancellationToken.None);

        Assert.Equal(invalidId, failed.NextCursor);
        Assert.Equal(1, failed.Selected);
        Assert.Equal(1, failed.Failed);
        Assert.Equal(validId, later.NextCursor);
        Assert.Equal(1, later.Rotated);
        await using var verification = await CreateContextAsync();
        Assert.Equal("valid-secret", protector.Unprotect(await verification.WebhookEndpoints.Where(endpoint => endpoint.Id == validId).Select(endpoint => endpoint.Secret).SingleAsync()));
    }

    [Fact]
    public async Task RotateBatch_WhenASecretCannotBeDecrypted_LogsEndpointAndBoundedDiagnosticWithoutCiphertext()
    {
        const string unconfiguredKeyId = "unconfigured-sensitive-key";
        var endpointId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var ciphertext = $"v2.{unconfiguredKeyId}.AQIDBAUGBwgJCgsM.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==";
        await using (var setup = await CreateContextAsync())
        {
            await SeedEndpointsAsync(setup, ("invalid", ciphertext, false, endpointId));
        }

        var logger = new CapturingRotationLogger();
        await using var workerContext = await CreateContextAsync();
        var worker = new WebhookSecretRotationProcessor(
            workerContext,
            CreateProtector(),
            Options.Create(new SecretProtectionOptions
            {
                ActiveKeyId = "key_current",
                EncryptionKey = LegacyKey,
                EncryptionKeys = CreateEncryptionKeys("key_current"),
                Rotation = new SecretRotationOptions { BatchSize = 1, DelaySeconds = 5 }
            }),
            logger);

        var result = await worker.RotateBatchAsync(null, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        var state = Assert.Single(logger.States);
        Assert.Contains(state, item => item.Key == "EndpointId" && Equals(item.Value, endpointId));
        Assert.Contains(state, item => item.Key == "Version" && Equals(item.Value, "v2"));
        Assert.Contains(state, item => item.Key == "KeyId" && Equals(item.Value, "unknown"));
        Assert.Contains(state, item => item.Key == "Reason" && Equals(item.Value, "unknown_key"));
        Assert.DoesNotContain(state, item => item.Value?.ToString()?.Contains(unconfiguredKeyId, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(state, item => item.Value?.ToString()?.Contains(ciphertext, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task CountRemaining_GroupsConfiguredKeysAndBoundsLegacyAndUnknownVersions()
    {
        var protector = CreateProtector();
        await using (var setup = await CreateContextAsync())
        {
            await SeedEndpointsAsync(
                setup,
                ("legacy", CreateLegacyCiphertext("legacy"), false),
                ("old", CreateOldCiphertext("old"), false),
                ("unknown", "v2.unconfigured.AQIDBAUGBwgJCgsM.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==", false),
                ("malformed", "not-a-webhook-secret", false),
                ("active", protector.Protect("active"), false));
        }

        await using var workerContext = await CreateContextAsync();
        var counts = await CreateProcessor(workerContext, protector, batchSize: 2).CountRemainingAsync(CancellationToken.None);

        Assert.Equal(1, counts.Single(count => count is { Version: "v1", KeyId: "legacy" }).Count);
        Assert.Equal(1, counts.Single(count => count is { Version: "v2", KeyId: "key_old" }).Count);
        Assert.Equal(1, counts.Single(count => count is { Version: "v2", KeyId: "unknown" }).Count);
        Assert.Equal(1, counts.Single(count => count is { Version: "unknown", KeyId: "unknown" }).Count);
        Assert.DoesNotContain(counts, count => count.KeyId == "unconfigured");
        Assert.DoesNotContain(counts, count => count.KeyId == "key_current");
    }

    [Fact]
    public async Task RotateBatch_ReportsTypedBoundedDecryptFailureReasons()
    {
        var missingLegacyProtector = CreateProtector(includeLegacyKey: false);
        await using (var setup = await CreateContextAsync())
        {
            var authenticatedCiphertext = CreateOldCiphertext("authenticated");
            var parts = authenticatedCiphertext.Split('.', StringSplitOptions.None);
            var tag = Convert.FromBase64String(parts[3]);
            tag[0] ^= 1;
            parts[3] = Convert.ToBase64String(tag);
            await SeedEndpointsAsync(
                setup,
                ("unknown-version", "v3.key_old.AQIDBAUGBwgJCgsM.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==", false),
                ("unknown-key", "v2.removed_key.AQIDBAUGBwgJCgsM.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==", false),
                ("configuration", "v1.AQIDBAUGBwgJCgsM.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==", false),
                ("malformed", "v2.key_old.bad.AQIDBAUGBwgJCgsMDQ4PEA==.AQ==", false),
                ("authentication", string.Join('.', parts), false));
        }

        using var listener = new MeterListener();
        var failures = new List<(string Version, string KeyId, string Reason)>();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CmsifyOperationalMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "cmsify.webhook.secret.decrypt_failures")
            {
                var version = string.Empty;
                var keyId = string.Empty;
                var reason = string.Empty;
                foreach (var tag in tags)
                {
                    switch (tag.Key)
                    {
                        case "version": version = tag.Value?.ToString() ?? string.Empty; break;
                        case "key_id": keyId = tag.Value?.ToString() ?? string.Empty; break;
                        case "reason": reason = tag.Value?.ToString() ?? string.Empty; break;
                    }
                }

                failures.Add((version, keyId, reason));
            }
        });
        listener.Start();

        await using var workerContext = await CreateContextAsync();
        var result = await CreateProcessor(workerContext, missingLegacyProtector, batchSize: 5, includeLegacyKey: false).RotateBatchAsync(null, CancellationToken.None);

        Assert.Equal(5, result.Failed);
        Assert.Equal(
            ["authentication", "configuration", "malformed_ciphertext", "unknown_key", "unknown_version"],
            failures.Select(failure => failure.Reason).OrderBy(reason => reason).ToArray());
        Assert.All(failures, failure => Assert.True(failure.Version is "v1" or "v2" or "unknown"));
        Assert.All(failures, failure => Assert.True(failure.KeyId is "key_old" or "unknown"));
        Assert.DoesNotContain(failures, failure => failure.KeyId == "removed_key");
    }

    private async Task<CmsifyDbContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .UseSyntaxCircusSnakeCaseNamingConvention();
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        var context = new CmsifyDbContext(builder.Options);
        await context.Database.MigrateAsync();
        return context;
    }

    private static WebhookSecretRotationProcessor CreateProcessor(CmsifyDbContext context, ISecretProtector protector, int batchSize, bool includeLegacyKey = true, string activeKeyId = "key_current") =>
        new(context, protector, Options.Create(new SecretProtectionOptions
        {
            ActiveKeyId = activeKeyId,
            EncryptionKey = includeLegacyKey ? LegacyKey : null,
            EncryptionKeys = CreateEncryptionKeys(activeKeyId),
            Rotation = new SecretRotationOptions { BatchSize = batchSize, DelaySeconds = 5 }
        }), NullLogger<WebhookSecretRotationProcessor>.Instance);

    private static AesSecretProtector CreateProtector(bool includeLegacyKey = true) => CreateProtector("key_current", includeLegacyKey);

    private static AesSecretProtector CreateProtector(string activeKeyId, bool includeLegacyKey = true) => new(Options.Create(new SecretProtectionOptions
    {
        ActiveKeyId = activeKeyId,
        EncryptionKey = includeLegacyKey ? LegacyKey : null,
        EncryptionKeys = CreateEncryptionKeys(activeKeyId)
    }));

    private static async Task<IReadOnlyList<WebhookEndpoint>> SeedEndpointsAsync(
        CmsifyDbContext context,
        params (string Name, string Secret, bool IsDeleted, Guid? Id)[] endpoints)
    {
        var workspace = new Workspace { Name = $"rotation-{Guid.NewGuid():N}", Slug = $"rotation-{Guid.NewGuid():N}" };
        var user = new User { Email = $"rotation-{Guid.NewGuid():N}@example.test", DisplayName = "Rotation", PasswordHash = "hash", Role = UserRole.Admin };
        var entities = endpoints.Select((endpoint, index) => new WebhookEndpoint
        {
            Id = endpoint.Id ?? Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            Name = endpoint.Name,
            Url = $"https://example.test/{index}",
            Secret = endpoint.Secret,
            CreatedByUserId = user.Id,
            IsDeleted = endpoint.IsDeleted,
            DeletedAt = endpoint.IsDeleted ? DateTimeOffset.UtcNow : null
        }).ToArray();
        context.AddRange(workspace, user);
        context.WebhookEndpoints.AddRange(entities);
        await context.SaveChangesAsync();
        return entities;
    }

    private static Task<IReadOnlyList<WebhookEndpoint>> SeedEndpointsAsync(
        CmsifyDbContext context,
        params (string Name, string Secret, bool IsDeleted)[] endpoints) =>
        SeedEndpointsAsync(context, endpoints.Select(endpoint => (endpoint.Name, endpoint.Secret, endpoint.IsDeleted, (Guid?)null)).ToArray());

    private static string CreateOldCiphertext(string secret)
    {
        var old = new AesSecretProtector(Options.Create(new SecretProtectionOptions
        {
            ActiveKeyId = "key_old",
            EncryptionKey = LegacyKey,
            EncryptionKeys = CreateEncryptionKeys("key_old")
        }));
        return old.Protect(secret);
    }

    private static async Task WaitForLockWaitAsync(CmsifyDbContext observerContext, int backendPid, Task updater)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var isWaitingOnLock = await observerContext.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE pid = {backendPid} AND state = 'active' AND wait_event_type = 'Lock'
                ) AS "Value"
                """).SingleAsync(timeout.Token);
            if (isWaitingOnLock)
            {
                return;
            }

            if (updater.IsCompleted)
            {
                throw new Xunit.Sdk.XunitException("The signing-secret update completed before it waited on the rotation row lock.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }

        throw new TimeoutException("The signing-secret update did not reach a PostgreSQL row-lock wait.");
    }

    private static async Task<SecretRotationBatchResult?> ReleaseAndObserveAsync(
        PauseAfterRotationSelectionInterceptor pause,
        Task<SecretRotationBatchResult> rotating,
        Task updater,
        Exception? primaryFailure)
    {
        pause.Release.TrySetResult();
        SecretRotationBatchResult? rotation = null;
        Exception? rotationFailure = null;
        Exception? updateFailure = null;
        try
        {
            rotation = await rotating;
        }
        catch (Exception exception)
        {
            rotationFailure = exception;
        }

        try
        {
            await updater;
        }
        catch (Exception exception)
        {
            updateFailure = exception;
        }

        if (primaryFailure is not null)
        {
            return rotation;
        }

        if (rotationFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(rotationFailure).Throw();
        }

        if (updateFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(updateFailure).Throw();
        }

        return rotation;
    }

    private static string CreateLegacyCiphertext(string secret)
    {
        var nonce = Convert.FromBase64String("AQIDBAUGBwgJCgsM");
        var plaintext = System.Text.Encoding.UTF8.GetBytes(secret);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new System.Security.Cryptography.AesGcm(Convert.FromBase64String(LegacyKey), tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(ciphertext)}";
    }

    private const string LegacyKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
    private static readonly string OldKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private static readonly string CurrentKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static Dictionary<string, string> CreateEncryptionKeys(string activeKeyId)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key_old"] = OldKey,
            ["key_current"] = CurrentKey
        };
        if (!keys.ContainsKey(activeKeyId)) keys.Add(activeKeyId, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        return keys;
    }

    private sealed class PauseAfterRotationSelectionInterceptor : DbCommandInterceptor
    {
        private int paused;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM webhook_endpoints", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FOR UPDATE SKIP LOCKED", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref paused, 1) == 0)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class CapturingRotationLogger : ILogger<WebhookSecretRotationProcessor>
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> States { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            States.Add(state as IReadOnlyList<KeyValuePair<string, object?>> ?? []);
        }
    }
}
