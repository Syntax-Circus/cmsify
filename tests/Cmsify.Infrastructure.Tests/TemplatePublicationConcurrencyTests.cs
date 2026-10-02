using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SyntaxCircus.EntityFrameworkCore.Postgres;
using Testcontainers.PostgreSql;

namespace Cmsify.Infrastructure.Tests;

public sealed class TemplatePublicationConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sqlite_PublishInvalidatesStalePublishedVersionAndScopesArchival()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cmsify-template-publication-{Guid.NewGuid():N}.db");
        var options = SqliteOptions(path);
        try
        {
            await using var setup = new CmsifyDbContext(options);
            await setup.Database.EnsureCreatedAsync(Ct);
            await VerifyStalePublicationAsync(options);
        }
        finally
        {
            DeleteSqliteFiles(path);
        }
    }

    [Fact]
    public async Task Postgres_PublishInvalidatesStalePublishedVersionAndScopesArchival()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("template_publication").WithUsername("cmsify").WithPassword("cmsify").Build();
        await postgres.StartAsync(Ct);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).UseSyntaxCircusSnakeCaseNamingConvention().Options;
        await using var setup = new CmsifyDbContext(options);
        await setup.Database.MigrateAsync(Ct);
        await VerifyStalePublicationAsync(options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_CallerOwnsPublicationCommitAndRollback(bool failTrackedSave)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cmsify-template-caller-transaction-{Guid.NewGuid():N}.db");
        var options = SqliteOptions(path);
        try
        {
            await using var setup = new CmsifyDbContext(options);
            await setup.Database.EnsureCreatedAsync(Ct);
            await VerifyCallerOwnedTransactionAsync(options, failTrackedSave);
        }
        finally
        {
            DeleteSqliteFiles(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Postgres_CallerOwnsPublicationCommitAndRollback(bool failTrackedSave)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("template_caller_transaction").WithUsername("cmsify").WithPassword("cmsify").Build();
        await postgres.StartAsync(Ct);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).UseSyntaxCircusSnakeCaseNamingConvention().Options;
        await using var setup = new CmsifyDbContext(options);
        await setup.Database.MigrateAsync(Ct);
        await VerifyCallerOwnedTransactionAsync(options, failTrackedSave);
    }

    [Fact]
    public async Task Sqlite_PublishWithExhaustedPublishedTokenLeavesEntirePublicationUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cmsify-template-exhaustion-{Guid.NewGuid():N}.db");
        var options = SqliteOptions(path);
        try
        {
            var seed = PublicationSeed.Create();
            await using (var setup = new CmsifyDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync(Ct);
                setup.AddRange(seed.Entities);
                await setup.SaveChangesAsync(Ct);
                // Seed the boundary without the tracked-save increment under test.
                await setup.TemplateVersions.Where(version => version.Id == seed.Published.Id)
                    .ExecuteUpdateAsync(updates => updates.SetProperty(version => EF.Property<uint>(version, "xmin"), uint.MaxValue), Ct);
            }
            await using var staleContext = new CmsifyDbContext(options);
            var stale = await staleContext.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct);
            await using (var publishing = new CmsifyDbContext(options))
            {
                var repository = new TemplateVersionRepository(publishing, CurrentActorInfo.Anonymous);
                await Should.ThrowAsync<OverflowException>(() => repository.PublishAsync(seed.Draft.Id, Guid.NewGuid(), Ct));
            }
            await using var verification = new CmsifyDbContext(options);
            (await verification.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct)).Status.ShouldBe(TemplateVersionStatus.Published);
            verification.Entry(await verification.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct))
                .Property<uint>("xmin").CurrentValue.ShouldBe(uint.MaxValue);
            (await verification.TemplateVersions.SingleAsync(version => version.Id == seed.SecondPublished.Id, Ct)).Status.ShouldBe(TemplateVersionStatus.Published);
            verification.Entry(await verification.TemplateVersions.SingleAsync(version => version.Id == seed.SecondPublished.Id, Ct))
                .Property<uint>("xmin").CurrentValue.ShouldBe(1u);
            var draft = await verification.TemplateVersions.SingleAsync(version => version.Id == seed.Draft.Id, Ct);
            draft.Status.ShouldBe(TemplateVersionStatus.Draft);
            draft.PublishedAt.ShouldBeNull();
            (await verification.Templates.SingleAsync(template => template.Id == seed.Template.Id, Ct)).CurrentVersionId.ShouldBeNull();
            stale.Notes = "Exhausted tracked saves must also fail without wrapping";
            await Should.ThrowAsync<OverflowException>(() => staleContext.SaveChangesAsync(Ct));
        }
        finally
        {
            DeleteSqliteFiles(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_TrackedSaveExhaustionRollsBackPriorArchival(bool exhaustTemplate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cmsify-template-rollback-{Guid.NewGuid():N}.db");
        var options = SqliteOptions(path);
        try
        {
            var seed = PublicationSeed.Create();
            await using (var setup = new CmsifyDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync(Ct);
                setup.AddRange(seed.Entities);
                await setup.SaveChangesAsync(Ct);
                if (exhaustTemplate)
                    await setup.Templates.Where(template => template.Id == seed.Template.Id)
                        .ExecuteUpdateAsync(updates => updates.SetProperty(template => EF.Property<uint>(template, "xmin"), uint.MaxValue), Ct);
                else
                    await setup.TemplateVersions.Where(version => version.Id == seed.Draft.Id)
                        .ExecuteUpdateAsync(updates => updates.SetProperty(version => EF.Property<uint>(version, "xmin"), uint.MaxValue), Ct);
            }
            await using (var publishing = new CmsifyDbContext(options))
            {
                var repository = new TemplateVersionRepository(publishing, CurrentActorInfo.Anonymous);
                await Should.ThrowAsync<OverflowException>(() => repository.PublishAsync(seed.Draft.Id, Guid.NewGuid(), Ct));
            }
            await using var verification = new CmsifyDbContext(options);
            var published = await verification.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct);
            published.Status.ShouldBe(TemplateVersionStatus.Published);
            verification.Entry(published).Property<uint>("xmin").CurrentValue.ShouldBe(1u);
            (await verification.TemplateVersions.SingleAsync(version => version.Id == seed.SecondPublished.Id, Ct)).Status.ShouldBe(TemplateVersionStatus.Published);
            var draft = await verification.TemplateVersions.SingleAsync(version => version.Id == seed.Draft.Id, Ct);
            draft.Status.ShouldBe(TemplateVersionStatus.Draft);
            draft.PublishedAt.ShouldBeNull();
            var template = await verification.Templates.SingleAsync(template => template.Id == seed.Template.Id, Ct);
            template.CurrentVersionId.ShouldBeNull();
            (exhaustTemplate ? verification.Entry(template).Property<uint>("xmin").CurrentValue : verification.Entry(draft).Property<uint>("xmin").CurrentValue)
                .ShouldBe(uint.MaxValue);
        }
        finally
        {
            DeleteSqliteFiles(path);
        }
    }

    private static async Task VerifyCallerOwnedTransactionAsync(DbContextOptions<CmsifyDbContext> options, bool failTrackedSave)
    {
        var seed = PublicationSeed.Create();
        Dictionary<Guid, uint> originalVersionTokens;
        uint originalTemplateToken;
        await using (var setup = new CmsifyDbContext(options))
        {
            setup.AddRange(seed.Entities);
            await setup.SaveChangesAsync(Ct);
            originalVersionTokens = await setup.TemplateVersions
                .Select(version => new { version.Id, Token = EF.Property<uint>(version, "xmin") })
                .ToDictionaryAsync(version => version.Id, version => version.Token, Ct);
            originalTemplateToken = setup.Entry(seed.Template).Property<uint>("xmin").CurrentValue;
        }

        await using var publishing = new CmsifyDbContext(options);
        await using var callerTransaction = await publishing.Database.BeginTransactionAsync(Ct);
        if (failTrackedSave)
        {
            // The existing workspace/slug unique constraint fails in both real providers.
            // This tracked write is saved after the repository's bulk archival statement.
            var template = await publishing.Templates.SingleAsync(template => template.Id == seed.Template.Id, Ct);
            template.Slug = seed.OtherTemplate.Slug;
        }
        var repository = new TemplateVersionRepository(publishing, CurrentActorInfo.Anonymous);
        if (failTrackedSave)
            await Should.ThrowAsync<DbUpdateException>(() => repository.PublishAsync(seed.Draft.Id, Guid.NewGuid(), Ct));
        else
            (await repository.PublishAsync(seed.Draft.Id, Guid.NewGuid(), Ct)).Status.ShouldBe(TemplateVersionStatus.Published);

        publishing.Database.CurrentTransaction.ShouldBeSameAs(callerTransaction);
        // The repository neither commits nor rolls back the transaction supplied by its caller.
        (await publishing.TemplateVersions.AsNoTracking().SingleAsync(version => version.Id == seed.Published.Id, Ct))
            .Status.ShouldBe(TemplateVersionStatus.Archived);
        await AssertPublicationUnchangedAsync(options, seed, originalVersionTokens, originalTemplateToken);
        await callerTransaction.RollbackAsync(Ct);
        await AssertPublicationUnchangedAsync(options, seed, originalVersionTokens, originalTemplateToken);
    }

    private static async Task AssertPublicationUnchangedAsync(DbContextOptions<CmsifyDbContext> options,
        PublicationSeed seed, Dictionary<Guid, uint> originalVersionTokens, uint originalTemplateToken)
    {
        // Independent reads verify that pending writes are invisible, and that caller rollback
        // restores both status and concurrency tokens rather than merely changing tracked objects.
        await using var verification = new CmsifyDbContext(options);
        var versions = await verification.TemplateVersions.ToDictionaryAsync(version => version.Id, Ct);
        versions[seed.Published.Id].Status.ShouldBe(TemplateVersionStatus.Published);
        versions[seed.SecondPublished.Id].Status.ShouldBe(TemplateVersionStatus.Published);
        versions[seed.Archived.Id].Status.ShouldBe(TemplateVersionStatus.Archived);
        versions[seed.OtherPublished.Id].Status.ShouldBe(TemplateVersionStatus.Published);
        versions[seed.Draft.Id].Status.ShouldBe(TemplateVersionStatus.Draft);
        versions[seed.Draft.Id].PublishedAt.ShouldBeNull();
        foreach (var (id, token) in originalVersionTokens)
            verification.Entry(versions[id]).Property<uint>("xmin").CurrentValue.ShouldBe(token);
        var template = await verification.Templates.SingleAsync(template => template.Id == seed.Template.Id, Ct);
        template.CurrentVersionId.ShouldBeNull();
        template.Slug.ShouldBe(seed.Template.Slug);
        verification.Entry(template).Property<uint>("xmin").CurrentValue.ShouldBe(originalTemplateToken);
    }

    private static async Task VerifyStalePublicationAsync(DbContextOptions<CmsifyDbContext> options)
    {
        var seed = PublicationSeed.Create();
        await using (var setup = new CmsifyDbContext(options))
        {
            setup.AddRange(seed.Entities);
            await setup.SaveChangesAsync(Ct);
        }
        await using var staleContext = new CmsifyDbContext(options);
        var stale = await staleContext.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct);
        var originalToken = staleContext.Entry(stale).Property<uint>("xmin").CurrentValue;
        var untouchedTokens = await staleContext.TemplateVersions.Where(version => version.Id == seed.Archived.Id || version.Id == seed.OtherPublished.Id)
            .Select(version => new { version.Id, Token = EF.Property<uint>(version, "xmin") }).ToDictionaryAsync(version => version.Id, version => version.Token, Ct);
        await using (var publishing = new CmsifyDbContext(options))
        {
            var repository = new TemplateVersionRepository(publishing, CurrentActorInfo.Anonymous);
            (await repository.PublishAsync(seed.Draft.Id, Guid.NewGuid(), Ct)).Status.ShouldBe(TemplateVersionStatus.Published);
        }
        await using (var verification = new CmsifyDbContext(options))
        {
            var archived = await verification.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct);
            archived.Status.ShouldBe(TemplateVersionStatus.Archived);
            verification.Entry(archived).Property<uint>("xmin").CurrentValue.ShouldNotBe(originalToken);
            (await verification.TemplateVersions.SingleAsync(version => version.Id == seed.SecondPublished.Id, Ct)).Status.ShouldBe(TemplateVersionStatus.Archived);
            foreach (var (id, token) in untouchedTokens)
            {
                var untouched = await verification.TemplateVersions.SingleAsync(version => version.Id == id, Ct);
                untouched.Status.ShouldBe(id == seed.Archived.Id ? TemplateVersionStatus.Archived : TemplateVersionStatus.Published);
                verification.Entry(untouched).Property<uint>("xmin").CurrentValue.ShouldBe(token);
            }
        }
        stale.Status = TemplateVersionStatus.Draft;
        stale.Notes = "Stale editor";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync(Ct));
        await using var fresh = new CmsifyDbContext(options);
        var retained = await fresh.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct);
        retained.Status.ShouldBe(TemplateVersionStatus.Archived);
        retained.Notes.ShouldBeNull();
        (await fresh.Templates.SingleAsync(template => template.Id == seed.Template.Id, Ct)).CurrentVersionId.ShouldBe(seed.Draft.Id);
    }

    private static DbContextOptions<CmsifyDbContext> SqliteOptions(string path) =>
        new DbContextOptionsBuilder<CmsifyDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Default Timeout=15").UseSnakeCaseNamingConvention().Options;

    private static void DeleteSqliteFiles(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }

    private sealed record PublicationSeed(Workspace Workspace, Template Template, Template OtherTemplate,
        TemplateVersion Published, TemplateVersion SecondPublished, TemplateVersion Archived, TemplateVersion Draft, TemplateVersion OtherPublished)
    {
        public object[] Entities => [Workspace, Template, OtherTemplate, Published, SecondPublished, Archived, Draft, OtherPublished];

        public static PublicationSeed Create()
        {
            var workspace = new Workspace { Name = "Publication", Slug = $"publication-{Guid.NewGuid():N}" };
            var template = new Template { WorkspaceId = workspace.Id, Name = "Profile", Slug = "profile" };
            var otherTemplate = new Template { WorkspaceId = workspace.Id, Name = "Other", Slug = "other" };
            return new(workspace, template, otherTemplate,
                new TemplateVersion { TemplateId = template.Id, VersionNumber = 2, Status = TemplateVersionStatus.Published },
                new TemplateVersion { TemplateId = template.Id, VersionNumber = 3, Status = TemplateVersionStatus.Published },
                new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Archived },
                new TemplateVersion { TemplateId = template.Id, VersionNumber = 4, Status = TemplateVersionStatus.Draft },
                new TemplateVersion { TemplateId = otherTemplate.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published });
        }
    }
}
