using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SyntaxCircus.EntityFrameworkCore.Postgres;
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

        var afterMigration = DateTimeOffset.UtcNow;
        context.ChangeTracker.Clear();
        var assets = await context.MediaAssets.IgnoreQueryFilters().Where(asset => asset.WorkspaceId == workspaceId).ToDictionaryAsync(asset => asset.Id);
        assets[activeId].BlobState.ShouldBe(MediaBlobState.Available);
        assets[deletedId].BlobState.ShouldBe(MediaBlobState.DeletePending);
        var intent = await context.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == deletedId);
        intent.Reason.ShouldBe("migration_deleted");
        intent.NotBefore.ShouldBeGreaterThanOrEqualTo(beforeMigration.AddDays(30).AddSeconds(-1));
        intent.NotBefore.ShouldBeLessThanOrEqualTo(afterMigration.AddDays(30).AddSeconds(1));
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

    private async Task<CmsifyDbContext> CreateContextAsync()
    {
        var context = new CmsifyDbContext(new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .UseSyntaxCircusSnakeCaseNamingConvention()
            .Options);
        await context.Database.MigrateAsync();
        return context;
    }
}
