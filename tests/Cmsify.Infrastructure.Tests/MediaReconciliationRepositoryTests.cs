using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using SyntaxCircus.Storage;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaPostgresTestGroup : ICollectionFixture<MediaPostgresFixture>
{
    public const string Name = "Media reconciliation PostgreSQL";
}

public sealed class MediaPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify_media")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public string ConnectionString => postgres.GetConnectionString();

    public async Task InitializeAsync() => await postgres.StartAsync();
    public async Task DisposeAsync() => await postgres.DisposeAsync();
}

[Collection(MediaPostgresTestGroup.Name)]
public sealed class MediaReconciliationRepositoryTests(MediaPostgresFixture fixture)
{
    [Fact]
    public async Task Migration_BackfillsActiveAndDeletedAssetsWithFreshRetentionIntent()
    {
        var database = $"media_upgrade_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await using var context = new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(connection)
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260826215147_ExpandWebhookSecretCiphertext");
        var now = DateTimeOffset.UtcNow;
        var workspaceId = Guid.CreateVersion7();
        var activeId = Guid.CreateVersion7();
        var deletedId = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workspaces (id, name, slug, created_at, updated_at, is_deleted)
            VALUES ({workspaceId}, 'Migration media', {database}, {now}, {now}, false);
            INSERT INTO media_assets
                (id, workspace_id, file_name, mime_type, size_bytes, storage_key, storage_provider, created_at, updated_at, is_deleted)
            VALUES ({activeId}, {workspaceId}, 'active.png', 'image/png', 1, 'default/active.png', 'local', {now}, {now}, false);
            INSERT INTO media_assets
                (id, workspace_id, file_name, mime_type, size_bytes, storage_key, storage_provider, created_at, updated_at, is_deleted, deleted_at)
            VALUES ({deletedId}, {workspaceId}, 'deleted.png', 'image/png', 1, 'default/deleted.png', 'local', {now}, {now}, true, {now.AddDays(-10)});
            """);
        var beforeMigration = DateTimeOffset.UtcNow;

        await migrator.MigrateAsync();

        var rollingReplicaId = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO media_assets
                (id, workspace_id, file_name, mime_type, size_bytes, storage_key, storage_provider, created_at, updated_at, is_deleted)
            VALUES ({rollingReplicaId}, {workspaceId}, 'rolling.png', 'image/png', 1, 'default/rolling.png', 'local', {now}, {now}, false);
            """);

