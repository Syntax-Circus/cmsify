using Cmsify.Api.Auth;
using Cmsify.Core.Workspaces;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using SyntaxCircus.Common;
using SyntaxCircus.Cmsify.Contracts;
using ContractWorkspaceDto = SyntaxCircus.Cmsify.Contracts.WorkspaceDto;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;
using UserRole = Cmsify.Core.Domain.Enums.UserRole;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces")]
[RequireRole(UserRole.Reader)]
public sealed class WorkspacesController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<ContractWorkspaceDto>>> List([FromQuery] PaginationQuery pagination,
        [FromServices] IWorkspacesListRequestHandler handler, CancellationToken ct = default)
    {
        var result = await handler.HandleAsync(new(pagination.Page, pagination.PageSize), ct);
        if (result.IsFailure) return Failure(result.Errors);
        var page = result.Value;
        return Ok(new PagedResponse<ContractWorkspaceDto>(page.Items.Select(ToContract).ToArray(), page.TotalCount, page.Page, page.PageSize));
    }

    [HttpPost]
    [RequireRole(UserRole.Admin)]
    public async Task<ActionResult<ContractWorkspaceDto>> Create(WorkspaceRequest request,
        [FromServices] IWorkspacesCreateRequestHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(request.Name, request.Slug, request.Description), ct);
        if (result.IsFailure) return Failure(result.Errors);
        var workspace = result.Value;
        Response.Headers.ETag = ETag(workspace.Revision);
        return CreatedAtAction(nameof(Get), new { id = workspace.Id }, ToContract(workspace));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContractWorkspaceDto>> Get(Guid id,
        [FromServices] IWorkspacesGetRequestHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(id), ct);
        if (result.IsFailure) return Failure(result.Errors);
        Response.Headers.ETag = ETag(result.Value.Revision);
        return Ok(ToContract(result.Value));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Admin)]
    public async Task<ActionResult<ContractWorkspaceDto>> Update(Guid id, WorkspaceRequest request,
        [FromServices] IWorkspacesUpdateRequestHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(id, request.Name, request.Slug, request.Description, ExpectedRevision()), ct);
        if (result.IsFailure) return Failure(result.Errors);
        Response.Headers.ETag = ETag(result.Value.Revision);
        return Ok(ToContract(result.Value));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.Admin)]
    public async Task<IActionResult> Delete(Guid id, [FromServices] IWorkspacesDeleteRequestHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new(id, ExpectedRevision()), ct);
        return result.IsFailure ? Failure(result.Errors) : NoContent();
    }

    private long? ExpectedRevision()
    {
        var header = Request.Headers.IfMatch.ToString();
        if (header.Length < 2 || header[0] != '"' || header[^1] != '"') return null;
        return long.TryParse(header.AsSpan(1, header.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            && string.Equals(header, ETag(revision), StringComparison.Ordinal) ? revision : null;
    }

    private static string ETag(long revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";
    private static ContractWorkspaceDto ToContract(WorkspaceOutput workspace) =>
        new(workspace.Id, workspace.Name, workspace.Slug, workspace.Description, workspace.CreatedAt, workspace.UpdatedAt, workspace.CanWrite);

    private ActionResult Failure(IReadOnlyList<ResultError> errors) => errors[0].Kind switch
    {
        ResultErrorKind.Unauthenticated => StatusCode(StatusCodes.Status401Unauthorized),
        ResultErrorKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden),
        ResultErrorKind.NotFound => NotFound(),
        ResultErrorKind.Conflict => StatusCode(StatusCodes.Status412PreconditionFailed),
        ResultErrorKind.Validation => this.Error(StatusCodes.Status422UnprocessableEntity,
            "validation-failed", "Invalid workspace", errors[0].Message),
        _ => throw new InvalidOperationException("Unexpected workspace workflow outcome.")
    };
}
