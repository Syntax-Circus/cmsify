using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/tags")]
[RequireRole(UserRole.Reader)]
public sealed class TagsController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;

    public TagsController(CmsifyDbContext dbContext, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TagResponse>>> List(Guid workspaceId, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return Forbid();
        }

        var tags = await dbContext.Tags.AsNoTracking()
            .Where(tag => tag.WorkspaceId == workspaceId && !tag.IsDeleted)
            .OrderBy(tag => tag.Name)
            .Select(tag => new TagResponse(tag.Id, tag.Name, dbContext.ContentItemTags.Count(join => join.TagId == tag.Id)))
            .ToListAsync(ct);
        return Ok(tags);
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.Admin)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return Forbid();
        }

        var tag = await dbContext.Tags.FirstOrDefaultAsync(tag => tag.Id == id && tag.WorkspaceId == workspaceId && !tag.IsDeleted, ct);
        if (tag is null)
        {
            return NotFound();
        }

        dbContext.ContentItemTags.RemoveRange(dbContext.ContentItemTags.Where(join => join.TagId == id));
        tag.IsDeleted = true;
        tag.DeletedAt = DateTimeOffset.UtcNow;
        tag.DeletedByUserId = currentActor.UserId;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

}

public sealed record TagResponse(Guid Id, string Name, int UsageCount);
