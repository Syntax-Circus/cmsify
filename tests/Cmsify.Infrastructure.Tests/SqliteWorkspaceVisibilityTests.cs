using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Tests;

public sealed class SqliteWorkspaceVisibilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Native_SQLite_filters_get_list_and_legacy_mutations_before_execution()
    {
        await using var fixture = new WorkspaceVisibilityFixture(sqlite: true);
        await fixture.InitializeAsync(Ct);
        using var provider = fixture.Provider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        using (fixture.Commands.BeginMeasurement())
        {
            var page = await repo.ListAsync(new(1, 1), Ct);
            page.TotalCount.ShouldBe(2);
            page.Items.Single().Id.ShouldBe(fixture.Rows[3].Id);
        }
        fixture.Commands.CommandCount.ShouldBe(2);
        fixture.Commands.Commands.ShouldAllBe(sql => sql.Contains("WHERE"));
        (await repo.GetAsync(fixture.Rows[0].Id, Ct)).ShouldBeNull();
        (await repo.GetAsync(fixture.Rows[4].Id, Ct)).ShouldBeNull();
        (await repo.UpdateAsync(new(fixture.Rows[1].Id, "Legacy visible", fixture.Rows[1].Slug, null), Ct)).Name.ShouldBe("Legacy visible");
        await Should.ThrowAsync<InvalidOperationException>(() => repo.UpdateAsync(new(fixture.Rows[0].Id, "Hidden", fixture.Rows[0].Slug, null), Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => repo.SoftDeleteAsync(fixture.Rows[0].Id, fixture.Actor.UserId!.Value, Ct));
        await repo.SoftDeleteAsync(fixture.Rows[1].Id, fixture.Actor.UserId!.Value, Ct);
        (await repo.GetAsync(fixture.Rows[1].Id, Ct)).ShouldBeNull();
        fixture.Visibility.Scope = WorkspaceVisibilityScope.None;
        (await repo.ListAsync(new(), Ct)).TotalCount.ShouldBe(0);
        fixture.Visibility.Scope = WorkspaceVisibilityScope.ForWorkspaces([fixture.Rows[3].Id]);
        using var anonymousProvider = fixture.Provider(CurrentActorInfo.Anonymous);
        using var anonymousScope = anonymousProvider.CreateScope();
        (await anonymousScope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().ListAsync(new(), Ct)).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Native_SQLite_host_revision_update_audit_outbox_stale_delete_and_revocation()
    {
        await using var fixture = new WorkspaceVisibilityFixture(sqlite: true);
        await fixture.InitializeAsync(Ct);
        using var provider = fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var before = (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(fixture.Rows[1].Id), Ct)).Value;
        var updated = await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(before.Id, "SQLite host", before.Slug, null, before.Revision), Ct);
        updated.IsSuccess.ShouldBeTrue();
        updated.Value.Revision.ShouldBeGreaterThan(before.Revision);
        var mutations = services.GetRequiredService<IWorkspaceMutationRepository>();
        (await mutations.UpdateAsync(new(before.Id, "Stale", before.Slug, null), before.Revision, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        (await mutations.DeleteAsync(before.Id, before.Revision, fixture.Actor.UserId!.Value, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        fixture.Visibility.Scope = WorkspaceVisibilityScope.None;
        (await mutations.UpdateAsync(new(before.Id, "Revoked", before.Slug, null), updated.Value.Revision, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        fixture.Visibility.Scope = WorkspaceVisibilityScope.ForWorkspaces([before.Id]);
        (await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(before.Id, updated.Value.Revision), Ct)).IsSuccess.ShouldBeTrue();
        using var verify = provider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stored = await db.Workspaces.IgnoreQueryFilters().SingleAsync(row => row.Id == before.Id, Ct);
        stored.IsDeleted.ShouldBeTrue();
        stored.Name.ShouldBe("SQLite host");
        stored.DeletedByUserId.ShouldBe(fixture.Actor.UserId);
        var audit = await db.AuditLogs.Where(row => row.EntityId == before.Id).ToListAsync(Ct);
        audit.Count.ShouldBe(3);
        audit.ShouldAllBe(row => row.ActorUserId == fixture.Actor.UserId);
        (await db.WebhookOutboxEvents.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(1);
        (await db.Users.CountAsync(Ct)).ShouldBe(0);
        (await db.UserSessions.CountAsync(Ct)).ShouldBe(0);
        (await db.UserWorkspaceAccesses.CountAsync(Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData(true, UserRole.Reader)]
    [InlineData(true, UserRole.Editor)]
    [InlineData(true, UserRole.Admin)]
    [InlineData(false, UserRole.Reader)]
    [InlineData(false, UserRole.Editor)]
    [InlineData(false, UserRole.Admin)]
    public async Task Default_CmsManaged_keeps_membership_SQL_API_client_SuperAdmin_and_anonymous(bool sqlite, UserRole role)
    {
        await using var fixture = new WorkspaceVisibilityFixture(sqlite);
        await fixture.InitializeAsync(Ct);
        using (var seedProvider = fixture.Provider())
        using (var seed = seedProvider.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            db.Users.Add(new User { Id = fixture.Actor.UserId!.Value, Email = "test@example.invalid", DisplayName = "Membership fixture", PasswordHash = "not-a-credential", Role = role });
            db.UserWorkspaceAccesses.Add(new() { UserId = fixture.Actor.UserId.Value, WorkspaceId = fixture.Rows[0].Id, AccessLevel = WorkspaceAccessLevel.Read });
            await db.SaveChangesAsync(Ct);
        }
        var membershipActor = new CurrentActorInfo(fixture.Actor.UserId, null, role, null, true);
        using (var memberProvider = fixture.Provider(membershipActor, defaultVisibility: true))
        using (var member = memberProvider.CreateScope())
        {
            using (fixture.Commands.BeginMeasurement())
            {
                var page = await member.ServiceProvider.GetRequiredService<IWorkspaceRepository>().ListAsync(new(), Ct);
                page.TotalCount.ShouldBe(1);
                page.Items.Single().Id.ShouldBe(fixture.Rows[0].Id);
            }
            fixture.Commands.Commands.ShouldAllBe(sql => sql.Contains("user_workspace_accesses") && sql.Contains("EXISTS"));
        }
        foreach (var actor in new[]
        {
            new CurrentActorInfo(null, Guid.NewGuid(), role, fixture.Rows[2].Id, true),
            new CurrentActorInfo(fixture.Actor.UserId, null, role, null, true, true),
            CurrentActorInfo.Anonymous
        })
        {
            using var defaultProvider = fixture.Provider(actor, defaultVisibility: true);
            using var operation = defaultProvider.CreateScope();
            var page = await operation.ServiceProvider.GetRequiredService<IWorkspaceRepository>().ListAsync(new(), Ct);
            page.TotalCount.ShouldBe(actor.IsSuperAdmin ? 4 : actor.IsAuthenticated ? 1 : 0);
            page.Items.ShouldNotContain(row => row.Id == fixture.Rows[4].Id);
        }
    }
}
