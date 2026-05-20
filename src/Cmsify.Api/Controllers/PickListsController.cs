using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/picklists")]
[RequireRole(UserRole.Reader)]
public sealed class PickListsController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;

    public PickListsController(CmsifyDbContext dbContext, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PickListSummaryResponse>>> List(Guid workspaceId, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var query = dbContext.PickLists.AsNoTracking()
            .Where(picklist => picklist.WorkspaceId == workspaceId && !picklist.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(picklist => EF.Functions.ILike(picklist.Name, $"%{search}%") || EF.Functions.ILike(picklist.Slug, $"%{search}%"));
        }

        var items = await query
            .OrderBy(picklist => picklist.Name)
            .Select(picklist => new PickListSummaryResponse(picklist.Id, picklist.Name, picklist.Slug, picklist.Description, picklist.Options.Count))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PickListResponse>> Get(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var picklist = await dbContext.PickLists.AsNoTracking()
            .Include(item => item.Options)
            .FirstOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId && !item.IsDeleted, ct);
        if (picklist is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(picklist.UpdatedAt);
        return Ok(ToResponse(picklist));
    }

    [HttpPost]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<PickListResponse>> Create(Guid workspaceId, PickListRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        if (await dbContext.PickLists.AnyAsync(item => item.WorkspaceId == workspaceId && !item.IsDeleted && item.Slug == request.Slug, ct))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "PickList slug already exists", $"A picklist with slug '{request.Slug}' already exists in this workspace.");
        }

        var picklist = new PickList
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Slug = request.Slug,
            Description = request.Description
        };

        foreach (var option in request.Options.Select((value, index) => (Option: value, Index: index)))
        {
            picklist.Options.Add(new PickListOption
            {
                PickListId = picklist.Id,
                Label = option.Option.Label,
                Value = option.Option.Value,
                Order = option.Option.Order ?? option.Index
            });
        }

        dbContext.PickLists.Add(picklist);
        await dbContext.SaveChangesAsync(ct);

        Response.Headers.ETag = ControllerHelpers.ETag(picklist.UpdatedAt);
        return CreatedAtAction(nameof(Get), new { workspaceId, id = picklist.Id }, ToResponse(picklist));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<PickListResponse>> Update(Guid workspaceId, Guid id, PickListRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var picklist = await dbContext.PickLists
            .Include(item => item.Options)
            .FirstOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId && !item.IsDeleted, ct);
        if (picklist is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(picklist.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        if (!string.Equals(picklist.Slug, request.Slug, StringComparison.OrdinalIgnoreCase)
            && await dbContext.PickLists.AnyAsync(item => item.WorkspaceId == workspaceId && !item.IsDeleted && item.Id != id && item.Slug == request.Slug, ct))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "PickList slug already exists", $"A picklist with slug '{request.Slug}' already exists in this workspace.");
        }

        picklist.Name = request.Name;
        picklist.Slug = request.Slug;
        picklist.Description = request.Description;
        picklist.UpdatedAt = DateTimeOffset.UtcNow;

        dbContext.PickListOptions.RemoveRange(picklist.Options);
        picklist.Options.Clear();
        foreach (var option in request.Options.Select((value, index) => (Option: value, Index: index)))
        {
            var entity = new PickListOption
            {
                PickListId = picklist.Id,
                Label = option.Option.Label,
                Value = option.Option.Value,
                Order = option.Option.Order ?? option.Index
            };
            picklist.Options.Add(entity);
            dbContext.PickListOptions.Add(entity);
        }

        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(picklist.UpdatedAt);
        return Ok(ToResponse(picklist));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var picklist = await dbContext.PickLists.FirstOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId && !item.IsDeleted, ct);
        if (picklist is null)
        {
            return NotFound();
        }

        var idString = id.ToString();
        var picklistFields = await dbContext.TemplateFields
            .Where(field => field.PrimitiveType == PrimitiveType.PickList && field.FieldConfig != null
                && dbContext.TemplateVersions.Any(version => version.Id == field.TemplateVersionId
                    && dbContext.Templates.Any(template => template.Id == version.TemplateId && template.WorkspaceId == workspaceId && !template.IsDeleted)))
            .Select(field => field.FieldConfig)
            .ToListAsync(ct);
        var referenced = picklistFields.Any(config => config.HasValue
            && config.Value.ValueKind == System.Text.Json.JsonValueKind.Object
            && config.Value.TryGetProperty("picklistId", out var picklistIdElement)
            && picklistIdElement.ValueKind == System.Text.Json.JsonValueKind.String
            && string.Equals(picklistIdElement.GetString(), idString, StringComparison.OrdinalIgnoreCase));
        if (referenced)
        {
            return this.Error(StatusCodes.Status409Conflict, "referenced-by-other-entity", "PickList is in use", "Unbind this picklist from all template fields before deleting it.");
        }

        picklist.IsDeleted = true;
        picklist.DeletedAt = DateTimeOffset.UtcNow;
        picklist.DeletedByUserId = currentActor.UserId;
        picklist.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    private ObjectResult? ValidateRequest(PickListRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid picklist", "Name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid picklist", "Slug is required.");
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in request.Options)
        {
            if (string.IsNullOrWhiteSpace(option.Label) || string.IsNullOrWhiteSpace(option.Value))
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid picklist option", "Each option requires a label and value.");
            }

            if (!values.Add(option.Value))
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Duplicate picklist option", $"Option value '{option.Value}' is duplicated.");
            }
        }

        return null;
    }

    private static PickListResponse ToResponse(PickList picklist) =>
        new(picklist.Id, picklist.Name, picklist.Slug, picklist.Description,
            picklist.Options.OrderBy(option => option.Order).Select(option => new PickListOptionResponse(option.Id, option.Label, option.Value, option.Order)).ToArray());
}

public sealed record PickListSummaryResponse(Guid Id, string Name, string Slug, string? Description, int OptionCount);

public sealed record PickListResponse(Guid Id, string Name, string Slug, string? Description, IReadOnlyList<PickListOptionResponse> Options);

public sealed record PickListOptionResponse(Guid Id, string Label, string Value, int Order);

public sealed record PickListOptionRequest(string Label, string Value, int? Order);

public sealed record PickListRequest(string Name, string Slug, string? Description, IReadOnlyList<PickListOptionRequest> Options);
