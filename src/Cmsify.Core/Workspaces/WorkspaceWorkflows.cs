using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using SyntaxCircus.Common;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Validation;
using FluentValidation.Results;

namespace Cmsify.Core.Workspaces;

public sealed record WorkspacesListRequest(int Page = 1, int PageSize = 20);
public sealed record WorkspacesCreateRequest(string Name, string Slug, string? Description);
public sealed record WorkspacesGetRequest(Guid Id);
public sealed record WorkspacesUpdateRequest(Guid Id, string Name, string Slug, string? Description, long? ExpectedRevision);
public sealed record WorkspacesDeleteRequest(Guid Id, long? ExpectedRevision);
public sealed record WorkspaceOutput(Guid Id, string Name, string Slug, string? Description,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool CanWrite)
{
    public long Revision => UpdatedAt.UtcTicks;
}
public sealed record WorkspacesListOutput(IReadOnlyList<WorkspaceOutput> Items, int TotalCount, int Page, int PageSize);

public interface IWorkspacesListRequestHandler
{
    Task<Result<WorkspacesListOutput>> HandleAsync(WorkspacesListRequest request, CancellationToken cancellationToken = default);
}
public interface IWorkspacesCreateRequestHandler
{
    Task<Result<WorkspaceOutput>> HandleAsync(WorkspacesCreateRequest request, CancellationToken cancellationToken = default);
}
public interface IWorkspacesGetRequestHandler
{
    Task<Result<WorkspaceOutput>> HandleAsync(WorkspacesGetRequest request, CancellationToken cancellationToken = default);
}
public interface IWorkspacesUpdateRequestHandler
{
    Task<Result<WorkspaceOutput>> HandleAsync(WorkspacesUpdateRequest request, CancellationToken cancellationToken = default);
}
public interface IWorkspacesDeleteRequestHandler
{
    Task<Result> HandleAsync(WorkspacesDeleteRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Atomic revision-checked mutations. Infrastructure owns concurrency and outbox persistence.</summary>
public interface IWorkspaceMutationRepository
{
    Task<Result<WorkspaceDto>> UpdateAsync(UpdateWorkspaceCommand command, long expectedRevision, CancellationToken cancellationToken = default);
    Task<Result> DeleteAsync(Guid id, long expectedRevision, Guid actorUserId, CancellationToken cancellationToken = default);
}

public sealed class WorkspacesListRequestHandler : IWorkspacesListRequestHandler
{
    private readonly IWorkspaceRepository _workspaces;
    private readonly ICurrentActor _actor;
    private readonly IWorkspaceAuthorizationService _authorization;

    public WorkspacesListRequestHandler(IWorkspaceRepository workspaces, ICurrentActor actor, IWorkspaceAuthorizationService authorization)
    {
        _workspaces = workspaces;
        _actor = actor;
        _authorization = authorization;
    }

    public async Task<Result<WorkspacesListOutput>> HandleAsync(WorkspacesListRequest request, CancellationToken cancellationToken = default)
    {
        if (WorkspaceWorkflowRules.Authorize(_actor, UserRole.Reader) is { } denial)
            return Result<WorkspacesListOutput>.Failure(denial);
        if (request.Page < 1 || request.PageSize < 1 || request.PageSize > WorkspaceWorkflowRules.MaxPageSize)
            return Result<WorkspacesListOutput>.Failure(new("validation-failed", "Invalid pagination.", ResultErrorKind.Validation));
        var offset = ((long)request.Page - 1) * request.PageSize;
        var overflow = offset > int.MaxValue;
        var page = await _workspaces.ListAsync(overflow ? new(0, 1) : new((int)offset, request.PageSize), cancellationToken);
        var items = new List<WorkspaceOutput>();
        if (!overflow)
            foreach (var workspace in page.Items)
                items.Add(WorkspaceWorkflowRules.Output(workspace, await _authorization.CanWriteWorkspaceAsync(workspace.Id, cancellationToken)));
        return Result<WorkspacesListOutput>.Success(new(items, page.TotalCount, request.Page, request.PageSize));
    }
}
public sealed class WorkspacesCreateRequestHandler : IWorkspacesCreateRequestHandler
{
    private readonly IWorkspaceRepository _workspaces;
    private readonly ICurrentActor _actor;
    private readonly IWorkspaceAuthorizationService _authorization;

    public WorkspacesCreateRequestHandler(IWorkspaceRepository workspaces, ICurrentActor actor, IWorkspaceAuthorizationService authorization)
    {
        _workspaces = workspaces;
        _actor = actor;
        _authorization = authorization;
    }

    public async Task<Result<WorkspaceOutput>> HandleAsync(WorkspacesCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (WorkspaceWorkflowRules.Authorize(_actor, UserRole.Admin) is { } denial)
            return Result<WorkspaceOutput>.Failure(denial);
        if (!_actor.IsSuperAdmin) return Result<WorkspaceOutput>.Failure(WorkspaceWorkflowRules.Forbidden);
        var command = new CreateWorkspaceCommand(request.Name, request.Slug, request.Description);
        var validation = new CreateWorkspaceCommandValidator().Validate(command);
        if (!validation.IsValid) return WorkspaceWorkflowRules.Invalid(validation);
        var workspace = await _workspaces.CreateAsync(command, cancellationToken);
        return Result<WorkspaceOutput>.Success(WorkspaceWorkflowRules.Output(workspace,
            await _authorization.CanWriteWorkspaceAsync(workspace.Id, cancellationToken)));
    }
}
public sealed class WorkspacesGetRequestHandler : IWorkspacesGetRequestHandler
{
    private readonly IWorkspaceRepository _workspaces;
    private readonly ICurrentActor _actor;
    private readonly IWorkspaceAuthorizationService _authorization;

    public WorkspacesGetRequestHandler(IWorkspaceRepository workspaces, ICurrentActor actor, IWorkspaceAuthorizationService authorization)
    {
        _workspaces = workspaces;
        _actor = actor;
        _authorization = authorization;
    }

    public async Task<Result<WorkspaceOutput>> HandleAsync(WorkspacesGetRequest request, CancellationToken cancellationToken = default)
    {
        if (WorkspaceWorkflowRules.Authorize(_actor, UserRole.Reader) is { } denial)
            return Result<WorkspaceOutput>.Failure(denial);
        var workspace = await _workspaces.GetAsync(request.Id, cancellationToken);
        if (workspace is null || !await _authorization.CanReadWorkspaceAsync(request.Id, cancellationToken))
            return Result<WorkspaceOutput>.Failure(WorkspaceWorkflowRules.NotFound);
        return Result<WorkspaceOutput>.Success(WorkspaceWorkflowRules.Output(workspace,
            await _authorization.CanWriteWorkspaceAsync(request.Id, cancellationToken)));
    }
}
public sealed class WorkspacesUpdateRequestHandler : IWorkspacesUpdateRequestHandler
{
    private readonly IWorkspaceRepository _workspaces;
    private readonly IWorkspaceMutationRepository _mutations;
    private readonly ICurrentActor _actor;
    private readonly IWorkspaceAuthorizationService _authorization;

    public WorkspacesUpdateRequestHandler(IWorkspaceRepository workspaces, IWorkspaceMutationRepository mutations, ICurrentActor actor, IWorkspaceAuthorizationService authorization)
    {
        _workspaces = workspaces;
        _mutations = mutations;
        _actor = actor;
        _authorization = authorization;
    }

    public async Task<Result<WorkspaceOutput>> HandleAsync(WorkspacesUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (WorkspaceWorkflowRules.Authorize(_actor, UserRole.Admin) is { } denial)
            return Result<WorkspaceOutput>.Failure(denial);
        var existing = await _workspaces.GetAsync(request.Id, cancellationToken);
        if (existing is null || !await _authorization.CanWriteWorkspaceAsync(request.Id, cancellationToken))
            return Result<WorkspaceOutput>.Failure(WorkspaceWorkflowRules.NotFound);
        if (request.ExpectedRevision != existing.UpdatedAt.UtcTicks)
            return Result<WorkspaceOutput>.Failure(WorkspaceWorkflowRules.Conflict);
        var command = new UpdateWorkspaceCommand(request.Id, request.Name, request.Slug, request.Description);
        var validation = new UpdateWorkspaceCommandValidator().Validate(command);
        if (!validation.IsValid) return WorkspaceWorkflowRules.Invalid(validation);
        var result = await _mutations.UpdateAsync(command, request.ExpectedRevision.Value, cancellationToken);
        if (result.IsFailure) return Result<WorkspaceOutput>.Failure(result.Errors[0]);
        return Result<WorkspaceOutput>.Success(WorkspaceWorkflowRules.Output(result.Value, true));
    }
}
public sealed class WorkspacesDeleteRequestHandler : IWorkspacesDeleteRequestHandler
{
    private readonly IWorkspaceRepository _workspaces;
    private readonly IWorkspaceMutationRepository _mutations;
    private readonly ICurrentActor _actor;
    private readonly IWorkspaceAuthorizationService _authorization;

    public WorkspacesDeleteRequestHandler(IWorkspaceRepository workspaces, IWorkspaceMutationRepository mutations, ICurrentActor actor, IWorkspaceAuthorizationService authorization)
    {
        _workspaces = workspaces;
        _mutations = mutations;
        _actor = actor;
        _authorization = authorization;
    }

    public async Task<Result> HandleAsync(WorkspacesDeleteRequest request, CancellationToken cancellationToken = default)
    {
        if (WorkspaceWorkflowRules.Authorize(_actor, UserRole.Admin) is { } denial) return Result.Failure(denial);
        var existing = await _workspaces.GetAsync(request.Id, cancellationToken);
        if (existing is null || !await _authorization.CanWriteWorkspaceAsync(request.Id, cancellationToken))
            return Result.Failure(WorkspaceWorkflowRules.NotFound);
        if (request.ExpectedRevision != existing.UpdatedAt.UtcTicks) return Result.Failure(WorkspaceWorkflowRules.Conflict);
        if (_actor.UserId is not { } userId) return Result.Failure(WorkspaceWorkflowRules.Forbidden);
        return await _mutations.DeleteAsync(request.Id, request.ExpectedRevision.Value, userId, cancellationToken);
    }
}

internal static class WorkspaceWorkflowRules
{
    internal const int MaxPageSize = 100;
    internal static readonly ResultError Forbidden = new("forbidden", "Permission denied.", ResultErrorKind.Forbidden);
    internal static readonly ResultError NotFound = new("not-found", "Workspace not found.", ResultErrorKind.NotFound);
    internal static readonly ResultError Conflict = new("concurrency-mismatch", "Workspace revision has changed.", ResultErrorKind.Conflict);
    internal static ResultError? Authorize(ICurrentActor actor, UserRole requiredRole) =>
        !actor.IsAuthenticated ? new("authentication-required", "Authentication is required.", ResultErrorKind.Unauthenticated)
        : actor.Role < requiredRole ? Forbidden : null;
    internal static WorkspaceOutput Output(WorkspaceDto workspace, bool canWrite) =>
        new(workspace.Id, workspace.Name, workspace.Slug, workspace.Description, workspace.CreatedAt, workspace.UpdatedAt, canWrite);
    internal static Result<WorkspaceOutput> Invalid(ValidationResult validation)
    {
        var errors = validation.Errors.Select(error => new ResultError("validation-failed", error.ErrorMessage,
            ResultErrorKind.Validation, error.PropertyName)).ToArray();
        return Result<WorkspaceOutput>.Failure(errors[0], errors.Skip(1).ToArray());
    }
}
