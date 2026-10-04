using System.Data.Common;
using Cmsify.Api.Controllers;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SyntaxCircus.Cmsify.Contracts;
using Testcontainers.PostgreSql;
using ContentStatus = Cmsify.Core.Domain.Enums.ContentStatus;
using TemplateVersionStatus = Cmsify.Core.Domain.Enums.TemplateVersionStatus;

namespace Cmsify.Api.Integration.Tests;

// Actual legacy controller paths, independent contexts, file-backed SQLite and
// migrated PostgreSQL. No production host/provider-support claim for SQLite.
public sealed class ContentIdentityControllerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IdentityMutation_RejectsStaleVersionSaveAndPreservesAffectedSet(bool link, bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        await using var staleContext = new CmsifyDbContext(database.Options);
        var stale = await staleContext.ContentVersions.SingleAsync(version => version.Id == seed.Versions[0].Id, Ct);
        await using (var writer = new CmsifyDbContext(database.Options))
            (await MutateAsync(writer, seed, link)).ShouldBeOfType<OkObjectResult>();

        stale.Slug = "stale-overwrite";
        staleContext.Entry(stale).Property(version => version.Slug).IsModified = true;
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync(Ct));
        await using var verification = new CmsifyDbContext(database.Options);
        var versions = await verification.ContentVersions.ToListAsync(Ct);
        foreach (var version in versions)
        {
            var prior = baseline[version.Id];
            var affected = version.ContentItemId == seed.Source.Id || (link && version.ContentItemId == seed.Target.Id);
            version.Slug.ShouldBe(affected && !link ? "renamed" : prior.Slug);
            version.LocaleCode.ShouldBe(affected && !link ? "fr" : prior.LocaleCode);
            version.TranslationGroupId.ShouldBe(affected ? seed.SourceGroup : prior.TranslationGroupId);
            string.Join(',', version.Tags).ShouldBe(prior.Tags);
            version.Status.ShouldBe(prior.Status);
            version.UpdatedAt.ShouldBe(prior.UpdatedAt);
            version.PublishedAt.ShouldBe(prior.PublishedAt);
            version.PublishAt.ShouldBe(prior.PublishAt);
            version.EffectiveStartAt.ShouldBe(prior.EffectiveStartAt);
            version.EffectiveEndAt.ShouldBe(prior.EffectiveEndAt);
            version.ArchivedAt.ShouldBe(prior.ArchivedAt);
            version.PublishLeaseOwner.ShouldBe(prior.PublishLeaseOwner);
            version.PublishLeaseToken.ShouldBe(prior.PublishLeaseToken);
            version.PublishLeaseExpiresAt.ShouldBe(prior.PublishLeaseExpiresAt);
            version.RolledBackFromVersionNumber.ShouldBe(prior.RolledBackFromVersionNumber);
            var token = verification.Entry(version).Property<uint>("xmin").CurrentValue;
            if (affected && postgres) token.ShouldNotBe(prior.Token);
            else token.ShouldBe(prior.Token + (affected ? 1u : 0u));
        }
        (await verification.ContentVersionFieldValues.SingleAsync(Ct)).TextValue.ShouldBe("Retained field");
        (await verification.WebhookOutboxEvents.CountAsync(Ct)).ShouldBe(link ? 0 : 1);
        if (!link)
        {
            (await verification.Tags.Where(tag => verification.ContentItemTags.Any(join => join.ContentItemId == seed.Source.Id && join.TagId == tag.Id))
                .Select(tag => tag.Name).ToListAsync(Ct)).ShouldBe(["new-tag"]);
            (await verification.WebhookOutboxEvents.SingleAsync(Ct)).EventType.ShouldBe("content.updated");
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task LateBulkFailure_RollsBackItemTagsOutboxAndVersions(bool link, bool postgres, bool cancel)
    {
        await using var database = await Database.CreateAsync(postgres);
        // With neither item grouped, linking mutates both items before the fault.
        var seed = await Seed.CreateAsync(database.Options, nullGroups: link);
        var baseline = await SnapshotAsync(database.Options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var failure = new BulkFailure(cancel, cancellation);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>(database.Options).AddInterceptors(failure).Options;
        await using (var writer = new CmsifyDbContext(options))
        {
            if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => MutateAsync(writer, seed, link, cancellation.Token));
            else await Should.ThrowAsync<InvalidOperationException>(() => MutateAsync(writer, seed, link));
        }
        failure.Calls.ShouldBe(1);
        await AssertUnchangedAsync(database.Options, seed, baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_ExhaustionRejectsAffectedSetBeforeAnyMutation(bool link)
    {
        await using var database = await Database.CreateAsync(false);
        var seed = await Seed.CreateAsync(database.Options);
        await using (var setup = new CmsifyDbContext(database.Options))
            await setup.ContentVersions.Where(version => version.Id == seed.Versions[1].Id)
                .ExecuteUpdateAsync(updates => updates.SetProperty(version => EF.Property<uint>(version, "xmin"), uint.MaxValue), Ct);
        var baseline = await SnapshotAsync(database.Options);
        await using (var writer = new CmsifyDbContext(database.Options))
            await Should.ThrowAsync<OverflowException>(() => MutateAsync(writer, seed, link));
        await AssertUnchangedAsync(database.Options, seed, baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_UnrelatedExhaustedVersionDoesNotBlockMutation(bool link)
    {
        await using var database = await Database.CreateAsync(false);
        var seed = await Seed.CreateAsync(database.Options);
        await using (var setup = new CmsifyDbContext(database.Options))
            await setup.ContentVersions.Where(version => version.ContentItemId == seed.Other.Id)
                .ExecuteUpdateAsync(updates => updates.SetProperty(version => EF.Property<uint>(version, "xmin"), uint.MaxValue), Ct);
        await using var writer = new CmsifyDbContext(database.Options);
        (await MutateAsync(writer, seed, link)).ShouldBeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task CallerTransaction_FailureRestoresLoadedGraphAndPendingIntentWithoutReplay(bool link, bool postgres, bool cancel)
    {
        await using var database = await Database.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var failure = new BulkFailure(cancel, cancellation);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>(database.Options).AddInterceptors(failure).Options;
        await using var writer = new CmsifyDbContext(options);
        await using var caller = await writer.Database.BeginTransactionAsync(Ct);
        var earlier = new Workspace { Name = "Earlier caller write", Slug = $"earlier-{Guid.NewGuid():N}" };
        writer.Add(earlier);
        await writer.SaveChangesAsync(Ct);
        earlier.Name = "Pending caller modification";
        var originalToken = writer.Entry(earlier).Property<uint>("xmin").OriginalValue;
        var pending = new Workspace { Name = "Pending caller addition", Slug = $"pending-{Guid.NewGuid():N}" };
        writer.Add(pending);
        var source = await writer.ContentItems.Include(item => item.Tags).SingleAsync(item => item.Id == seed.Source.Id, Ct);
        var joins = source.Tags.ToArray();
        var sourceToken = writer.Entry(source).Property<uint>("xmin").OriginalValue;
        if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => MutateAsync(writer, seed, link, cancellation.Token));
        else await Should.ThrowAsync<InvalidOperationException>(() => MutateAsync(writer, seed, link));
        writer.Database.CurrentTransaction.ShouldBeSameAs(caller);
        source.Slug.ShouldBe("source");
        source.TranslationGroupId.ShouldBe(seed.SourceGroup);
        source.Tags.ShouldBe(joins);
        writer.Entry(source).Collection(item => item.Tags).IsLoaded.ShouldBeTrue();
        writer.Entry(source).Property<uint>("xmin").OriginalValue.ShouldBe(sourceToken);
        writer.Entry(earlier).State.ShouldBe(EntityState.Modified);
        writer.Entry(earlier).Property<uint>("xmin").OriginalValue.ShouldBe(originalToken);
        writer.Entry(pending).State.ShouldBe(EntityState.Added);
        failure.Disabled = true;
        await writer.SaveChangesAsync(Ct);
        await caller.CommitAsync(Ct);
        await AssertUnchangedAsync(database.Options, seed, baseline);
        await using var verification = new CmsifyDbContext(database.Options);
        (await verification.Workspaces.SingleAsync(item => item.Id == earlier.Id, Ct)).Name.ShouldBe("Pending caller modification");
        (await verification.Workspaces.AnyAsync(item => item.Id == pending.Id, Ct)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CallerTransaction_SuccessLeavesCommitOwnershipWithCaller(bool link, bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        await using var writer = new CmsifyDbContext(database.Options);
        await using var caller = await writer.Database.BeginTransactionAsync(Ct);
        (await MutateAsync(writer, seed, link)).ShouldBeOfType<OkObjectResult>();
        writer.Database.CurrentTransaction.ShouldBeSameAs(caller);
        await AssertUnchangedAsync(database.Options, seed, baseline);
        await caller.RollbackAsync(Ct);
        await AssertUnchangedAsync(database.Options, seed, baseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_WithoutIdentityChangeKeepsVersionTokensAndSnapshots(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        await using (var writer = new CmsifyDbContext(database.Options))
        {
            var controller = Controller(writer);
            (await controller.Update(seed.Workspace.Id, seed.Source.Id,
                new("source", "en", seed.SourceGroup, ["new-tag"]), Ct)).Result.ShouldBeOfType<OkObjectResult>();
        }
        var after = await SnapshotAsync(database.Options);
        foreach (var (id, value) in baseline) after[id].ShouldBe(value);
        await using var verification = new CmsifyDbContext(database.Options);
        (await verification.WebhookOutboxEvents.CountAsync(Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controllers_PreserveAuthorizationNotFoundAndIfMatchResponses(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        await using var writer = new CmsifyDbContext(database.Options);
        var denied = Controller(writer, false);
        (await denied.Update(seed.Workspace.Id, seed.Source.Id, Request(seed), Ct)).Result.ShouldBeOfType<NotFoundResult>();
        (await denied.LinkTranslation(seed.Workspace.Id, seed.Source.Id, new(seed.Target.Id), Ct)).Result.ShouldBeOfType<NotFoundResult>();
        var controller = Controller(writer);
        (await controller.Update(seed.Workspace.Id, Guid.NewGuid(), Request(seed), Ct)).Result.ShouldBeOfType<NotFoundResult>();
        (await controller.LinkTranslation(seed.Workspace.Id, seed.Source.Id, new(Guid.NewGuid()), Ct)).Result.ShouldBeOfType<NotFoundResult>();
        controller.Request.Headers.IfMatch = "\"stale\"";
        (await controller.Update(seed.Workspace.Id, seed.Source.Id, Request(seed), Ct)).Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(412);
        await AssertUnchangedAsync(database.Options, seed, baseline);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BulkMutation_HasConstantCommandsAndNoExtraVersionMaterialization(bool link, bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options, 32);
        var counter = new Counter();
        var options = new DbContextOptionsBuilder<CmsifyDbContext>(database.Options).AddInterceptors(counter).Options;
        await using var writer = new CmsifyDbContext(options);
        (await MutateAsync(writer, seed, link)).ShouldBeOfType<OkObjectResult>();
        counter.BulkUpdates.ShouldBe(1);
        // Existing response builders load each returned item's versions. The repair
        // must introduce no historical-version load beyond those response reads.
        counter.MaterializedVersions.ShouldBe(link ? 33 : 32);
        counter.Commands.ShouldBeLessThan(25);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task LinkTranslation_KeepsSourceThenTargetThenNewGroupSelection(bool postgres, int selection)
    {
        await using var database = await Database.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var targetGroup = seed.Target.TranslationGroupId;
        await using (var setup = new CmsifyDbContext(database.Options))
        {
            if (selection > 0)
                await setup.ContentItems.Where(item => item.Id == seed.Source.Id)
                    .ExecuteUpdateAsync(updates => updates.SetProperty(item => item.TranslationGroupId, (Guid?)null), Ct);
            if (selection > 1)
                await setup.ContentItems.Where(item => item.Id == seed.Target.Id)
                    .ExecuteUpdateAsync(updates => updates.SetProperty(item => item.TranslationGroupId, (Guid?)null), Ct);
        }
        await using (var writer = new CmsifyDbContext(database.Options))
            (await MutateAsync(writer, seed, true)).ShouldBeOfType<OkObjectResult>();
        await using var verification = new CmsifyDbContext(database.Options);
        var source = await verification.ContentItems.SingleAsync(item => item.Id == seed.Source.Id, Ct);
        var target = await verification.ContentItems.SingleAsync(item => item.Id == seed.Target.Id, Ct);
        source.TranslationGroupId.ShouldNotBeNull();
        source.TranslationGroupId.ShouldBe(target.TranslationGroupId);
        if (selection == 0) source.TranslationGroupId.ShouldBe(seed.SourceGroup);
        else if (selection == 1) source.TranslationGroupId.ShouldBe(targetGroup);
        else { source.TranslationGroupId.ShouldNotBe(seed.SourceGroup); source.TranslationGroupId.ShouldNotBe(targetGroup); }
        (await verification.ContentVersions.Where(version => version.ContentItemId == seed.Source.Id || version.ContentItemId == seed.Target.Id)
            .AllAsync(version => version.TranslationGroupId == source.TranslationGroupId, Ct)).ShouldBeTrue();
        (await verification.WebhookOutboxEvents.CountAsync(Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_TrackedConcurrencyFailureReturns412AndDoesNotReplayFailedGraph(bool postgres)
    {
        await using var database = await Database.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        await using var writer = new CmsifyDbContext(database.Options);
        var source = await writer.ContentItems.Include(item => item.Tags).SingleAsync(item => item.Id == seed.Source.Id, Ct);
        var original = writer.Entry(source).Property<uint>("xmin").OriginalValue;
        await using (var competitor = new CmsifyDbContext(database.Options))
        {
            var competingSource = await competitor.ContentItems.SingleAsync(item => item.Id == seed.Source.Id, Ct);
            competingSource.SearchVector = "'retained'";
            await competitor.SaveChangesAsync(Ct);
        }
        var controller = Controller(writer);
        (await controller.Update(seed.Workspace.Id, seed.Source.Id, Request(seed), Ct)).Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(412);
        source.Slug.ShouldBe("source");
        writer.Entry(source).Property<uint>("xmin").OriginalValue.ShouldBe(original);
        writer.Entry(source).State.ShouldBe(EntityState.Unchanged);
        writer.Add(new Workspace { Name = "Later work", Slug = $"later-{Guid.NewGuid():N}" });
        await writer.SaveChangesAsync(Ct);
        await AssertUnchangedAsync(database.Options, seed, baseline);
        await using var verification = new CmsifyDbContext(database.Options);
        (await verification.ContentItems.SingleAsync(item => item.Id == source.Id, Ct)).SearchVector.ShouldBe("'retained'");
    }

    private static UpdateContentItemRequest Request(Seed seed) => new("renamed", "fr", seed.SourceGroup, ["new-tag"]);

    private static async Task<ActionResult> MutateAsync(CmsifyDbContext context, Seed seed, bool link, CancellationToken? token = null)
    {
        var controller = Controller(context);
        return link
            ? (await controller.LinkTranslation(seed.Workspace.Id, seed.Source.Id, new(seed.Target.Id), token ?? Ct)).Result!
            : (await controller.Update(seed.Workspace.Id, seed.Source.Id, Request(seed), token ?? Ct)).Result!;
    }

    private static ContentController Controller(CmsifyDbContext context, bool allowed = true)
    {
        var authorization = Substitute.For<IWorkspaceAuthorizationService>();
        authorization.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(allowed);
        var controller = new ContentController(context, Substitute.For<IContentValidator>(), Substitute.For<IContentSearchVectorBuilder>(),
            Substitute.For<IContentLifecycleService>(), Substitute.For<IContentPublishingService>(), CurrentActorInfo.Anonymous,
            authorization, new EfWebhookOutbox(context))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers.IfMatch = "\"639028224000000000\""; // 2026-01-01 UTC, legacy accepted ETag
        return controller;
    }

    private sealed record VersionSnapshot(string? Slug, string? LocaleCode, Guid? TranslationGroupId, string Tags,
        ContentStatus Status, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt, DateTimeOffset? PublishAt,
        DateTimeOffset? EffectiveStartAt, DateTimeOffset? EffectiveEndAt, DateTimeOffset? ArchivedAt,
        string? PublishLeaseOwner, Guid? PublishLeaseToken, DateTimeOffset? PublishLeaseExpiresAt,
        int? RolledBackFromVersionNumber, uint Token);

    private static async Task<Dictionary<Guid, VersionSnapshot>> SnapshotAsync(DbContextOptions<CmsifyDbContext> options)
    {
        await using var context = new CmsifyDbContext(options);
        return (await context.ContentVersions.ToListAsync(Ct)).ToDictionary(version => version.Id, version => new VersionSnapshot(
            version.Slug, version.LocaleCode, version.TranslationGroupId, string.Join(',', version.Tags), version.Status,
            version.UpdatedAt, version.PublishedAt, version.PublishAt, version.EffectiveStartAt, version.EffectiveEndAt,
            version.ArchivedAt, version.PublishLeaseOwner, version.PublishLeaseToken, version.PublishLeaseExpiresAt,
            version.RolledBackFromVersionNumber, context.Entry(version).Property<uint>("xmin").CurrentValue));
    }

    private static async Task AssertUnchangedAsync(DbContextOptions<CmsifyDbContext> options, Seed seed, Dictionary<Guid, VersionSnapshot> baseline)
    {
        var after = await SnapshotAsync(options);
        foreach (var (id, value) in baseline) after[id].ShouldBe(value);
        await using var context = new CmsifyDbContext(options);
        var source = await context.ContentItems.SingleAsync(item => item.Id == seed.Source.Id, Ct);
        source.Slug.ShouldBe("source");
        source.LocaleCode.ShouldBe("en");
        source.TranslationGroupId.ShouldBe(seed.Source.TranslationGroupId);
        source.UpdatedAt.ShouldBe(seed.Source.UpdatedAt);
        (await context.ContentItems.SingleAsync(item => item.Id == seed.Target.Id, Ct)).TranslationGroupId.ShouldBe(seed.Target.TranslationGroupId);
        (await context.Tags.Where(tag => tag.WorkspaceId == seed.Workspace.Id).Select(tag => tag.Name).ToListAsync(Ct)).ShouldBe(["old-tag"]);
        (await context.ContentItemTags.Where(join => join.ContentItemId == seed.Source.Id).Select(join => join.TagId).ToListAsync(Ct)).ShouldBe([seed.Tag.Id]);
        (await context.WebhookOutboxEvents.CountAsync(Ct)).ShouldBe(0);
    }

    private sealed class BulkFailure(bool cancel, CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public int Calls { get; private set; }
        public bool Disabled { get; set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Disabled || eventData.CommandSource != CommandSource.ExecuteUpdate) return ValueTask.FromResult(result);
            Calls++;
            if (cancel) { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("Injected late bulk failure after real item save");
        }
    }

    private sealed class Counter : DbCommandInterceptor, IMaterializationInterceptor
    {
        public int Commands { get; private set; }
        public int BulkUpdates { get; private set; }
        public int MaterializedVersions { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Commands++; if (eventData.CommandSource == CommandSource.ExecuteUpdate) BulkUpdates++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Commands++; return ValueTask.FromResult(result); }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        { if (entity is ContentVersion) MaterializedVersions++; return entity; }
    }

    private sealed record Seed(Workspace Workspace, ContentItem Source, ContentItem Target, ContentItem Other,
        Tag Tag, Guid SourceGroup, ContentVersion[] Versions)
    {
        public static async Task<Seed> CreateAsync(DbContextOptions<CmsifyDbContext> options, int count = 3, bool nullGroups = false)
        {
            var workspace = new Workspace { Name = "Identity", Slug = $"identity-{Guid.NewGuid():N}" };
            var secondWorkspace = new Workspace { Name = "Other workspace", Slug = $"other-{Guid.NewGuid():N}" };
            var template = new Template { WorkspaceId = workspace.Id, Name = "Article", Slug = "article" };
            var templateVersion = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
            var field = new TemplateField { TemplateVersionId = templateVersion.Id, Key = "title", Label = "Title", PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text };
            var sourceGroup = Guid.NewGuid();
            var source = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id, Slug = "source", LocaleCode = "en", TranslationGroupId = sourceGroup };
            var target = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id, Slug = "target", LocaleCode = "de", TranslationGroupId = Guid.NewGuid() };
            if (nullGroups) source.TranslationGroupId = target.TranslationGroupId = null;
            var other = new ContentItem { WorkspaceId = secondWorkspace.Id, TemplateVersionId = templateVersion.Id, Slug = "other", LocaleCode = "en", TranslationGroupId = sourceGroup };
            var sibling = new ContentItem { WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id, Slug = "sibling", LocaleCode = "es", TranslationGroupId = Guid.NewGuid() };
            source.UpdatedAt = target.UpdatedAt = other.UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var tag = new Tag { WorkspaceId = workspace.Id, Name = "old-tag" };
            source.Tags.Add(new ContentItemTag { ContentItemId = source.Id, TagId = tag.Id });
            var versions = Enumerable.Range(1, count).Select(number => new ContentVersion
            {
                ContentItemId = source.Id, WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id,
                VersionNumber = number, Slug = "source", LocaleCode = "en", TranslationGroupId = source.TranslationGroupId,
                Tags = ["snapshot-tag"], Status = number == 1 ? ContentStatus.Published : number == 2 ? ContentStatus.Archived : ContentStatus.Draft,
                PublishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), PublishAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z"),
                EffectiveStartAt = DateTimeOffset.Parse("2026-03-01T00:00:00Z"), EffectiveEndAt = DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
                ArchivedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z"), PublishLeaseOwner = "lease-owner", PublishLeaseToken = Guid.NewGuid(),
                PublishLeaseExpiresAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z"), RolledBackFromVersionNumber = 1
            }).ToArray();
            var targetVersion = new ContentVersion { ContentItemId = target.Id, WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id, VersionNumber = 1, Slug = "target", LocaleCode = "de", TranslationGroupId = target.TranslationGroupId, Tags = ["target-snapshot"] };
            var otherVersion = new ContentVersion { ContentItemId = other.Id, WorkspaceId = secondWorkspace.Id, TemplateVersionId = templateVersion.Id, VersionNumber = 1, Slug = "other", LocaleCode = "en", TranslationGroupId = sourceGroup, Tags = ["other-snapshot"] };
            var siblingVersion = new ContentVersion { ContentItemId = sibling.Id, WorkspaceId = workspace.Id, TemplateVersionId = templateVersion.Id, VersionNumber = 1, Slug = "sibling", LocaleCode = "es", TranslationGroupId = sibling.TranslationGroupId, Tags = ["sibling-snapshot"] };
            versions[0].FieldValues.Add(new ContentVersionFieldValue { ContentVersionId = versions[0].Id, FieldId = field.Id, TextValue = "Retained field", ValueKind = Cmsify.Core.Domain.Enums.ValueKind.Text });
            await using var context = new CmsifyDbContext(options);
            context.AddRange(workspace, secondWorkspace, template, templateVersion, field, source, target, other, sibling, tag, targetVersion, otherVersion, siblingVersion);
            context.AddRange(versions);
            await context.SaveChangesAsync(Ct);
            return new(workspace, source, target, other, tag, sourceGroup, versions);
        }
    }

    private sealed class Database(DbContextOptions<CmsifyDbContext> options, string? path, PostgreSqlContainer? postgres) : IAsyncDisposable
    {
        public DbContextOptions<CmsifyDbContext> Options { get; } = options;
        public static async Task<Database> CreateAsync(bool usePostgres)
        {
            string? path = null;
            PostgreSqlContainer? postgres = null;
            var builder = new DbContextOptionsBuilder<CmsifyDbContext>();
            if (usePostgres)
            {
                postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("content_identity").WithUsername("cmsify").WithPassword("cmsify").Build();
                await postgres.StartAsync(Ct);
                builder.UseNpgsql(postgres.GetConnectionString()).UseSnakeCaseNamingConvention();
            }
            else
            {
                path = Path.Combine(Path.GetTempPath(), $"cmsify-content-identity-{Guid.NewGuid():N}.db");
                builder.UseSqlite($"Data Source={path};Pooling=False;Default Timeout=15").UseSnakeCaseNamingConvention();
            }
            var database = new Database(builder.Options, path, postgres);
            await using var setup = new CmsifyDbContext(database.Options);
            if (usePostgres) await setup.Database.MigrateAsync(Ct);
            else await setup.Database.EnsureCreatedAsync(Ct);
            return database;
        }
        public async ValueTask DisposeAsync()
        {
            if (postgres is not null) await postgres.DisposeAsync();
            if (path is not null) foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
