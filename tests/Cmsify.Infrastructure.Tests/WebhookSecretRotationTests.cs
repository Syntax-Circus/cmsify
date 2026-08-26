using System.Data.Common;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
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

    public Task InitializeAsync() => postgres.StartAsync();

    public async Task DisposeAsync() => await postgres.DisposeAsync();

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
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondResult = await second.RotateBatchAsync(null, CancellationToken.None);
        pause.Release.TrySetResult();
        var firstResult = await firstTask;

        Assert.Equal(2, firstResult.Selected);
        Assert.Equal(2, secondResult.Selected);
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
        var worker = CreateProcessor(rotationContext, protector, batchSize: 1);
        var rotating = worker.RotateBatchAsync(null, CancellationToken.None);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var updateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = Task.Run(async () =>
        {
            updateStarted.TrySetResult();
            await updateContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE webhook_endpoints
                SET secret = {protector.Protect("new-secret")}, updated_at = CURRENT_TIMESTAMP
                WHERE id = {endpointId}
                """);
        });
        await updateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pause.Release.TrySetResult();
        await rotating;
        await update;

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

    private static WebhookSecretRotationProcessor CreateProcessor(CmsifyDbContext context, ISecretProtector protector, int batchSize) =>
        new(context, protector, Options.Create(new SecretProtectionOptions
        {
            ActiveKeyId = "key_current",
            EncryptionKey = LegacyKey,
            EncryptionKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["key_old"] = OldKey,
                ["key_current"] = CurrentKey
            },
            Rotation = new SecretRotationOptions { BatchSize = batchSize, DelaySeconds = 5 }
        }));

    private static AesSecretProtector CreateProtector() => new(Options.Create(new SecretProtectionOptions
    {
        ActiveKeyId = "key_current",
        EncryptionKey = LegacyKey,
        EncryptionKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["key_old"] = OldKey,
            ["key_current"] = CurrentKey
        }
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
            EncryptionKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["key_old"] = OldKey,
                ["key_current"] = CurrentKey
            }
        }));
        return old.Protect(secret);
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
    private const string OldKey = "ISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=";
    private const string CurrentKey = "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eX2A=";

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
}
