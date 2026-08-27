using System.Collections.Concurrent;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using SyntaxCircus.Storage;

namespace Cmsify.Infrastructure.Tests;

[Collection(MediaPostgresTestGroup.Name)]
public sealed class MediaReconciliationServiceIntegrationTests(MediaPostgresFixture fixture)
{
    [Fact]
    public async Task AbandonedUploadCleanup_SurvivesHostRestart()
    {
        var now = DateTimeOffset.Parse("2026-08-27T21:00:00Z");
        var connectionString = await CreateDatabaseAsync();
        var storage = new RecordingStorageProvider();
        Guid assetId;
        string storageKey;

        await using (var firstHost = BuildServices(connectionString, storage))
        {
            await using var scope = firstHost.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var workspace = new Workspace { Name = "Restart media", Slug = $"restart-media-{Guid.NewGuid():N}" };
            var asset = new MediaAsset
            {
                WorkspaceId = workspace.Id,
                FileName = "abandoned.txt",
                MimeType = "text/plain",
                StorageKey = $"cmsify/media/{workspace.Id}/abandoned.txt",
                StorageProvider = "local",
                BlobState = MediaBlobState.PendingUpload,
                BlobStateChangedAt = now.AddMinutes(-30)
            };
            context.AddRange(workspace, asset);
            await context.SaveChangesAsync();
            assetId = asset.Id;
            storageKey = asset.StorageKey;
            var repository = scope.ServiceProvider.GetRequiredService<IMediaReconciliationRepository>();
            (await repository.FailStaleUploadsAsync(now.AddMinutes(-30), now, 100)).ShouldBe(1);
        }

        await using (var restartedHost = BuildServices(connectionString, storage))
        {
            await CreateService(restartedHost, now, "worker-after-restart").RunOnceAsync();
            await using var scope = restartedHost.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var asset = await context.MediaAssets.IgnoreQueryFilters().SingleAsync(item => item.Id == assetId);
            var intent = await context.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == assetId);
            asset.BlobState.ShouldBe(MediaBlobState.Deleted);
            intent.CompletedAt.ShouldBe(now);
        }

        storage.DeleteCount(storageKey).ShouldBe(1);
    }

    [Fact]
    public async Task TwoReplicaServices_DeleteOneDurableIntentExactlyOnce()
    {
        var now = DateTimeOffset.Parse("2026-08-27T21:30:00Z");
        var connectionString = await CreateDatabaseAsync();
        var storage = new RecordingStorageProvider();
        var storageKey = $"cmsify/media/{Guid.NewGuid():N}";
        Guid intentId;
        await using (var setup = CreateContext(connectionString))
        {
            var intent = new MediaDeletionIntent
            {
                Provider = "local",
                StorageKey = storageKey,
                Reason = "orphan",
                NotBefore = now,
                NextAttemptAt = now,
                CreatedAt = now
            };
            setup.MediaDeletionIntents.Add(intent);
            await setup.SaveChangesAsync();
            intentId = intent.Id;
        }

        await using var services = BuildServices(connectionString, storage);
        var first = CreateService(services, now, "replica-a");
        var second = CreateService(services, now, "replica-b");

        await Task.WhenAll(first.RunOnceAsync(), second.RunOnceAsync());

        storage.DeleteCount(storageKey).ShouldBe(1);
        await using var verification = CreateContext(connectionString);
        (await verification.MediaDeletionIntents.SingleAsync(item => item.Id == intentId)).CompletedAt.ShouldBe(now);
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var database = $"media_service_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync();
        return connectionString;
    }

    private static ServiceProvider BuildServices(string connectionString, IStorageProvider storage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CmsifyDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSyntaxCircusSnakeCaseNamingConvention());
        services.AddScoped<IMediaReconciliationRepository, MediaReconciliationRepository>();
        services.AddSingleton(storage);
        services.AddSingleton<IStorageProvider>(storage);
        return services.BuildServiceProvider();
    }

    private static MediaReconciliationService CreateService(
        ServiceProvider services,
        DateTimeOffset now,
        string workerId)
    {
        var options = Options.Create(new MediaOperationalOptions());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "local"
        }).Build();
        return new MediaReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            configuration,
            services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<ILogger<MediaReconciliationService>>(),
            new FixedTimeProvider(now),
            workerId);
    }

    private static CmsifyDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(connectionString)
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingStorageProvider : IStorageProvider
    {
        private readonly ConcurrentDictionary<string, int> deleteCounts = new(StringComparer.Ordinal);

        public int DeleteCount(string key) => deleteCounts.GetValueOrDefault(key);

        public Task<StoredObject> StoreAsync(StoreObjectRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredObject(request.Key, request.Content.CanSeek ? request.Content.Length : 0));

        public Task<StorageReadResult?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<StorageReadResult?>(null);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            deleteCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
            return Task.CompletedTask;
        }

        public Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<StorageObjectMetadata?>(null);

        public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageObjectPage([], null));
    }
}
