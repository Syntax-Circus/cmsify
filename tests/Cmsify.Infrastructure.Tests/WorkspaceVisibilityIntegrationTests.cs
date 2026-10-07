using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Cmsify.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
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
    public async Task Old_repository_constructor_retains_CmsManaged_scope()
    {
        typeof(WorkspaceRepository).GetConstructor([typeof(CmsifyDbContext), typeof(ICurrentActor)]).ShouldNotBeNull();
        using var provider = _fixture.Provider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var legacy = new WorkspaceRepository(db, new CurrentActorInfo(null, Guid.NewGuid(), UserRole.Reader, _fixture.Rows[0].Id, true));
        (await legacy.GetAsync(_fixture.Rows[0].Id, Ct)).ShouldNotBeNull();
        (await legacy.GetAsync(_fixture.Rows[1].Id, Ct)).ShouldBeNull();
    }
}
