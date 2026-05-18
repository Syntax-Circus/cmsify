using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Core.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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
    public async Task<ActionResult<PagedResult<WorkspaceDto>>> List([FromQuery] int offset = 0, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var result = await workspaceRepository.ListAsync(new PageRequest(offset, limit), ct);
        return Ok(result);
    }

    [HttpPost]
    [RequireRole(UserRole.Admin)]
    public async Task<ActionResult<WorkspaceDto>> Create(CreateWorkspaceCommand command, CancellationToken ct)
    {
        if (!currentActor.IsSuperAdmin)
        {
            return Forbid();
        }

        var workspace = await workspaceRepository.CreateAsync(command, ct);
        Response.Headers.ETag = ToETag(workspace);
        return CreatedAtAction(nameof(Get), new { id = workspace.Id }, workspace);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkspaceDto>> Get(Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(id, ct))
        {
            return Forbid();
        }

        var workspace = await workspaceRepository.GetAsync(id, ct);
        if (workspace is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ToETag(workspace);
        return Ok(workspace);
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Admin)]
    public async Task<ActionResult<WorkspaceDto>> Update(Guid id, UpdateWorkspaceRequest request, CancellationToken ct)
    {
        var existing = await workspaceRepository.GetAsync(id, ct);
        if (existing is null)
        {
            return NotFound();
        }

        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(id, ct))
        {
            return Forbid();
        }

        if (!IfMatchMatches(existing))
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        var workspace = await workspaceRepository.UpdateAsync(new UpdateWorkspaceCommand(id, request.Name, request.Slug, request.Description), ct);
        await webhookQueue.EnqueueAsync(new WebhookEvent("workspace.updated", id, id, JsonSerializer.SerializeToElement(new { workspaceId = id, workspace.Name, workspace.Slug }), DateTimeOffset.UtcNow), ct);
        Response.Headers.ETag = ToETag(workspace);
        return Ok(workspace);
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
            return Forbid();
        }

        if (!IfMatchMatches(existing))
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        await workspaceRepository.SoftDeleteAsync(id, currentActor.UserId!.Value, ct);
        return NoContent();
    }

    private bool IfMatchMatches(WorkspaceDto workspace)
    {
        var ifMatch = Request.Headers.IfMatch.ToString();
        return !string.IsNullOrWhiteSpace(ifMatch) && string.Equals(ifMatch, ToETag(workspace), StringComparison.Ordinal);
    }

    private static string ToETag(WorkspaceDto workspace) => $"\"{workspace.UpdatedAt.UtcTicks}\"";
}

public sealed record UpdateWorkspaceRequest(string Name, string Slug, string? Description);
