using System.Text;
using System.Text.Json;
using System.Data.Common;
using Cmsify.Api.Controllers;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using SyntaxCircus.Cmsify.Contracts;
using Testcontainers.PostgreSql;
using TemplateVersionStatus = Cmsify.Core.Domain.Enums.TemplateVersionStatus;

namespace Cmsify.Api.Integration.Tests;

// Scoped controller fixtures execute production methods with independent real database
// contexts. SQLite uses EnsureCreated, not production host registration or migrations.
public sealed class TemplateArchivalControllerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ArchiveThroughController_RejectsStaleSaveAndPreservesUnrelatedRows(bool import, bool postgres)
    {
        await using var database = await ControllerDatabase.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        await using var staleContext = new CmsifyDbContext(database.Options);
        var stale = await staleContext.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct);
        var originalToken = staleContext.Entry(stale).Property<uint>("xmin").CurrentValue;
        var unaffected = await staleContext.TemplateVersions
            .Where(version => version.Id == seed.Archived.Id || version.Id == seed.OtherPublished.Id || (import && version.Id == seed.Draft.Id))
            .Select(version => new { version.Id, Token = EF.Property<uint>(version, "xmin") })
            .ToDictionaryAsync(version => version.Id, version => version.Token, Ct);

        await using (var writer = new CmsifyDbContext(database.Options))
        {
            if (import)
                (await ImportAsync(writer, seed, Manifest(seed))).Result.ShouldBeOfType<OkObjectResult>();
            else
                (await PublishAsync(writer, seed)).Result.ShouldBeOfType<OkObjectResult>();
        }

        // Attempt the actual stale overwrite before asserting tokens. The broken
        // status-only bulk update allows this save to resurrect a published row.
        staleContext.Entry(stale).Property(version => version.Status).IsModified = true;
        stale.Notes = "Stale editor overwrite";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync(Ct));

        await using var verification = new CmsifyDbContext(database.Options);
        var archived = await verification.TemplateVersions.SingleAsync(version => version.Id == seed.Published.Id, Ct);
        archived.Status.ShouldBe(TemplateVersionStatus.Archived);
        archived.Notes.ShouldBeNull();
        var token = verification.Entry(archived).Property<uint>("xmin").CurrentValue;
        if (postgres) token.ShouldNotBe(originalToken);
        else token.ShouldBe(originalToken + 1u);
        var second = await verification.TemplateVersions.SingleAsync(version => version.Id == seed.SecondPublished.Id, Ct);
        second.Status.ShouldBe(TemplateVersionStatus.Archived);
        if (!postgres) verification.Entry(second).Property<uint>("xmin").CurrentValue.ShouldBe(2u);
        foreach (var (id, expectedToken) in unaffected)
        {
            var row = await verification.TemplateVersions.SingleAsync(version => version.Id == id, Ct);
            verification.Entry(row).Property<uint>("xmin").CurrentValue.ShouldBe(expectedToken);
        }
        var template = await verification.Templates.SingleAsync(template => template.Id == seed.Template.Id, Ct);
        var current = await verification.TemplateVersions.SingleAsync(version => version.Id == template.CurrentVersionId, Ct);
        current.Status.ShouldBe(TemplateVersionStatus.Published);
        current.PublishedAt.ShouldNotBeNull();
        current.VersionNumber.ShouldBe(import ? 5 : 4);
        if (import)
        {
            (await verification.Templates.SingleAsync(template => template.WorkspaceId == seed.Workspace.Id && template.Slug == "fresh", Ct)).CurrentVersionId.ShouldNotBeNull();
            (await verification.PickLists.SingleAsync(list => list.WorkspaceId == seed.Workspace.Id, Ct)).CurrentRevisionId.ShouldNotBeNull();
            (await verification.Components.SingleAsync(component => component.WorkspaceId == seed.Workspace.Id, Ct)).CurrentVersionId.ShouldNotBeNull();
            (await verification.WebhookOutboxEvents.CountAsync(item => item.WorkspaceId == seed.Workspace.Id, Ct)).ShouldBe(0);
        }
        else
        {
            template.CurrentVersionId.ShouldBe(seed.Draft.Id);
            var outbox = await verification.WebhookOutboxEvents.SingleAsync(item => item.WorkspaceId == seed.Workspace.Id, Ct);
            outbox.EventType.ShouldBe("template.version_published");
            outbox.EntityId.ShouldBe(seed.Draft.Id);
            outbox.Payload.GetProperty("templateId").GetGuid().ShouldBe(seed.Template.Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_ImportExhaustionRollsBackRelatedSavesAndEarlierArchives(bool laterTemplate)
    {
        await using var database = await ControllerDatabase.CreateAsync(false);
        var seed = await Seed.CreateAsync(database.Options);
        await using (var setup = new CmsifyDbContext(database.Options))
        {
            var exhaustedId = laterTemplate ? seed.OtherPublished.Id : seed.SecondPublished.Id;
            await setup.TemplateVersions.Where(version => version.Id == exhaustedId)
                .ExecuteUpdateAsync(updates => updates.SetProperty(version => EF.Property<uint>(version, "xmin"), uint.MaxValue), Ct);
        }
        var baseline = await SnapshotAsync(database.Options);
        var manifest = Manifest(seed);
        if (laterTemplate) manifest = manifest with { Templates = [manifest.Templates[0], new(seed.OtherTemplate.Slug, "Other import", null, [], [])] };
        await using (var writer = new CmsifyDbContext(database.Options))
            await Should.ThrowAsync<OverflowException>(() => ImportAsync(writer, seed, manifest));
        await AssertUnchangedAsync(database.Options, seed, baseline);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Sqlite_PublishExhaustionRollsBackEntirePublication(bool trackedFailure, bool exhaustTemplate)
    {
        await using var database = await ControllerDatabase.CreateAsync(false);
        var seed = await Seed.CreateAsync(database.Options);
        await using (var setup = new CmsifyDbContext(database.Options))
        {
            if (trackedFailure && exhaustTemplate)
                await setup.Templates.Where(template => template.Id == seed.Template.Id)
                    .ExecuteUpdateAsync(updates => updates.SetProperty(template => EF.Property<uint>(template, "xmin"), uint.MaxValue), Ct);
            else
            {
                var id = trackedFailure ? seed.Draft.Id : seed.SecondPublished.Id;
                await setup.TemplateVersions.Where(version => version.Id == id)
                    .ExecuteUpdateAsync(updates => updates.SetProperty(version => EF.Property<uint>(version, "xmin"), uint.MaxValue), Ct);
            }
        }
        var baseline = await SnapshotAsync(database.Options);
        await using (var writer = new CmsifyDbContext(database.Options))
            await Should.ThrowAsync<OverflowException>(() => PublishAsync(writer, seed));
        await AssertUnchangedAsync(database.Options, seed, baseline);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Import_FinalSaveFailureRollsBackNewVersionsAndEarlierRelatedSaves(bool postgres, int failure)
    {
        await using var database = await ControllerDatabase.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var interceptor = new FinalImportSaveFailure(seed, failure, cancellation);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>(database.Options).AddInterceptors(interceptor).Options;
        await using (var writer = new CmsifyDbContext(options))
        {
            if (failure == FinalImportSaveFailure.Cancellation)
                await Should.ThrowAsync<OperationCanceledException>(() => ImportAsync(writer, seed, Manifest(seed), cancellationToken: cancellation.Token));
            else if (failure == FinalImportSaveFailure.Concurrency)
                await Should.ThrowAsync<DbUpdateConcurrencyException>(() => ImportAsync(writer, seed, Manifest(seed)));
            else
                await Should.ThrowAsync<DbUpdateException>(() => ImportAsync(writer, seed, Manifest(seed)));
        }
        interceptor.Calls.ShouldBe(4);
        await AssertUnchangedAsync(database.Options, seed, baseline);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Controller_CallerTransactionOwnsSuccessfulCommitAndRollback(bool import, bool postgres)
    {
        await using var database = await ControllerDatabase.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        await using var writer = new CmsifyDbContext(database.Options);
        await using var caller = await writer.Database.BeginTransactionAsync(Ct);
        if (import) (await ImportAsync(writer, seed, Manifest(seed))).Result.ShouldBeOfType<OkObjectResult>();
        else (await PublishAsync(writer, seed)).Result.ShouldBeOfType<OkObjectResult>();
        writer.Database.CurrentTransaction.ShouldBeSameAs(caller);
        (await writer.TemplateVersions.AsNoTracking().SingleAsync(version => version.Id == seed.Published.Id, Ct)).Status.ShouldBe(TemplateVersionStatus.Archived);
        await AssertUnchangedAsync(database.Options, seed, baseline);
        await caller.RollbackAsync(Ct);
        await AssertUnchangedAsync(database.Options, seed, baseline);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Import_CallerTransactionFailurePreservesEarlierUnrelatedWrites(bool postgres, int failure)
    {
        await using var database = await ControllerDatabase.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var baseline = await SnapshotAsync(database.Options);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var interceptor = new FinalImportSaveFailure(seed, failure, cancellation);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>(database.Options).AddInterceptors(interceptor).Options;
        await using var writer = new CmsifyDbContext(options);
        await using var caller = await writer.Database.BeginTransactionAsync(Ct);
        var unrelated = new Workspace { Name = "Caller work", Slug = $"caller-{Guid.NewGuid():N}" };
        writer.Workspaces.Add(unrelated);
        await writer.SaveChangesAsync(Ct);
        var callerOriginalToken = writer.Entry(unrelated).Property<uint>("xmin").OriginalValue;
        unrelated.Name = "Caller pending update";
        var pendingCaller = new Workspace { Name = "Pending caller work", Slug = $"pending-{Guid.NewGuid():N}" };
        writer.Workspaces.Add(pendingCaller);
        var priorTemplate = await writer.Templates.Include(template => template.Versions)
            .SingleAsync(template => template.Id == seed.Template.Id, Ct);
        interceptor.Reset();
        if (failure == FinalImportSaveFailure.Cancellation)
            await Should.ThrowAsync<OperationCanceledException>(() => ImportAsync(writer, seed, Manifest(seed), cancellationToken: cancellation.Token));
        else if (failure == FinalImportSaveFailure.Concurrency)
            await Should.ThrowAsync<DbUpdateConcurrencyException>(() => ImportAsync(writer, seed, Manifest(seed)));
        else
            await Should.ThrowAsync<DbUpdateException>(() => ImportAsync(writer, seed, Manifest(seed)));
        writer.Database.CurrentTransaction.ShouldBeSameAs(caller);
        (await writer.Workspaces.AsNoTracking().AnyAsync(workspace => workspace.Id == unrelated.Id, Ct)).ShouldBeTrue();
        (await writer.TemplateVersions.AsNoTracking().SingleAsync(version => version.Id == seed.Published.Id, Ct)).Status.ShouldBe(TemplateVersionStatus.Published);
        (await writer.PickLists.AsNoTracking().CountAsync(list => list.WorkspaceId == seed.Workspace.Id, Ct)).ShouldBe(0);
        writer.Entry(pendingCaller).State.ShouldBe(EntityState.Added);
        writer.Entry(unrelated).State.ShouldBe(EntityState.Modified);
        unrelated.Name.ShouldBe("Caller pending update");
        writer.Entry(unrelated).Property<uint>("xmin").OriginalValue.ShouldBe(callerOriginalToken);
        priorTemplate.Name.ShouldBe("Profile");
        priorTemplate.Versions.Count.ShouldBe(4);
        interceptor.Disabled = true;
        unrelated.Name = "Caller saved twice";
        await writer.SaveChangesAsync(Ct);
        await caller.CommitAsync(Ct);
        await AssertUnchangedAsync(database.Options, seed, baseline);
        await using var verification = new CmsifyDbContext(database.Options);
        (await verification.Workspaces.AnyAsync(workspace => workspace.Id == unrelated.Id, Ct)).ShouldBeTrue();
        (await verification.Workspaces.SingleAsync(workspace => workspace.Id == unrelated.Id, Ct)).Name.ShouldBe("Caller saved twice");
        (await verification.Workspaces.AnyAsync(workspace => workspace.Id == pendingCaller.Id, Ct)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_FailureRestoresExistingRelatedGraphsAndPendingCallerChanges(bool postgres)
    {
        await using var database = await ControllerDatabase.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        var picklist = new PickList { WorkspaceId = seed.Workspace.Id, Name = "Original choices", Slug = "choices" };
        var option = new PickListOption { PickListId = picklist.Id, Label = "Old", Value = "old" };
        picklist.Options.Add(option);
        var revision = new PickListRevision { PickListId = picklist.Id, VersionNumber = 1 };
        revision.Options.Add(new PickListRevisionOption { PickListRevisionId = revision.Id, Label = "Old", Value = "old" });
        var component = new ComponentDefinition { WorkspaceId = seed.Workspace.Id, Name = "Original reusable", Slug = "reusable" };
        var componentVersion = new ComponentVersion { ComponentId = component.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        componentVersion.Fields.Add(new ComponentField { ComponentVersionId = componentVersion.Id, Key = "title", Label = "Old title", PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text });
        await using (var setup = new CmsifyDbContext(database.Options))
        {
            setup.AddRange(picklist, revision, component, componentVersion);
            await setup.SaveChangesAsync(Ct);
            picklist.CurrentRevisionId = revision.Id;
            component.CurrentVersionId = componentVersion.Id;
            await setup.SaveChangesAsync(Ct);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var interceptor = new FinalImportSaveFailure(seed, FinalImportSaveFailure.Constraint, cancellation);
        var options = new DbContextOptionsBuilder<CmsifyDbContext>(database.Options).AddInterceptors(interceptor).Options;
        await using var writer = new CmsifyDbContext(options);
        await using var caller = await writer.Database.BeginTransactionAsync(Ct);
        var existingList = await writer.PickLists.Include(list => list.Options).SingleAsync(Ct);
        var existingComponent = await writer.Components.Include(item => item.Versions).ThenInclude(version => version.Fields).SingleAsync(Ct);
        var existingTemplate = await writer.Templates.Include(template => template.Versions).SingleAsync(template => template.Id == seed.Template.Id, Ct);
        existingList.Description = "Caller choices description";
        existingComponent.Description = "Caller component description";
        existingTemplate.Description = "Caller template description";
        var originalListToken = writer.Entry(existingList).Property<uint>("xmin").OriginalValue;
        var originalComponentToken = writer.Entry(existingComponent).Property<uint>("xmin").OriginalValue;
        var originalTemplateToken = writer.Entry(existingTemplate).Property<uint>("xmin").OriginalValue;
        var resolutions = new PackageImportResolutionsRequest(new Dictionary<string, string> { ["choices"] = PackagePickListResolution.Replace },
            new Dictionary<string, string> { ["reusable"] = PackageComponentResolution.Replace });
        await Should.ThrowAsync<DbUpdateException>(() => ImportAsync(writer, seed, Manifest(seed), resolutions));
        existingList.Options.Select(item => item.Id).ShouldBe([option.Id]);
        existingList.CurrentRevisionId.ShouldBe(revision.Id);
        existingList.Name.ShouldBe("Original choices");
        existingList.Description.ShouldBe("Caller choices description");
        existingComponent.Versions.Select(item => item.Id).ShouldBe([componentVersion.Id]);
        existingComponent.Versions.Single().Status.ShouldBe(TemplateVersionStatus.Published);
        existingComponent.Versions.Single().Fields.Single().Label.ShouldBe("Old title");
        existingComponent.CurrentVersionId.ShouldBe(componentVersion.Id);
        existingComponent.Description.ShouldBe("Caller component description");
        existingTemplate.Versions.Count.ShouldBe(4);
        existingTemplate.CurrentVersionId.ShouldBe(seed.Published.Id);
        existingTemplate.Description.ShouldBe("Caller template description");
        writer.Entry(existingList).Property<uint>("xmin").OriginalValue.ShouldBe(originalListToken);
        writer.Entry(existingComponent).Property<uint>("xmin").OriginalValue.ShouldBe(originalComponentToken);
        writer.Entry(existingTemplate).Property<uint>("xmin").OriginalValue.ShouldBe(originalTemplateToken);
        interceptor.Disabled = true;
        await writer.SaveChangesAsync(Ct);
        await caller.CommitAsync(Ct);
        await using var verification = new CmsifyDbContext(database.Options);
        var retainedList = await verification.PickLists.Include(list => list.Options).SingleAsync(Ct);
        retainedList.Options.Select(item => item.Id).ShouldBe([option.Id]);
        retainedList.Description.ShouldBe("Caller choices description");
        retainedList.CurrentRevisionId.ShouldBe(revision.Id);
        (await verification.PickListRevisions.CountAsync(Ct)).ShouldBe(1);
        var retainedComponent = await verification.Components.Include(item => item.Versions).SingleAsync(Ct);
        retainedComponent.Versions.Count.ShouldBe(1);
        retainedComponent.CurrentVersionId.ShouldBe(componentVersion.Id);
        retainedComponent.Description.ShouldBe("Caller component description");
        (await verification.TemplateVersions.CountAsync(Ct)).ShouldBe(5);
        (await verification.Templates.SingleAsync(template => template.Id == seed.Template.Id, Ct)).Description.ShouldBe("Caller template description");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_ArchivalKeepsBulkCommandAndMaterializationBounds(bool import)
    {
        const int additionalPublishedVersions = 32;
        await using var database = await ControllerDatabase.CreateAsync(false);
        var seed = await Seed.CreateAsync(database.Options);
        await using (var setup = new CmsifyDbContext(database.Options))
        {
            setup.TemplateVersions.AddRange(Enumerable.Range(5, additionalPublishedVersions).Select(number =>
                new TemplateVersion { TemplateId = seed.Template.Id, VersionNumber = number, Status = TemplateVersionStatus.Published }));
            await setup.SaveChangesAsync(Ct);
        }
        var counter = new ArchivalCounter();
        var options = new DbContextOptionsBuilder<CmsifyDbContext>(database.Options).AddInterceptors(counter).Options;
        await using (var writer = new CmsifyDbContext(options))
        {
            if (import) (await ImportAsync(writer, seed, Manifest(seed))).Result.ShouldBeOfType<OkObjectResult>();
            else (await PublishAsync(writer, seed)).Result.ShouldBeOfType<OkObjectResult>();
        }
        counter.BulkUpdates.ShouldBe(import ? 2 : 1);
        counter.MaterializedVersions.ShouldBe(import ? 0 : 1);
        await using var verification = new CmsifyDbContext(database.Options);
        (await verification.TemplateVersions.CountAsync(version => version.TemplateId == seed.Template.Id
            && version.Status == TemplateVersionStatus.Archived && EF.Property<uint>(version, "xmin") == 2u, Ct)).ShouldBe(34);
        verification.Entry(await verification.TemplateVersions.SingleAsync(version => version.Id == seed.Archived.Id, Ct))
            .Property<uint>("xmin").CurrentValue.ShouldBe(1u);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Import_ComponentConflictRollsBackEarlierPicklistSave(bool postgres, bool invalidResolution)
    {
        await using var database = await ControllerDatabase.CreateAsync(postgres);
        var seed = await Seed.CreateAsync(database.Options);
        await using (var setup = new CmsifyDbContext(database.Options))
        {
            var component = new ComponentDefinition { WorkspaceId = seed.Workspace.Id, Name = "Existing", Slug = "reusable" };
            var version = new ComponentVersion { ComponentId = component.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
            version.Fields.Add(new ComponentField { ComponentVersionId = version.Id, Key = "title", Label = "Title", PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType.Text });
            setup.AddRange(component, version);
            await setup.SaveChangesAsync(Ct);
        }
        await using var writer = new CmsifyDbContext(database.Options);
        var resolutions = invalidResolution ? new PackageImportResolutionsRequest(null, new Dictionary<string, string> { ["reusable"] = "invalid" }) : null;
        var result = (await ImportAsync(writer, seed, Manifest(seed), resolutions)).Result.ShouldBeOfType<ObjectResult>();
        result.StatusCode.ShouldBe(invalidResolution ? StatusCodes.Status400BadRequest : StatusCodes.Status409Conflict);
        await using var verification = new CmsifyDbContext(database.Options);
        (await verification.PickLists.CountAsync(list => list.WorkspaceId == seed.Workspace.Id, Ct)).ShouldBe(0);
        (await verification.PickListRevisions.CountAsync(Ct)).ShouldBe(0);
        (await verification.TemplateVersions.CountAsync(Ct)).ShouldBe(5);
        (await verification.Components.CountAsync(Ct)).ShouldBe(1);
    }

    private static async Task<Dictionary<Guid, uint>> SnapshotAsync(DbContextOptions<CmsifyDbContext> options)
    {
        await using var context = new CmsifyDbContext(options);
        return await context.TemplateVersions.Select(version => new { version.Id, Token = EF.Property<uint>(version, "xmin") })
            .ToDictionaryAsync(version => version.Id, version => version.Token, Ct);
    }

    private static async Task AssertUnchangedAsync(DbContextOptions<CmsifyDbContext> options, Seed seed, Dictionary<Guid, uint> baseline)
    {
        await using var context = new CmsifyDbContext(options);
        var versions = await context.TemplateVersions.ToDictionaryAsync(version => version.Id, Ct);
        versions.Count.ShouldBe(5);
        versions[seed.Published.Id].Status.ShouldBe(TemplateVersionStatus.Published);
        versions[seed.SecondPublished.Id].Status.ShouldBe(TemplateVersionStatus.Published);
        versions[seed.Archived.Id].Status.ShouldBe(TemplateVersionStatus.Archived);
        versions[seed.Draft.Id].Status.ShouldBe(TemplateVersionStatus.Draft);
        versions[seed.Draft.Id].PublishedAt.ShouldBeNull();
        versions[seed.OtherPublished.Id].Status.ShouldBe(TemplateVersionStatus.Published);
        foreach (var (id, token) in baseline) context.Entry(versions[id]).Property<uint>("xmin").CurrentValue.ShouldBe(token);
        var template = await context.Templates.SingleAsync(template => template.Id == seed.Template.Id, Ct);
        template.CurrentVersionId.ShouldBe(seed.Published.Id);
        template.Name.ShouldBe("Profile");
        template.PackageId.ShouldBeNull();
        (await context.Templates.CountAsync(template => template.WorkspaceId == seed.Workspace.Id, Ct)).ShouldBe(2);
        (await context.PickLists.CountAsync(list => list.WorkspaceId == seed.Workspace.Id, Ct)).ShouldBe(0);
        (await context.PickListRevisions.CountAsync(Ct)).ShouldBe(0);
        (await context.Components.CountAsync(component => component.WorkspaceId == seed.Workspace.Id, Ct)).ShouldBe(0);
        (await context.WebhookOutboxEvents.CountAsync(item => item.WorkspaceId == seed.Workspace.Id, Ct)).ShouldBe(0);
    }

    private static Task<ActionResult<TemplateVersionResponse>> PublishAsync(CmsifyDbContext context, Seed seed)
    {
        var controller = new TemplatesController(context, CurrentActorInfo.Anonymous, Authorization(),
            Substitute.For<IFieldConfigValidator>(), new EfWebhookOutbox(context));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller.Publish(seed.Workspace.Id, seed.Template.Id, seed.Draft.VersionNumber, Ct);
    }

    private static Task<ActionResult<PackageImportResponse>> ImportAsync(CmsifyDbContext context, Seed seed,
        CtpPackageManifest manifest, PackageImportResolutionsRequest? resolutions = null, CancellationToken? cancellationToken = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { manifest, resolutions }, CmsifyJsonOptions.Create())));
        var controller = new PackagesController(context, Substitute.For<IWebHostEnvironment>(), Authorization())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return controller.Import(seed.Workspace.Id, cancellationToken ?? Ct);
    }

    private static IWorkspaceAuthorizationService Authorization()
    {
        var authorization = Substitute.For<IWorkspaceAuthorizationService>();
        authorization.CanWriteWorkspaceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return authorization;
    }

    private static CtpPackageManifest Manifest(Seed seed) => new("1.1", "tests", "archival", "1.0.0", "Archival", null, null, null, null,
        [new(seed.Template.Slug, "Imported profile", null, [], []), new("fresh", "Fresh", null, [], [])],
        [new("choices", "Choices", null, [new("One", "one", 0)])],
        [new("reusable", "Reusable", null, [])]);

    // Fault injection stays in test infrastructure. It occurs only at the fourth
    // (final pointer) save, after real related rows and template versions were saved.
    private sealed class FinalImportSaveFailure(Seed seed, int failure, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public const int Concurrency = 0;
        public const int Constraint = 1;
        public const int Cancellation = 2;
        public int Calls { get; private set; }
        public bool Disabled { get; set; }
        public void Reset() => Calls = 0;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Disabled || ++Calls != 4) return ValueTask.FromResult(result);
            if (failure == Concurrency) throw new DbUpdateConcurrencyException("Injected final-save conflict");
            if (failure == Cancellation)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            else if (failure == Constraint)
            {
                var template = eventData.Context!.ChangeTracker.Entries<Template>().Single(entry => entry.Entity.Id == seed.Template.Id);
                template.Entity.Slug = seed.OtherTemplate.Slug;
                template.Property(item => item.Slug).IsModified = true;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ArchivalCounter : DbCommandInterceptor, IMaterializationInterceptor
    {
        public int BulkUpdates { get; private set; }
        public int MaterializedVersions { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.CommandSource == CommandSource.ExecuteUpdate) BulkUpdates++;
            return ValueTask.FromResult(result);
        }
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is TemplateVersion) MaterializedVersions++;
            return entity;
        }
    }

    private sealed record Seed(Workspace Workspace, Template Template, Template OtherTemplate,
        TemplateVersion Published, TemplateVersion SecondPublished, TemplateVersion Archived,
        TemplateVersion Draft, TemplateVersion OtherPublished)
    {
        public static async Task<Seed> CreateAsync(DbContextOptions<CmsifyDbContext> options)
        {
            var workspace = new Workspace { Name = "Controller archival", Slug = $"controller-{Guid.NewGuid():N}" };
            var template = new Template { WorkspaceId = workspace.Id, Name = "Profile", Slug = "profile" };
            var other = new Template { WorkspaceId = workspace.Id, Name = "Other", Slug = "other" };
            var seed = new Seed(workspace, template, other,
                new() { TemplateId = template.Id, VersionNumber = 2, Status = TemplateVersionStatus.Published },
                new() { TemplateId = template.Id, VersionNumber = 3, Status = TemplateVersionStatus.Published },
                new() { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Archived },
                new() { TemplateId = template.Id, VersionNumber = 4, Status = TemplateVersionStatus.Draft },
                new() { TemplateId = other.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published });
            await using var context = new CmsifyDbContext(options);
            context.AddRange(workspace, template, other, seed.Published, seed.SecondPublished, seed.Archived, seed.Draft, seed.OtherPublished);
            await context.SaveChangesAsync(Ct);
            template.CurrentVersionId = seed.Published.Id;
            other.CurrentVersionId = seed.OtherPublished.Id;
            await context.SaveChangesAsync(Ct);
            return seed;
        }
    }

    private sealed class ControllerDatabase(DbContextOptions<CmsifyDbContext> options, string? path, PostgreSqlContainer? postgres) : IAsyncDisposable
    {
        public DbContextOptions<CmsifyDbContext> Options { get; } = options;

        public static async Task<ControllerDatabase> CreateAsync(bool usePostgres)
        {
            string? path = null;
            PostgreSqlContainer? postgres = null;
            var builder = new DbContextOptionsBuilder<CmsifyDbContext>();
            if (usePostgres)
            {
                postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("controller_archival")
                    .WithUsername("cmsify").WithPassword("cmsify").Build();
                await postgres.StartAsync(Ct);
                builder.UseNpgsql(postgres.GetConnectionString()).UseSnakeCaseNamingConvention();
            }
            else
            {
                path = Path.Combine(Path.GetTempPath(), $"cmsify-controller-archival-{Guid.NewGuid():N}.db");
                builder.UseSqlite($"Data Source={path};Pooling=False;Default Timeout=15").UseSnakeCaseNamingConvention();
            }
            var database = new ControllerDatabase(builder.Options, path, postgres);
            await using var context = new CmsifyDbContext(database.Options);
            if (usePostgres) await context.Database.MigrateAsync(Ct);
            else await context.Database.EnsureCreatedAsync(Ct);
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