        var afterMigration = DateTimeOffset.UtcNow;
        context.ChangeTracker.Clear();
        var assets = await context.MediaAssets.IgnoreQueryFilters().Where(asset => asset.WorkspaceId == workspaceId).ToDictionaryAsync(asset => asset.Id);
        assets[activeId].BlobState.ShouldBe(MediaBlobState.Available);
        assets[deletedId].BlobState.ShouldBe(MediaBlobState.DeletePending);
        assets[rollingReplicaId].BlobState.ShouldBe(MediaBlobState.Available);
        var intent = await context.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == deletedId);
        intent.Reason.ShouldBe("migration_deleted");
        intent.NotBefore.ShouldBeGreaterThanOrEqualTo(beforeMigration.AddDays(30).AddSeconds(-1));
        intent.NotBefore.ShouldBeLessThanOrEqualTo(afterMigration.AddDays(30).AddSeconds(1));
    }

    [Fact]
    public async Task ConcurrentDeletionClaims_AreDisjointAcrossWorkers()
    {
        var now = DateTimeOffset.Parse("2026-08-27T12:30:00Z");
        var intentId = await SeedIntentAsync(now);
        await using var firstContext = await CreateContextAsync();
        await using var secondContext = await CreateContextAsync();
        var first = new MediaReconciliationRepository(firstContext);
        var second = new MediaReconciliationRepository(secondContext);

        var results = await Task.WhenAll(
            first.ClaimDeletionIntentsAsync("worker-a", now, TimeSpan.FromMinutes(5), 1),
            second.ClaimDeletionIntentsAsync("worker-b", now, TimeSpan.FromMinutes(5), 1));

        results.SelectMany(claims => claims).Select(claim => claim.Id).ShouldBe([intentId]);
    }

    [Fact]
    public async Task DeletionClaims_AreDisjoint_ReclaimAfterExpiry_AndFenceStaleCompletion()
    {
        var now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var intentId = await SeedIntentAsync(now);
        await using var firstContext = await CreateContextAsync();
        var first = new MediaReconciliationRepository(firstContext);
        var firstClaim = Assert.Single(await first.ClaimDeletionIntentsAsync("worker-a", now, TimeSpan.FromMinutes(5), 10));

        await using var secondContext = await CreateContextAsync();
        var second = new MediaReconciliationRepository(secondContext);
        Assert.Empty(await second.ClaimDeletionIntentsAsync("worker-b", now.AddMinutes(4), TimeSpan.FromMinutes(5), 10));
        var reclaimed = Assert.Single(await second.ClaimDeletionIntentsAsync("worker-b", now.AddMinutes(5), TimeSpan.FromMinutes(5), 10));

        reclaimed.Id.ShouldBe(intentId);
        reclaimed.WasReclaimed.ShouldBeTrue();
        reclaimed.LeaseToken.ShouldNotBe(firstClaim.LeaseToken);
        (await first.CompleteDeletionAsync(firstClaim, now.AddMinutes(5))).ShouldBeFalse();
        (await second.CompleteDeletionAsync(reclaimed, now.AddMinutes(5))).ShouldBeTrue();
    }

    [Fact]
    public async Task RetryDeletion_RecordsBoundedDiagnosticAndClearsLease()
    {
        var now = DateTimeOffset.Parse("2026-08-27T13:00:00Z");
        var intentId = await SeedIntentAsync(now);
        await using var context = await CreateContextAsync();
        var repository = new MediaReconciliationRepository(context);
        var claim = Assert.Single(await repository.ClaimDeletionIntentsAsync("worker", now, TimeSpan.FromMinutes(5), 1));

        (await repository.RetryDeletionAsync(claim, now, now.AddSeconds(30), new string('x', 3_000))).ShouldBeTrue();

        context.ChangeTracker.Clear();
        var intent = await context.MediaDeletionIntents.SingleAsync(item => item.Id == intentId);
        intent.AttemptCount.ShouldBe(1);
        intent.NextAttemptAt.ShouldBe(now.AddSeconds(30));
        intent.LastError!.Length.ShouldBe(2_000);
        intent.LeaseOwner.ShouldBeNull();
        intent.LeaseToken.ShouldBeNull();
        intent.LeaseExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareOrphanDeletion_CancelsIntentWhenKeyBecomesOwnedAfterClaim()
    {
        var now = DateTimeOffset.Parse("2026-08-27T13:30:00Z");
        var storageKey = $"cmsify/media/{Guid.NewGuid():N}";
        await using (var setup = await CreateContextAsync())
        {
            setup.MediaDeletionIntents.Add(new MediaDeletionIntent
            {
                Provider = "local",
                StorageKey = storageKey,
                Reason = "orphan",
                NotBefore = now,
                NextAttemptAt = now,
                CreatedAt = now
            });
            await setup.SaveChangesAsync();
        }

        await using var workerContext = await CreateContextAsync();
        var repository = new MediaReconciliationRepository(workerContext);
        var claim = Assert.Single(
            await repository.ClaimDeletionIntentsAsync("worker", now, TimeSpan.FromMinutes(5), 1_000),
            item => item.StorageKey == storageKey);

        await using (var apiContext = await CreateContextAsync())
        {
            var workspace = new Workspace { Name = "Claimed orphan", Slug = $"claimed-orphan-{Guid.NewGuid():N}" };
            var asset = Asset(workspace.Id, "claimed-orphan", now);
            asset.StorageKey = storageKey;
            asset.BlobState = MediaBlobState.PendingUpload;
            apiContext.AddRange(workspace, asset);
            await apiContext.SaveChangesAsync();
        }

        claim.Reason.ShouldBe("orphan");
        (await workerContext.MediaAssets.IgnoreQueryFilters().AnyAsync(
            asset => asset.StorageProvider == "local" && asset.StorageKey == storageKey && asset.BlobState != MediaBlobState.Deleted))
            .ShouldBeTrue();

        var result = await repository.PrepareDeletionAsync(claim, now.AddSeconds(1), TimeSpan.FromMinutes(5));

        result.ShouldBe(DeletionPreparationResult.Owned);
        workerContext.ChangeTracker.Clear();
        var intent = await workerContext.MediaDeletionIntents.SingleAsync(item => item.Id == claim.Id);
        intent.CompletedAt.ShouldBe(now.AddSeconds(1));
        intent.LeaseOwner.ShouldBeNull();
    }

    [Fact]
    public async Task FailStaleUploads_UsesInclusiveBoundaryAndCreatesImmediateCleanup()
    {
        var now = DateTimeOffset.Parse("2026-08-27T14:00:00Z");
        var cutoff = now.AddMinutes(-30);
        await using (var setup = await CreateContextAsync())
        {
            var workspace = new Workspace { Name = "Media", Slug = $"media-{Guid.NewGuid():N}" };
            setup.Workspaces.Add(workspace);
            setup.MediaAssets.AddRange(
                Asset(workspace.Id, "stale", cutoff),
                Asset(workspace.Id, "recent", cutoff.AddMilliseconds(1)));
            await setup.SaveChangesAsync();
        }

        await using var context = await CreateContextAsync();
        var count = await new MediaReconciliationRepository(context).FailStaleUploadsAsync(cutoff, now, 100);

        count.ShouldBe(1);
        context.ChangeTracker.Clear();
        var stale = await context.MediaAssets.SingleAsync(asset => asset.FileName == "stale");
        var recent = await context.MediaAssets.SingleAsync(asset => asset.FileName == "recent");
        stale.BlobState.ShouldBe(MediaBlobState.UploadFailed);
        recent.BlobState.ShouldBe(MediaBlobState.PendingUpload);
        var intent = await context.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == stale.Id);
        intent.NextAttemptAt.ShouldBe(now);
        intent.Reason.ShouldBe("abandoned_upload");
    }

    [Fact]
    public async Task CheckpointClaim_PersistsProgressAndFencesExpiredOwner()
    {
        var now = DateTimeOffset.Parse("2026-08-27T15:00:00Z");
        await using var firstContext = await CreateContextAsync();
        var first = new MediaReconciliationRepository(firstContext);
        var claim = await first.ClaimCheckpointAsync("local", "cmsify/media/", "worker-a", now, TimeSpan.FromMinutes(5));
        claim.ShouldNotBeNull();

        await using var secondContext = await CreateContextAsync();
        var second = new MediaReconciliationRepository(secondContext);
        (await second.ClaimCheckpointAsync("local", "cmsify/media/", "worker-b", now.AddMinutes(4), TimeSpan.FromMinutes(5))).ShouldBeNull();
        var reclaimed = await second.ClaimCheckpointAsync("local", "cmsify/media/", "worker-b", now.AddMinutes(5), TimeSpan.FromMinutes(5));
        reclaimed.ShouldNotBeNull();
        (await first.CompleteCheckpointAsync(claim, "cmsify/media/a", false, now.AddMinutes(5))).ShouldBeFalse();
        (await second.CompleteCheckpointAsync(reclaimed, "cmsify/media/b", false, now.AddMinutes(5))).ShouldBeTrue();

        await using var verification = await CreateContextAsync();
        var checkpoint = await verification.MediaReconciliationCheckpoints.SingleAsync();
        checkpoint.AfterKey.ShouldBe("cmsify/media/b");
        checkpoint.LeaseOwner.ShouldBeNull();
    }

    [Fact]
    public async Task MissingBlobCanReappear_AndVerificationTimeAdvancesInBothStates()
    {
        var now = DateTimeOffset.Parse("2026-08-27T15:30:00Z");
        Guid assetId;
        await using (var setup = await CreateContextAsync())
        {
            var workspace = new Workspace { Name = "Verification", Slug = $"verification-{Guid.NewGuid():N}" };
            var asset = Asset(workspace.Id, "verify", now.AddDays(-1));
            asset.BlobState = MediaBlobState.Available;
            asset.BlobVerifiedAt = now.AddDays(-1);
            setup.AddRange(workspace, asset);
            await setup.SaveChangesAsync();
            assetId = asset.Id;
        }

        await using var context = await CreateContextAsync();
        var repository = new MediaReconciliationRepository(context);
        await repository.RecordBlobMissingAsync(assetId, now);
        context.ChangeTracker.Clear();
        var missing = await context.MediaAssets.SingleAsync(asset => asset.Id == assetId);
        missing.BlobState.ShouldBe(MediaBlobState.Missing);
        missing.BlobVerifiedAt.ShouldBe(now);

        await repository.RecordBlobPresentAsync(assetId, now.AddMinutes(1));
        context.ChangeTracker.Clear();
        var present = await context.MediaAssets.SingleAsync(asset => asset.Id == assetId);
        present.BlobState.ShouldBe(MediaBlobState.Available);
        present.MissingDetectedAt.ShouldBeNull();
        present.BlobVerifiedAt.ShouldBe(now.AddMinutes(1));
    }

    [Fact]
    public async Task VerificationBatch_IsScopedToConfiguredProviderBeforeApplyingLimit()
    {
        var now = DateTimeOffset.Parse("2026-08-27T15:45:00Z");
        await using (var setup = await CreateContextAsync())
        {
            var workspace = new Workspace { Name = "Provider verification", Slug = $"provider-{Guid.NewGuid():N}" };
            setup.Workspaces.Add(workspace);
            for (var index = 0; index < 3; index++)
            {
                var wrongProvider = Asset(workspace.Id, $"s3-{index}", now.AddDays(-2));
                wrongProvider.BlobState = MediaBlobState.Available;
                wrongProvider.StorageProvider = "s3";
                wrongProvider.BlobVerifiedAt = now.AddDays(-2);
                setup.MediaAssets.Add(wrongProvider);
            }

            var local = Asset(workspace.Id, "local", now.AddDays(-1));
            local.BlobState = MediaBlobState.Available;
            local.BlobVerifiedAt = now.AddDays(-1);
            setup.MediaAssets.Add(local);
            await setup.SaveChangesAsync();
        }

        await using var context = await CreateContextAsync();
        var candidates = await new MediaReconciliationRepository(context).GetVerificationBatchAsync("local", 1);

        var candidate = Assert.Single(candidates);
        candidate.Provider.ShouldBe("local");
    }

    [Fact]
    public async Task MediaAssetRepository_HidesEveryNonAvailableLifecycleState()
    {
        var now = DateTimeOffset.Parse("2026-08-27T20:00:00Z");
        await using var context = await CreateContextAsync();
        var workspace = new Workspace { Name = "Repository visibility", Slug = $"repository-visibility-{Guid.NewGuid():N}" };
        var available = Asset(workspace.Id, "available", now);
        available.BlobState = MediaBlobState.Available;
        context.Add(workspace);
        context.MediaAssets.Add(available);
        foreach (var state in new[] { MediaBlobState.PendingUpload, MediaBlobState.UploadFailed, MediaBlobState.Missing, MediaBlobState.DeletePending })
        {
            var hidden = Asset(workspace.Id, state.ToString(), now);
            hidden.BlobState = state;
            context.MediaAssets.Add(hidden);
        }
        await context.SaveChangesAsync();
        var repository = CreateMediaAssetRepository(context, workspace.Id);

        var page = await repository.ListByWorkspaceAsync(workspace.Id, new PageRequest(0, 100));

        Assert.Equal([available.Id], page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task MediaAssetRepository_RejectsCreationWhenBlobCannotBeVerified()
    {
        await using var context = await CreateContextAsync();
        var workspace = new Workspace { Name = "Repository create", Slug = $"repository-create-{Guid.NewGuid():N}" };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();
        var repository = CreateMediaAssetRepository(context, workspace.Id);
        var command = new CreateMediaAssetCommand(
            workspace.Id, "missing.txt", "text/plain", 1, $"cmsify/media/{workspace.Id}/missing.txt", "local", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CreateAsync(command));
    }

    [Fact]
    public async Task MediaAssetRepository_VerifiesBlobAndCreatesAvailableAsset()
    {
        var now = DateTimeOffset.Parse("2026-08-27T20:30:00Z");
        await using var context = await CreateContextAsync();
        var workspace = new Workspace { Name = "Repository verified create", Slug = $"repository-verified-{Guid.NewGuid():N}" };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();
        var key = $"cmsify/media/{workspace.Id}/verified.txt";
        var storage = Substitute.For<IStorageProvider>();
        storage.GetMetadataAsync(key, Arg.Any<CancellationToken>())
            .Returns(new StorageObjectMetadata(key, 42, "text/plain", now));
        var repository = CreateMediaAssetRepository(context, workspace.Id, storage, new FixedTimeProvider(now));

        var created = await repository.CreateAsync(new CreateMediaAssetCommand(
            workspace.Id, "verified.txt", "text/plain", 1, key, "local", null));

        context.ChangeTracker.Clear();
        var persisted = await context.MediaAssets.SingleAsync(item => item.Id == created.Id);
        persisted.BlobState.ShouldBe(MediaBlobState.Available);
        persisted.SizeBytes.ShouldBe(42);
        persisted.UploadCompletedAt.ShouldBe(now);
        persisted.BlobVerifiedAt.ShouldBe(now);
    }

    [Fact]
    public async Task MediaAssetRepository_CanonicalizesProviderForOwnershipFencing()
    {
        var now = DateTimeOffset.Parse("2026-08-27T20:45:00Z");
        await using var context = await CreateContextAsync();
        var workspace = new Workspace { Name = "Canonical provider", Slug = $"canonical-provider-{Guid.NewGuid():N}" };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();
        var key = $"cmsify/media/{workspace.Id}/canonical.txt";
        var storage = Substitute.For<IStorageProvider>();
        storage.GetMetadataAsync(key, Arg.Any<CancellationToken>())
            .Returns(new StorageObjectMetadata(key, 1, "text/plain", now));
        var repository = CreateMediaAssetRepository(context, workspace.Id, storage, new FixedTimeProvider(now));

        var created = await repository.CreateAsync(new CreateMediaAssetCommand(
            workspace.Id, "canonical.txt", "text/plain", 1, key, "LOCAL", null));

        context.ChangeTracker.Clear();
        (await context.MediaAssets.SingleAsync(item => item.Id == created.Id)).StorageProvider.ShouldBe("local");
        (await new MediaReconciliationRepository(context).StorageKeyExistsAsync("local", key)).ShouldBeTrue();
    }

    [Fact]
    public async Task MediaAssetRepository_SoftDeleteCreatesRecoverableIntent()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = await CreateContextAsync();
        var workspace = new Workspace { Name = "Repository delete", Slug = $"repository-delete-{Guid.NewGuid():N}" };
        var asset = Asset(workspace.Id, "delete", now);
        asset.BlobState = MediaBlobState.Available;
        context.AddRange(workspace, asset);
        await context.SaveChangesAsync();
        var repository = CreateMediaAssetRepository(context, workspace.Id);

        await repository.SoftDeleteAsync(asset.Id, Guid.NewGuid());

        context.ChangeTracker.Clear();
        var deleted = await context.MediaAssets.IgnoreQueryFilters().SingleAsync(item => item.Id == asset.Id);
        var intent = await context.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == asset.Id);
        deleted.BlobState.ShouldBe(MediaBlobState.DeletePending);
        intent.Reason.ShouldBe("user_delete");
        intent.NotBefore.ShouldBe(deleted.PurgeAfter!.Value);
        intent.NotBefore.ShouldBeInRange(now.AddDays(30).AddMinutes(-1), now.AddDays(30).AddMinutes(1));
    }

    private async Task<Guid> SeedIntentAsync(DateTimeOffset dueAt)
    {
        await using var context = await CreateContextAsync();
        var intent = new MediaDeletionIntent
        {
            Provider = "local",
            StorageKey = $"cmsify/media/{Guid.NewGuid():N}",
            Reason = "test",
            NotBefore = dueAt,
            NextAttemptAt = dueAt,
            CreatedAt = dueAt
        };
        context.MediaDeletionIntents.Add(intent);
        await context.SaveChangesAsync();
        return intent.Id;
    }

    private static MediaAsset Asset(Guid workspaceId, string fileName, DateTimeOffset stateChangedAt) => new()
    {
        WorkspaceId = workspaceId,
        FileName = fileName,
        MimeType = "image/png",
        StorageKey = $"cmsify/media/{workspaceId}/{fileName}",
        StorageProvider = "local",
        BlobState = MediaBlobState.PendingUpload,
        BlobStateChangedAt = stateChangedAt
    };

    private static MediaAssetRepository CreateMediaAssetRepository(
        CmsifyDbContext context,
        Guid workspaceId,
        IStorageProvider? storage = null,
        TimeProvider? timeProvider = null) => new(
            context,
            new CurrentActorInfo(null, null, UserRole.Admin, workspaceId, true),
            storage ?? Substitute.For<IStorageProvider>(),
            Options.Create(new MediaOperationalOptions()),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "local"
            }).Build(),
            timeProvider);

    private async Task<CmsifyDbContext> CreateContextAsync()
    {
        var context = new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options);
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
