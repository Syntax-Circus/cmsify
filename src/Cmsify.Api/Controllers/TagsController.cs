using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using SyntaxCircus.Cmsify.Contracts;
using UserRole = Cmsify.Core.Domain.Enums.UserRole;
using Microsoft.EntityFrameworkCore;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;

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
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<TagResponse>>> List(Guid workspaceId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var query = dbContext.Tags.AsNoTracking()
            .Where(tag => tag.WorkspaceId == workspaceId && !tag.IsDeleted)
            .OrderBy(tag => tag.Name)
            .Select(tag => new TagResponse(tag.Id, tag.Name, dbContext.ContentItemTags.Count(join => join.TagId == tag.Id)));
        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<TagResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var tags = await query.Skip(offset).Take(pagination.PageSize).ToListAsync(ct);
        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<TagResponse>(tags, total, pagination.Page, pagination.PageSize));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.Admin)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
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
