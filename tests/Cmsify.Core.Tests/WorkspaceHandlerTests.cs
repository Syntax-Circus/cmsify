using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Workspaces;
using NSubstitute;
using Shouldly;
using SyntaxCircus.Common;
using PagedResult = Cmsify.Core.Interfaces.Repositories.PagedResult<Cmsify.Core.Interfaces.Repositories.WorkspaceDto>;

#pragma warning disable xUnit1051 // Dedicated cancellation propagation test supplies a unique caller token.

namespace Cmsify.Core.Tests;

public sealed class WorkspaceHandlerTests
{
    private readonly IWorkspaceRepository _workspaces = Substitute.For<IWorkspaceRepository>();
    private readonly IWorkspaceMutationRepository _mutations = Substitute.For<IWorkspaceMutationRepository>();
    private readonly IWorkspaceAuthorizationService _authorization = Substitute.For<IWorkspaceAuthorizationService>();
    private readonly ICurrentActor _actor = Substitute.For<ICurrentActor>();
    private readonly WorkspaceDto _workspace = new(Guid.NewGuid(), "Workspace", "workspace", null,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public WorkspaceHandlerTests()
    {
        _actor.IsAuthenticated.Returns(true);
        _actor.Role.Returns(UserRole.Admin);
        _actor.UserId.Returns(Guid.NewGuid());
        _actor.IsSuperAdmin.Returns(true);
        _workspaces.GetAsync(_workspace.Id, Arg.Any<CancellationToken>()).Returns(_workspace);
        _authorization.CanReadWorkspaceAsync(_workspace.Id, Arg.Any<CancellationToken>()).Returns(true);
        _authorization.CanWriteWorkspaceAsync(_workspace.Id, Arg.Any<CancellationToken>()).Returns(true);
        _workspaces.ListAsync(Arg.Any<PageRequest>(), Arg.Any<CancellationToken>()).Returns(new PagedResult([_workspace], 1, 0, 50));
    }

    [Fact]
    public async Task Anonymous_AllWorkflowsReturnUnauthenticated()
    {
        _actor.IsAuthenticated.Returns(false);
        var results = await AllWorkflows();
        results.ShouldAllBe(errors => errors.Single().Kind == ResultErrorKind.Unauthenticated);
    }

    [Theory]
    [InlineData(UserRole.Reader)]
    [InlineData(UserRole.Editor)]
    public async Task NonAdmin_MutationsReturnForbidden(UserRole role)
    {
        _actor.Role.Returns(role);
        var results = await AllWorkflows();
        results.Skip(2).ShouldAllBe(errors => errors.Single().Kind == ResultErrorKind.Forbidden);
    }

    [Fact]
    public async Task Create_RequiresSuperAdmin()
    {
        _actor.IsSuperAdmin.Returns(false);
        var result = await Create().HandleAsync(new("Workspace", "workspace", null));
        result.Errors.Single().Kind.ShouldBe(ResultErrorKind.Forbidden);
    }

    [Theory]
    [InlineData("", "workspace", null)]
    [InlineData("  ", "workspace", null)]
    [InlineData("Workspace", "INVALID", null)]
    public async Task Mutations_RejectInvalidInput(string name, string slug, string? description)
    {
        var created = await Create().HandleAsync(new(name, slug, description));
        var updated = await Update().HandleAsync(new(_workspace.Id, name, slug, description, _workspace.UpdatedAt.UtcTicks));
        created.Errors[0].Kind.ShouldBe(ResultErrorKind.Validation);
        updated.Errors[0].Kind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public async Task Mutations_RejectLengthLimitsFromExistingCommandValidators()
    {
        var created = await Create().HandleAsync(new(new string('n', 201), "workspace", new string('d', 1001)));
        created.Errors.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InaccessibleMutation_ReturnsNotFoundBeforeRevisionOrValidation(bool exists)
    {
        if (!exists) _workspaces.GetAsync(_workspace.Id, Arg.Any<CancellationToken>()).Returns((WorkspaceDto?)null);
        _authorization.CanWriteWorkspaceAsync(_workspace.Id, Arg.Any<CancellationToken>()).Returns(false);
        var updated = await Update().HandleAsync(new(_workspace.Id, "", "INVALID", null, null));
        var deleted = await Delete().HandleAsync(new(_workspace.Id, null));
        updated.Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
        deleted.Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
    }

    [Fact]
    public async Task Get_HidesUnreadableWorkspaceEvenWhenRepositoryReturnsIt()
    {
        _authorization.CanReadWorkspaceAsync(_workspace.Id, Arg.Any<CancellationToken>()).Returns(false);
        (await Get().HandleAsync(new(_workspace.Id))).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    public async Task MissingOrStaleRevision_ConflictsBeforeValidation(long? revision)
    {
        var updated = await Update().HandleAsync(new(_workspace.Id, "", "INVALID", null, revision));
        var deleted = await Delete().HandleAsync(new(_workspace.Id, revision));
        updated.Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
        deleted.Errors[0].Kind.ShouldBe(ResultErrorKind.Conflict);
    }

    [Fact]
    public async Task Delete_WithNoUserSubjectReturnsSafeDenial()
    {
        _actor.UserId.Returns((Guid?)null);
        _actor.ApiClientId.Returns(Guid.NewGuid());
        _actor.IsSuperAdmin.Returns(false);
        _actor.WorkspaceId.Returns(_workspace.Id);
        (await Delete().HandleAsync(new(_workspace.Id, _workspace.UpdatedAt.UtcTicks))).Errors[0].Kind.ShouldBe(ResultErrorKind.Forbidden);
        _authorization.CanWriteWorkspaceAsync(_workspace.Id, Arg.Any<CancellationToken>()).Returns(false);
        (await Delete().HandleAsync(new(_workspace.Id, null))).Errors[0].Kind.ShouldBe(ResultErrorKind.NotFound);
    }

    [Fact]
    public async Task SuccessfulOperations_ReturnDataRevisionCapabilitiesAndPaging()
    {
        _workspaces.CreateAsync(new CreateWorkspaceCommand("Workspace", "workspace", null), Arg.Any<CancellationToken>()).Returns(_workspace);
        _workspaces.ListAsync(new PageRequest(10, 10), Arg.Any<CancellationToken>()).Returns(new PagedResult([_workspace], 23, 10, 10));
        _mutations.UpdateAsync(Arg.Any<UpdateWorkspaceCommand>(), _workspace.UpdatedAt.UtcTicks, Arg.Any<CancellationToken>()).Returns(Result<WorkspaceDto>.Success(_workspace with { Name = "Updated" }));
        _mutations.DeleteAsync(_workspace.Id, _workspace.UpdatedAt.UtcTicks, _actor.UserId!.Value, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var page = (await List().HandleAsync(new(2, 10))).Value;
        page.TotalCount.ShouldBe(23);
        page.Page.ShouldBe(2);
        page.PageSize.ShouldBe(10);
        page.Items.Single().CanWrite.ShouldBeTrue();
        var created = (await Create().HandleAsync(new("Workspace", "workspace", null))).Value;
        created.Id.ShouldBe(_workspace.Id);
        created.Revision.ShouldBe(621355968000000000L);
        (await Get().HandleAsync(new(_workspace.Id))).Value.CanWrite.ShouldBeTrue();
        (await Update().HandleAsync(new(_workspace.Id, "Updated", "workspace", null, _workspace.UpdatedAt.UtcTicks))).Value.Name.ShouldBe("Updated");
        (await Delete().HandleAsync(new(_workspace.Id, _workspace.UpdatedAt.UtcTicks))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task List_OverflowPageReturnsEmptyItemsWithTotal()
    {
        _workspaces.ListAsync(new PageRequest(0, 1), Arg.Any<CancellationToken>()).Returns(new PagedResult([_workspace], 7, 0, 1));
        var page = (await List().HandleAsync(new(int.MaxValue, 100))).Value;
        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(7);
    }

    [Fact]
    public async Task List_RejectsPaginationOutsidePublicConstraints()
    {
        var oversized = await List().HandleAsync(new(1, 101));
        oversized.IsFailure.ShouldBeTrue();
        oversized.Errors[0].Kind.ShouldBe(ResultErrorKind.Validation);
        (await List().HandleAsync(new(0, 20))).Errors[0].Kind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public async Task Mutation_PropagatesPersistenceConcurrencyConflict()
    {
        var error = new ResultError("concurrency-mismatch", "Conflict", ResultErrorKind.Conflict);
        _mutations.UpdateAsync(Arg.Any<UpdateWorkspaceCommand>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Result<WorkspaceDto>.Failure(error));
        _mutations.DeleteAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Failure(error));
        (await Update().HandleAsync(new(_workspace.Id, "Updated", "workspace", null, _workspace.UpdatedAt.UtcTicks))).Errors.Single().ShouldBe(error);
        (await Delete().HandleAsync(new(_workspace.Id, _workspace.UpdatedAt.UtcTicks))).Errors.Single().ShouldBe(error);
    }

    [Fact]
    public async Task EveryDependencyReceivesCallerCancellation()
    {
        using var source = new CancellationTokenSource();
        var token = source.Token;
        _workspaces.ListAsync(Arg.Any<PageRequest>(), token).Returns(new PagedResult([_workspace], 1, 0, 50));
        _workspaces.CreateAsync(Arg.Any<CreateWorkspaceCommand>(), token).Returns(_workspace);
        _mutations.UpdateAsync(Arg.Any<UpdateWorkspaceCommand>(), Arg.Any<long>(), token).Returns(Result<WorkspaceDto>.Success(_workspace));
        _mutations.DeleteAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<Guid>(), token).Returns(Result.Success());
        await List().HandleAsync(new(), token);
        await Create().HandleAsync(new("Workspace", "workspace", null), token);
        await Get().HandleAsync(new(_workspace.Id), token);
        await Update().HandleAsync(new(_workspace.Id, "Workspace", "workspace", null, _workspace.UpdatedAt.UtcTicks), token);
        await Delete().HandleAsync(new(_workspace.Id, _workspace.UpdatedAt.UtcTicks), token);
        foreach (var call in _workspaces.ReceivedCalls().Concat(_authorization.ReceivedCalls()).Concat(_mutations.ReceivedCalls()))
            ((CancellationToken)call.GetArguments()[^1]!).ShouldBe(token);
    }

    private async Task<IReadOnlyList<ResultError>[]> AllWorkflows() =>
    [
        (await List().HandleAsync(new())).Errors, (await Get().HandleAsync(new(_workspace.Id))).Errors,
        (await Create().HandleAsync(new("Workspace", "workspace", null))).Errors,
        (await Update().HandleAsync(new(_workspace.Id, "Workspace", "workspace", null, _workspace.UpdatedAt.UtcTicks))).Errors,
        (await Delete().HandleAsync(new(_workspace.Id, _workspace.UpdatedAt.UtcTicks))).Errors
    ];
    private WorkspacesListRequestHandler List() => new(_workspaces, _actor, _authorization);
    private WorkspacesCreateRequestHandler Create() => new(_workspaces, _actor, _authorization);
    private WorkspacesGetRequestHandler Get() => new(_workspaces, _actor, _authorization);
    private WorkspacesUpdateRequestHandler Update() => new(_workspaces, _mutations, _actor, _authorization);
    private WorkspacesDeleteRequestHandler Delete() => new(_workspaces, _mutations, _actor, _authorization);
}
