using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Domain.ValueObjects;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
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
    private readonly IWorkspaceRepository workspaceRepository;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IWebhookQueue webhookQueue;

    public WorkspacesController(IWorkspaceRepository workspaceRepository, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, IWebhookQueue webhookQueue)
    {
        this.workspaceRepository = workspaceRepository;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.webhookQueue = webhookQueue;
    }

    [HttpGet]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<ContractWorkspaceDto>>> List([FromQuery] PaginationQuery pagination, CancellationToken ct = default)
    {
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            var countResult = await workspaceRepository.ListAsync(new PageRequest(0, 1), ct);
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContractWorkspaceDto>([], countResult.TotalCount, pagination.Page, pagination.PageSize));
        }

        var result = await workspaceRepository.ListAsync(new PageRequest(offset, pagination.PageSize), ct);
        var workspaces = new List<ContractWorkspaceDto>(result.Items.Count);
        foreach (var workspace in result.Items)
        {
            workspaces.Add(await WithCapabilitiesAsync(workspace, ct));
        }

        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContractWorkspaceDto>(workspaces, result.TotalCount, pagination.Page, pagination.PageSize));
    }

    [HttpPost]
    [RequireRole(UserRole.Admin)]
    public async Task<ActionResult<ContractWorkspaceDto>> Create(WorkspaceRequest request, CancellationToken ct)
    {
        if (!currentActor.IsSuperAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!SlugRules.IsValid(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid workspace", SlugRules.ValidationMessage);
        }

        var workspace = await workspaceRepository.CreateAsync(new CreateWorkspaceCommand(request.Name, request.Slug, request.Description), ct);
        Response.Headers.ETag = ToETag(workspace);
        return CreatedAtAction(nameof(Get), new { id = workspace.Id }, await WithCapabilitiesAsync(workspace, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContractWorkspaceDto>> Get(Guid id, CancellationToken ct)
    {
        var workspace = await workspaceRepository.GetAsync(id, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ToETag(workspace);
        return Ok(await WithCapabilitiesAsync(workspace, ct));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Admin)]
    public async Task<ActionResult<ContractWorkspaceDto>> Update(Guid id, WorkspaceRequest request, CancellationToken ct)
    {
        var existing = await workspaceRepository.GetAsync(id, ct);
        if (existing is null)
        {
            return NotFound();
        }

        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(id, ct))
        {
            return NotFound();
        }

        if (!IfMatchMatches(existing))
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        if (!SlugRules.IsValid(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid workspace", SlugRules.ValidationMessage);
        }

        var workspace = await workspaceRepository.UpdateAsync(new UpdateWorkspaceCommand(id, request.Name, request.Slug, request.Description), ct);
        await webhookQueue.EnqueueAsync(new WebhookEvent("workspace.updated", id, id, JsonSerializer.SerializeToElement(new { workspaceId = id, workspace.Name, workspace.Slug }), DateTimeOffset.UtcNow), ct);
        Response.Headers.ETag = ToETag(workspace);
        return Ok(await WithCapabilitiesAsync(workspace, ct));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var existing = await workspaceRepository.GetAsync(id, ct);
        if (existing is null)
        {
            return NotFound();
        }

        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(id, ct))
        {
            return NotFound();
        }

        if (!IfMatchMatches(existing))
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        await workspaceRepository.SoftDeleteAsync(id, currentActor.UserId!.Value, ct);
        return NoContent();
    }

    private bool IfMatchMatches(Cmsify.Core.Interfaces.Repositories.WorkspaceDto workspace)
    {
        var ifMatch = Request.Headers.IfMatch.ToString();
        return !string.IsNullOrWhiteSpace(ifMatch) && string.Equals(ifMatch, ToETag(workspace), StringComparison.Ordinal);
    }

    private static string ToETag(Cmsify.Core.Interfaces.Repositories.WorkspaceDto workspace) => $"\"{workspace.UpdatedAt.UtcTicks}\"";

    private async Task<ContractWorkspaceDto> WithCapabilitiesAsync(Cmsify.Core.Interfaces.Repositories.WorkspaceDto workspace, CancellationToken ct) =>
        workspace.ToContract(await workspaceAuthorization.CanWriteWorkspaceAsync(workspace.Id, ct));
}
