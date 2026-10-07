using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SyntaxCircus.Common;

namespace Cmsify.Infrastructure.Tests;

public sealed class WorkspaceVisibilityIntegrationTests : IAsyncLifetime
{
    private readonly WorkspaceVisibilityFixture _fixture = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public async ValueTask InitializeAsync() => await _fixture.InitializeAsync(Ct);
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Host_only_actor_gets_visible_workspace_without_CMS_access_rows()
    {
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var result = await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe(_fixture.Rows[1].Id);
        var db = services.GetRequiredService<CmsifyDbContext>();
        (await db.Users.CountAsync(Ct)).ShouldBe(0);
        (await db.UserSessions.CountAsync(Ct)).ShouldBe(0);
        (await db.UserWorkspaceAccesses.CountAsync(Ct)).ShouldBe(0);
        (await db.ApiClients.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Restricted_scope_overrides_privileged_actor()
    {
        var actor = new CurrentActorInfo(_fixture.Actor.UserId, null, UserRole.Admin, _fixture.Rows[0].Id, true, true);
        using var provider = _fixture.Provider(actor);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        (await repo.GetAsync(_fixture.Rows[0].Id, Ct)).ShouldBeNull();
        (await repo.GetAsync(_fixture.Rows[1].Id, Ct)).ShouldNotBeNull();
        (await repo.GetAsync(_fixture.Rows[4].Id, Ct)).ShouldBeNull();
        (await repo.GetAsync(Guid.Empty, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task List_count_and_pages_filter_before_paging()
    {
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var before = _fixture.Visibility.Resolutions;
        using (_fixture.Commands.BeginMeasurement())
        {
            var first = await repo.ListAsync(new(0, 1), Ct);
            first.TotalCount.ShouldBe(2);
            first.Items.Single().Id.ShouldBe(_fixture.Rows[1].Id);
        }
        _fixture.Commands.CommandCount.ShouldBe(2);
        _fixture.Commands.Commands.ShouldAllBe(sql => sql.Contains("WHERE"));
        _fixture.Commands.Commands[1].ShouldContain("LIMIT");
        _fixture.Visibility.Resolutions.ShouldBe(before + 1);
        var second = await repo.ListAsync(new(1, 1), Ct);
        second.TotalCount.ShouldBe(2);
        second.Items.Single().Id.ShouldBe(_fixture.Rows[3].Id);
        var beyond = await repo.ListAsync(new(2, 1), Ct);
        beyond.TotalCount.ShouldBe(2);
        beyond.Items.ShouldBeEmpty();
        using (_fixture.Commands.BeginMeasurement()) (await repo.GetAsync(_fixture.Rows[1].Id, Ct)).ShouldNotBeNull();
        _fixture.Commands.CommandCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Empty_scope_returns_no_rows_and_anonymous_IDs_are_ineffective(bool anonymous)
    {
        if (!anonymous) _fixture.Visibility.Scope = WorkspaceVisibilityScope.None;
        using var provider = _fixture.Provider(anonymous ? CurrentActorInfo.Anonymous : null);
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        (await repo.GetAsync(_fixture.Rows[1].Id, Ct)).ShouldBeNull();
        (await repo.ListAsync(new(), Ct)).TotalCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Provider_failure_never_falls_back(bool nullDecision)
    {
        if (nullDecision) _fixture.Visibility.Scope = null!;
        else _fixture.Visibility.Failure = new InvalidOperationException("Test provider failed.");
        using var provider = _fixture.Provider(new(_fixture.Actor.UserId, null, UserRole.Admin, null, true, true));
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        using (_fixture.Commands.BeginMeasurement())
        {
            await Should.ThrowAsync<InvalidOperationException>(() => repo.GetAsync(_fixture.Rows[1].Id, Ct));
            await Should.ThrowAsync<InvalidOperationException>(() => repo.ListAsync(new(), Ct));
        }
        _fixture.Commands.CommandCount.ShouldBe(0);
    }

    [Fact]
    public async Task Capability_and_visibility_denials_keep_missing_outcomes()
    {
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var get = services.GetRequiredService<IWorkspacesGetRequestHandler>();
        var invisible = await get.HandleAsync(new(_fixture.Rows[0].Id), Ct);
        var missing = await get.HandleAsync(new(Guid.NewGuid()), Ct);
        invisible.Errors.ShouldBe(missing.Errors);
        _fixture.Capabilities.Read = false;
        (await get.HandleAsync(new(_fixture.Rows[1].Id), Ct)).Errors.ShouldBe(missing.Errors);
        var update = await services.GetRequiredService<IWorkspacesUpdateRequestHandler>()
            .HandleAsync(new(_fixture.Rows[0].Id, "Invisible", _fixture.Rows[0].Slug, null, _fixture.Rows[0].UpdatedAt.UtcTicks), Ct);
        update.Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
    }

    [Fact]
    public async Task Host_only_Admin_update_commits_workspace_audit_and_outbox()
    {
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var before = (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct)).Value;
        var update = await services.GetRequiredService<IWorkspacesUpdateRequestHandler>()
            .HandleAsync(new(before.Id, "Host updated", before.Slug, null, before.Revision), Ct);
        update.IsSuccess.ShouldBeTrue();
        update.Value.Revision.ShouldBeGreaterThan(before.Revision);
        using var verify = provider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await db.Workspaces.SingleAsync(row => row.Id == before.Id, Ct)).Name.ShouldBe("Host updated");
        var audit = await db.AuditLogs.Where(row => row.EntityId == before.Id).ToListAsync(Ct);
        audit.Count.ShouldBe(2);
        audit.ShouldAllBe(row => row.ActorUserId == _fixture.Actor.UserId && row.ActorApiClientId == null);
        (await db.WebhookOutboxEvents.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(1);
        (await db.Users.CountAsync(Ct)).ShouldBe(0);
        (await db.UserSessions.CountAsync(Ct)).ShouldBe(0);
        (await db.UserWorkspaceAccesses.CountAsync(Ct)).ShouldBe(0);
        (await db.ApiClients.CountAsync(Ct)).ShouldBe(0);
        var mutations = services.GetRequiredService<IWorkspaceMutationRepository>();
        (await mutations.UpdateAsync(new(before.Id, "Stale", before.Slug, null), before.Revision, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        (await mutations.DeleteAsync(before.Id, before.Revision, _fixture.Actor.UserId!.Value, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        (await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(before.Id, update.Value.Revision), Ct)).IsSuccess.ShouldBeTrue();
        using var deletedScope = provider.CreateScope();
        var deletedDb = deletedScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var deleted = await deletedDb.Workspaces.IgnoreQueryFilters().SingleAsync(row => row.Id == before.Id, Ct);
        deleted.IsDeleted.ShouldBeTrue();
        deleted.DeletedByUserId.ShouldBe(_fixture.Actor.UserId);
        (await deletedDb.AuditLogs.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(3);
        (await deletedDb.WebhookOutboxEvents.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Read_visibility_does_not_grant_write()
    {
        _fixture.Capabilities.Write = false;
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var before = (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct)).Value;
        before.CanWrite.ShouldBeFalse();
        (await services.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(before.Id, "Denied", before.Slug, null, before.Revision), Ct))
            .Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await services.GetRequiredService<IWorkspacesDeleteRequestHandler>().HandleAsync(new(before.Id, before.Revision), Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        await AssertUnchangedAsync(provider, before);
    }

    [Fact]
    public async Task Cross_workspace_Admin_mutation_is_denied()
    {
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var row = _fixture.Rows[0];
        var before = await services.GetRequiredService<CmsifyDbContext>().Workspaces.AsNoTracking().SingleAsync(item => item.Id == row.Id, Ct);
        var mutations = services.GetRequiredService<IWorkspaceMutationRepository>();
        (await mutations.UpdateAsync(new(row.Id, "Denied", row.Slug, null), before.UpdatedAt.UtcTicks, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await mutations.DeleteAsync(row.Id, before.UpdatedAt.UtcTicks, _fixture.Actor.UserId!.Value, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        await AssertUnchangedAsync(provider, new(row.Id, row.Name, row.Slug, row.Description, before.CreatedAt, before.UpdatedAt, true));
    }

    [Fact]
    public async Task Revoked_scope_blocks_reused_context_target()
    {
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<CmsifyDbContext>();
        var mutations = services.GetRequiredService<IWorkspaceMutationRepository>();
        var before = (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct)).Value;
        var accepted = await mutations.UpdateAsync(new(before.Id, "Accepted", before.Slug, null), before.Revision, Ct);
        accepted.IsSuccess.ShouldBeTrue();
        _fixture.Visibility.Scope = WorkspaceVisibilityScope.None;
        var resolutions = _fixture.Visibility.Resolutions;
        (await mutations.UpdateAsync(new(before.Id, "Revoked", before.Slug, null), accepted.Value.UpdatedAt.UtcTicks, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        (await mutations.DeleteAsync(before.Id, accepted.Value.UpdatedAt.UtcTicks, _fixture.Actor.UserId!.Value, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        _fixture.Visibility.Resolutions.ShouldBe(resolutions + 2);
        db.ChangeTracker.Entries<Cmsify.Core.Domain.Entities.Workspace>().ShouldBeEmpty();
        await db.SaveChangesAsync(Ct);
        using var verify = provider.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stored = await verifyDb.Workspaces.SingleAsync(row => row.Id == before.Id, Ct);
        stored.Name.ShouldBe("Accepted");
        stored.IsDeleted.ShouldBeFalse();
        (await verifyDb.AuditLogs.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(2);
        (await verifyDb.WebhookOutboxEvents.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_provider_mutations_leave_workspace_audit_and_outbox_unchanged(bool nullDecision)
    {
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var before = (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct)).Value;
        if (nullDecision) _fixture.Visibility.Scope = null!;
        else _fixture.Visibility.Failure = new InvalidOperationException("Test provider failed.");
        var mutations = services.GetRequiredService<IWorkspaceMutationRepository>();
        await Should.ThrowAsync<InvalidOperationException>(() => mutations.UpdateAsync(new(before.Id, "Denied", before.Slug, null), before.Revision, Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => mutations.DeleteAsync(before.Id, before.Revision, _fixture.Actor.UserId!.Value, Ct));
        await services.GetRequiredService<CmsifyDbContext>().SaveChangesAsync(Ct);
        await AssertUnchangedAsync(provider, before);
    }

    [Fact]
    public async Task Cancellation_before_resolution_leaves_no_commands_or_writes()
    {
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var before = (await services.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct)).Value;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var repo = services.GetRequiredService<IWorkspaceRepository>();
        var mutations = services.GetRequiredService<IWorkspaceMutationRepository>();
        var resolutions = _fixture.Visibility.Resolutions;
        using (_fixture.Commands.BeginMeasurement())
        {
            await Should.ThrowAsync<OperationCanceledException>(() => repo.GetAsync(before.Id, cancelled.Token));
            await Should.ThrowAsync<OperationCanceledException>(() => repo.ListAsync(new(), cancelled.Token));
            await Should.ThrowAsync<OperationCanceledException>(() => mutations.UpdateAsync(new(before.Id, "Cancelled", before.Slug, null), before.Revision, cancelled.Token));
            await Should.ThrowAsync<OperationCanceledException>(() => mutations.DeleteAsync(before.Id, before.Revision, _fixture.Actor.UserId!.Value, cancelled.Token));
        }
        _fixture.Commands.CommandCount.ShouldBe(0);
        _fixture.Visibility.Resolutions.ShouldBe(resolutions);
        await AssertUnchangedAsync(provider, before);
    }

    [Fact]
    public async Task Two_host_actors_in_fresh_scopes_have_one_revision_winner()
    {
        var gate = new MutationGate();
        var secondActor = new CurrentActorInfo(Guid.NewGuid(), null, UserRole.Admin, null, true);
        var secondVisibility = new MutableVisibility { Scope = WorkspaceVisibilityScope.ForWorkspaces([_fixture.Rows[1].Id]) };
        using var firstProvider = _fixture.Provider(saveInterceptor: gate);
        using var secondProvider = _fixture.Provider(secondActor, visibility: secondVisibility, saveInterceptor: gate);
        using var firstScope = firstProvider.CreateScope();
        using var secondScope = secondProvider.CreateScope();
        var before = (await firstScope.ServiceProvider.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct)).Value;
        var first = firstScope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(before.Id, "First host", before.Slug, null, before.Revision), Ct);
        var second = secondScope.ServiceProvider.GetRequiredService<IWorkspacesUpdateRequestHandler>().HandleAsync(new(before.Id, "Second host", before.Slug, null, before.Revision), Ct);
        var results = await Task.WhenAll(first, second);
        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Single(result => result.IsFailure).Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        using var verify = firstProvider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await db.AuditLogs.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(2);
        (await db.WebhookOutboxEvents.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(1);
        var acceptedActor = results[0].IsSuccess ? _fixture.Actor.UserId : secondActor.UserId;
        (await db.AuditLogs.SingleAsync(row => row.EntityId == before.Id && row.Action == AuditAction.Updated, Ct)).ActorUserId.ShouldBe(acceptedActor);
        (await secondScope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().GetAsync(_fixture.Rows[3].Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Cancellation_during_save_rolls_back_and_discards_attempt()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var provider = _fixture.Provider(saveInterceptor: new MutationAction(() => cancelled.Cancel()));
        using var scope = provider.CreateScope();
        var before = (await scope.ServiceProvider.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct)).Value;
        await Should.ThrowAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<IWorkspaceMutationRepository>()
            .UpdateAsync(new(before.Id, "Cancelled", before.Slug, null), before.Revision, cancelled.Token));
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertUnchangedAsync(provider, before);
    }

    [Fact]
    public async Task In_flight_authorized_write_finishes_and_next_operation_observes_revocation()
    {
        using var provider = _fixture.Provider(saveInterceptor: new MutationAction(() => _fixture.Visibility.Scope = WorkspaceVisibilityScope.None));
        using var scope = provider.CreateScope();
        var before = (await scope.ServiceProvider.GetRequiredService<IWorkspacesGetRequestHandler>().HandleAsync(new(_fixture.Rows[1].Id), Ct)).Value;
        var repo = scope.ServiceProvider.GetRequiredService<IWorkspaceMutationRepository>();
        var accepted = await repo.UpdateAsync(new(before.Id, "In flight", before.Slug, null), before.Revision, Ct);
        accepted.IsSuccess.ShouldBeTrue();
        (await repo.UpdateAsync(new(before.Id, "After revoke", before.Slug, null), accepted.Value.UpdatedAt.UtcTicks, Ct)).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        using var verify = provider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        (await db.Workspaces.SingleAsync(row => row.Id == before.Id, Ct)).Name.ShouldBe("In flight");
        (await db.WebhookOutboxEvents.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(1);
    }

    private sealed class MutationGate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    private sealed class MutationAction(Action action) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            action();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private static async Task AssertUnchangedAsync(ServiceProvider provider, WorkspaceOutput before)
    {
        using var verify = provider.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var stored = await db.Workspaces.SingleAsync(row => row.Id == before.Id, Ct);
        stored.Name.ShouldBe(before.Name);
        stored.UpdatedAt.UtcTicks.ShouldBe(before.Revision);
        (await db.AuditLogs.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(1);
        (await db.WebhookOutboxEvents.CountAsync(row => row.EntityId == before.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Old_repository_constructor_retains_CmsManaged_scope()
    {
        typeof(WorkspaceRepository).GetConstructor([typeof(CmsifyDbContext), typeof(ICurrentActor)]).ShouldNotBeNull();
        typeof(WorkspaceMutationRepository).GetConstructor([typeof(CmsifyDbContext), typeof(ICurrentActor), typeof(IWebhookOutbox)]).ShouldNotBeNull();
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var legacy = new WorkspaceRepository(db, new CurrentActorInfo(null, Guid.NewGuid(), UserRole.Reader, _fixture.Rows[0].Id, true));
        (await legacy.GetAsync(_fixture.Rows[0].Id, Ct)).ShouldNotBeNull();
        (await legacy.GetAsync(_fixture.Rows[1].Id, Ct)).ShouldBeNull();
    }
}
