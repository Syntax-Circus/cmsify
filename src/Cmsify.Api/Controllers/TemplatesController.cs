using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Domain.ValueObjects;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/templates")]
[RequireRole(UserRole.Reader)]
public sealed class TemplatesController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IFieldConfigValidator fieldConfigValidator;
    private readonly IWebhookQueue webhookQueue;

    public TemplatesController(CmsifyDbContext dbContext, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, IFieldConfigValidator fieldConfigValidator, IWebhookQueue webhookQueue)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.fieldConfigValidator = fieldConfigValidator;
        this.webhookQueue = webhookQueue;
    }

    [HttpGet]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<TemplateSummaryResponse>>> List(Guid workspaceId, [FromQuery] PaginationQuery pagination, [FromQuery] bool? isSystem = null, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var query = dbContext.Templates.AsNoTracking().Where(template => template.WorkspaceId == workspaceId && !template.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(template => EF.Functions.ILike(template.Name, $"%{search}%") || EF.Functions.ILike(template.Slug, $"%{search}%"));
        }

        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<TemplateSummaryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var items = await query.OrderBy(template => template.Name)
            .Skip(offset)
            .Take(pagination.PageSize)
            .Select(template => new TemplateSummaryResponse(template.Id, template.WorkspaceId, template.Name, template.Slug, template.Description, template.CurrentVersionId))
            .ToListAsync(ct);

        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<TemplateSummaryResponse>(items, total, pagination.Page, pagination.PageSize));
    }

    [HttpPost]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<TemplateResponse>> Create(Guid workspaceId, CreateTemplateRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid template", "Name is required.");
        }

        if (!SlugRules.IsValid(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid template", SlugRules.ValidationMessage);
        }

        var template = new Template
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Slug = request.Slug,
            Description = request.Description
        };
        var version = new TemplateVersion
        {
            TemplateId = template.Id,
            VersionNumber = 1,
            Status = TemplateVersionStatus.Draft
        };
        template.Versions.Add(version);
        dbContext.Templates.Add(template);
        await dbContext.SaveChangesAsync(ct);

        Response.Headers.ETag = ControllerHelpers.ETag(template.UpdatedAt);
        return CreatedAtAction(nameof(Get), new { workspaceId, id = template.Id }, await BuildTemplateResponseAsync(template.Id, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TemplateResponse>> Get(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var template = await dbContext.Templates.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId && !item.IsDeleted, ct);
        if (template is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(template.UpdatedAt);
        return Ok(await BuildTemplateResponseAsync(id, ct));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<TemplateResponse>> Update(Guid workspaceId, Guid id, UpdateTemplateRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var template = await dbContext.Templates.FirstOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId && !item.IsDeleted, ct);
        if (template is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(template.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        template.Name = request.Name;
        template.Description = request.Description;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        Response.Headers.ETag = ControllerHelpers.ETag(template.UpdatedAt);
        return Ok(await BuildTemplateResponseAsync(id, ct));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var template = await dbContext.Templates.FirstOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId && !item.IsDeleted, ct);
        if (template is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(template.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        var hasContent = await dbContext.ContentItems.AnyAsync(content => !content.IsDeleted && dbContext.TemplateVersions.Any(version => version.Id == content.TemplateVersionId && version.TemplateId == id), ct);
        if (hasContent)
        {
            return this.Error(StatusCodes.Status409Conflict, "referenced-by-other-entity", "Template has content items", "Delete the content items that use this template before deleting it.");
        }

        template.IsDeleted = true;
        template.DeletedAt = DateTimeOffset.UtcNow;
        template.DeletedByUserId = currentActor.UserId;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<TemplateVersionSummaryResponse>>> ListVersions(Guid workspaceId, Guid id, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        if (!await TemplateExistsAsync(workspaceId, id, requireWrite: false, ct))
        {
            return NotFound();
        }

        var query = dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.TemplateId == id && !version.IsDeleted)
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new TemplateVersionSummaryResponse(version.Id, version.VersionNumber, version.Status, version.PublishedAt, version.Notes, version.Fields.Count));
        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<TemplateVersionSummaryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var versions = await query.Skip(offset).Take(pagination.PageSize).ToListAsync(ct);
        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<TemplateVersionSummaryResponse>(versions, total, pagination.Page, pagination.PageSize));
    }

    [HttpPost("{id:guid}/versions")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<TemplateVersionResponse>> CreateDraft(Guid workspaceId, Guid id, CreateTemplateVersionRequest request, CancellationToken ct)
    {
        if (!await TemplateExistsAsync(workspaceId, id, requireWrite: true, ct))
        {
            return NotFound();
        }

        if (await dbContext.TemplateVersions.AnyAsync(version => version.TemplateId == id && version.Status == TemplateVersionStatus.Draft && !version.IsDeleted, ct))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Draft already exists", "Only one draft template version can exist at a time.");
        }

        var current = await dbContext.TemplateVersions
            .Include(version => version.Sections)
            .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
            .FirstOrDefaultAsync(version => version.TemplateId == id && version.Status == TemplateVersionStatus.Published && !version.IsDeleted, ct);
        var nextVersionNumber = (await dbContext.TemplateVersions.Where(version => version.TemplateId == id).MaxAsync(version => (int?)version.VersionNumber, ct) ?? 0) + 1;
        var draft = new TemplateVersion { TemplateId = id, VersionNumber = nextVersionNumber, Status = TemplateVersionStatus.Draft, Notes = request.Notes };

        if (current is not null)
        {
            var sectionMap = new Dictionary<Guid, Guid>();
            foreach (var source in current.Sections.OrderBy(section => section.Order))
            {
                var section = new TemplateSection { TemplateVersionId = draft.Id, Name = source.Name, Description = source.Description, Order = source.Order, IsCollapsible = source.IsCollapsible };
                sectionMap[source.Id] = section.Id;
                draft.Sections.Add(section);
            }

            foreach (var source in current.Fields.OrderBy(field => field.Order))
            {
                var field = CopyField(source, draft.Id, source.SectionId.HasValue && sectionMap.TryGetValue(source.SectionId.Value, out var sectionId) ? sectionId : null);
                draft.Fields.Add(field);
            }
        }

        dbContext.TemplateVersions.Add(draft);
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(draft.UpdatedAt);
        return CreatedAtAction(nameof(GetVersion), new { workspaceId, id, versionNumber = draft.VersionNumber }, await BuildVersionResponseAsync(draft.Id, ct));
    }

    [HttpGet("{id:guid}/versions/{versionNumber:int}")]
    public async Task<ActionResult<TemplateVersionResponse>> GetVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        var version = await FindVersionAsync(workspaceId, id, versionNumber, requireWrite: false, tracking: false, ct);
        if (version is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(version.UpdatedAt);
        return Ok(ToVersionResponse(version));
    }

    [HttpPut("{id:guid}/versions/{versionNumber:int}/publish")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<TemplateVersionResponse>> Publish(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        var version = await FindVersionAsync(workspaceId, id, versionNumber, requireWrite: true, tracking: true, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (version.Status != TemplateVersionStatus.Draft)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only draft versions can be published");
        }

        await dbContext.TemplateVersions
            .Where(candidate => candidate.TemplateId == id && candidate.Status == TemplateVersionStatus.Published)
            .ExecuteUpdateAsync(updates => updates.SetProperty(candidate => candidate.Status, TemplateVersionStatus.Archived), ct);

        version.Status = TemplateVersionStatus.Published;
        version.PublishedAt = DateTimeOffset.UtcNow;
        version.UpdatedAt = DateTimeOffset.UtcNow;
        var template = await dbContext.Templates.FirstAsync(template => template.Id == id, ct);
        template.CurrentVersionId = version.Id;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        await webhookQueue.EnqueueAsync(new WebhookEvent(
            "template.version_published",
            workspaceId,
            version.Id,
            JsonSerializer.SerializeToElement(new { templateId = id, templateVersionId = version.Id, versionNumber = version.VersionNumber, workspaceId }),
            DateTimeOffset.UtcNow),
            ct);
        return Ok(ToVersionResponse(version));
    }

    [HttpPost("{id:guid}/versions/{versionNumber:int}/sections")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<TemplateSectionResponse>> AddSection(Guid workspaceId, Guid id, int versionNumber, TemplateSectionRequest request, CancellationToken ct)
    {
        var version = await FindDraftVersionAsync(workspaceId, id, versionNumber, ct);
        if (version.Result is not null)
        {
            return version.Result;
        }

        var section = new TemplateSection { TemplateVersionId = version.Value!.Id, Name = request.Name, Description = request.Description, Order = request.Order, IsCollapsible = request.IsCollapsible };
        version.Value.Sections.Add(section);
        dbContext.TemplateSections.Add(section);
        version.Value.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetVersion), new { workspaceId, id, versionNumber }, ToSectionResponse(section));
    }

    [HttpPut("{id:guid}/versions/{versionNumber:int}/sections/{sectionId:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<TemplateSectionResponse>> UpdateSection(Guid workspaceId, Guid id, int versionNumber, Guid sectionId, TemplateSectionRequest request, CancellationToken ct)
    {
        var version = await FindDraftVersionAsync(workspaceId, id, versionNumber, ct);
        if (version.Result is not null)
        {
            return version.Result;
        }

        var section = version.Value!.Sections.FirstOrDefault(candidate => candidate.Id == sectionId);
        if (section is null)
        {
            return NotFound();
        }

        section.Name = request.Name;
        section.Description = request.Description;
        section.Order = request.Order;
        section.IsCollapsible = request.IsCollapsible;
        version.Value.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return Ok(ToSectionResponse(section));
    }

    [HttpDelete("{id:guid}/versions/{versionNumber:int}/sections/{sectionId:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<IActionResult> DeleteSection(Guid workspaceId, Guid id, int versionNumber, Guid sectionId, CancellationToken ct)
    {
        var version = await FindDraftVersionAsync(workspaceId, id, versionNumber, ct);
        if (version.Result is not null)
        {
            return version.Result;
        }

        var section = version.Value!.Sections.FirstOrDefault(candidate => candidate.Id == sectionId);
        if (section is null)
        {
            return NotFound();
        }

        foreach (var field in version.Value.Fields.Where(field => field.SectionId == sectionId))
        {
            field.SectionId = null;
        }

        dbContext.TemplateSections.Remove(section);
        version.Value.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/versions/{versionNumber:int}/fields")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<TemplateFieldResponse>> AddField(Guid workspaceId, Guid id, int versionNumber, TemplateFieldRequest request, CancellationToken ct)
    {
        var version = await FindDraftVersionAsync(workspaceId, id, versionNumber, ct);
        if (version.Result is not null)
        {
            return version.Result;
        }

        var validation = await ValidateFieldRequestAsync(workspaceId, version.Value!, request, ct);
        if (validation is not null)
        {
            return validation;
        }

        var field = ToField(version.Value!.Id, request);
        version.Value.Fields.Add(field);
        dbContext.TemplateFields.Add(field);
        var cycle = await DetectCycleAsync(workspaceId, id, version.Value, ct);
        if (cycle.Count > 0)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "circular-template-reference", "Circular template reference", $"Saving this field would create a circular reference: {string.Join(" -> ", cycle)}", new Dictionary<string, object?> { ["cycle"] = cycle });
        }

        version.Value.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetVersion), new { workspaceId, id, versionNumber }, ToFieldResponse(field));
    }

    [HttpPut("{id:guid}/versions/{versionNumber:int}/fields/{fieldId:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<TemplateFieldResponse>> UpdateField(Guid workspaceId, Guid id, int versionNumber, Guid fieldId, TemplateFieldRequest request, CancellationToken ct)
    {
        var version = await FindDraftVersionAsync(workspaceId, id, versionNumber, ct);
        if (version.Result is not null)
        {
            return version.Result;
        }

        var field = version.Value!.Fields.FirstOrDefault(candidate => candidate.Id == fieldId);
        if (field is null)
        {
            return NotFound();
        }

        var validation = await ValidateFieldRequestAsync(workspaceId, version.Value!, request, ct, field.Id);
        if (validation is not null)
        {
            return validation;
        }

        ApplyField(field, request);
        dbContext.TemplateFieldAllowedTypes.RemoveRange(field.AllowedTypes);
        field.AllowedTypes.Clear();
        foreach (var allowedType in request.AllowedTypes)
        {
            var entry = new TemplateFieldAllowedType { FieldId = field.Id, PrimitiveType = allowedType.PrimitiveType, AllowedTemplateId = allowedType.AllowedTemplateId };
            field.AllowedTypes.Add(entry);
            dbContext.TemplateFieldAllowedTypes.Add(entry);
        }

        var cycle = await DetectCycleAsync(workspaceId, id, version.Value, ct);
        if (cycle.Count > 0)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "circular-template-reference", "Circular template reference", $"Saving this field would create a circular reference: {string.Join(" -> ", cycle)}", new Dictionary<string, object?> { ["cycle"] = cycle });
        }

        version.Value.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return Ok(ToFieldResponse(field));
    }

    [HttpDelete("{id:guid}/versions/{versionNumber:int}/fields/{fieldId:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<IActionResult> DeleteField(Guid workspaceId, Guid id, int versionNumber, Guid fieldId, CancellationToken ct)
    {
        var version = await FindDraftVersionAsync(workspaceId, id, versionNumber, ct);
        if (version.Result is not null)
        {
            return version.Result;
        }

        var field = version.Value!.Fields.FirstOrDefault(candidate => candidate.Id == fieldId);
        if (field is null)
        {
            return NotFound();
        }

        dbContext.TemplateFieldAllowedTypes.RemoveRange(field.AllowedTypes);
        dbContext.TemplateFields.Remove(field);
        version.Value.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/versions/{versionNumber:int}/fields/reorder")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<IActionResult> ReorderFields(Guid workspaceId, Guid id, int versionNumber, IReadOnlyList<ReorderFieldRequest> request, CancellationToken ct)
    {
        var version = await FindDraftVersionAsync(workspaceId, id, versionNumber, ct);
        if (version.Result is not null)
        {
            return version.Result;
        }

        var orders = request.ToDictionary(item => item.FieldId, item => item.Order);
        foreach (var field in version.Value!.Fields)
        {
            if (orders.TryGetValue(field.Id, out var order))
            {
                field.Order = order;
            }
        }

        version.Value.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<bool> TemplateExistsAsync(Guid workspaceId, Guid id, bool requireWrite, CancellationToken ct) =>
        (requireWrite
            ? await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct)
            : await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        && await dbContext.Templates.AnyAsync(template => template.Id == id && template.WorkspaceId == workspaceId && !template.IsDeleted, ct);

    private async Task<TemplateVersion?> FindVersionAsync(Guid workspaceId, Guid id, int versionNumber, bool requireWrite, bool tracking, CancellationToken ct)
    {
        var canAccess = requireWrite
            ? await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct)
            : await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct);
        if (!canAccess)
        {
            return null;
        }

        var query = dbContext.TemplateVersions
            .Include(version => version.Sections)
            .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
            .Where(version => version.TemplateId == id && version.VersionNumber == versionNumber && !version.IsDeleted && dbContext.Templates.Any(template => template.Id == id && template.WorkspaceId == workspaceId && !template.IsDeleted));
        return await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private async Task<ActionResult<TemplateVersion>> FindDraftVersionAsync(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        var version = await FindVersionAsync(workspaceId, id, versionNumber, requireWrite: true, tracking: true, ct);
        if (version is null)
        {
            return new NotFoundResult();
        }

        if (version.Status != TemplateVersionStatus.Draft)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Published template versions are immutable");
        }

        return version;
    }

    private async Task<TemplateResponse> BuildTemplateResponseAsync(Guid id, CancellationToken ct)
    {
        var template = await dbContext.Templates.AsNoTracking().FirstAsync(item => item.Id == id, ct);
        TemplateVersionResponse? currentVersion = null;
        if (template.CurrentVersionId.HasValue)
        {
            currentVersion = await BuildVersionResponseAsync(template.CurrentVersionId.Value, ct);
        }
        else
        {
            var draft = await dbContext.TemplateVersions.AsNoTracking()
                .Include(version => version.Sections)
                .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
                .FirstOrDefaultAsync(version => version.TemplateId == id && version.Status == TemplateVersionStatus.Draft, ct);
            currentVersion = draft is null ? null : ToVersionResponse(draft);
        }

        return new TemplateResponse(template.Id, template.WorkspaceId, template.Name, template.Slug, template.Description, false, currentVersion);
    }

    private async Task<TemplateVersionResponse> BuildVersionResponseAsync(Guid versionId, CancellationToken ct)
    {
        var version = await dbContext.TemplateVersions.AsNoTracking()
            .Include(item => item.Sections)
            .Include(item => item.Fields).ThenInclude(field => field.AllowedTypes)
            .FirstAsync(version => version.Id == versionId, ct);
        return ToVersionResponse(version);
    }

    private static TemplateVersionResponse ToVersionResponse(TemplateVersion version) =>
        new(version.Id, version.TemplateId, version.VersionNumber, version.Status, version.PublishedAt, version.Notes, version.Sections.OrderBy(section => section.Order).Select(ToSectionResponse).ToArray(), version.Fields.OrderBy(field => field.Order).Select(ToFieldResponse).ToArray());

    private static TemplateSectionResponse ToSectionResponse(TemplateSection section) =>
        new(section.Id, section.Name, section.Description, section.Order, section.IsCollapsible);

    private static TemplateFieldResponse ToFieldResponse(TemplateField field) =>
        new(field.Id, field.SectionId, field.Key, field.Label, field.HelpText, field.Order, field.IsRequired, field.MinOccurrences, field.MaxOccurrences, field.IsOpen, field.CompositionMode, field.PrimitiveType, field.TemplateId, field.AllowedTypes.Select(type => new TemplateFieldAllowedTypeResponse(type.Id, type.PrimitiveType, type.AllowedTemplateId)).ToArray(), field.FieldConfig.Clone(), field.ComponentId);

    private async Task<ObjectResult?> ValidateFieldRequestAsync(Guid workspaceId, TemplateVersion version, TemplateFieldRequest request, CancellationToken ct, Guid? currentFieldId = null)
    {
        if (request.SectionId.HasValue && version.Sections.All(section => section.Id != request.SectionId.Value))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid section", "The selected section does not exist in this template version.");
        }

        if (version.Fields.Any(field => field.Id != currentFieldId && string.Equals(field.Key, request.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Field key already exists", $"A field with key '{request.Key}' already exists in this template version.");
        }

        var typeCount = (request.PrimitiveType.HasValue ? 1 : 0) + (request.TemplateId.HasValue ? 1 : 0) + (request.ComponentId.HasValue ? 1 : 0);
        if (!request.IsOpen && typeCount != 1)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid field type", "Constrained fields must define exactly one primitiveType or templateId.");
        }

        if (request.IsOpen && (request.PrimitiveType.HasValue || request.TemplateId.HasValue || request.ComponentId.HasValue))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid field type", "Open fields cannot define primitiveType or templateId.");
        }

        if (request.ComponentId.HasValue && !dbContext.Components.Any(component =>
                component.Id == request.ComponentId.Value
                && !component.IsDeleted
                && dbContext.Templates.Any(template => template.Id == version.TemplateId && template.WorkspaceId == component.WorkspaceId && !template.IsDeleted)))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Unknown component", "The selected component must belong to this workspace.");
        }

        if (request.PrimitiveType.HasValue)
        {
            var result = fieldConfigValidator.Validate(request.PrimitiveType.Value, request.FieldConfig);
            if (!result.IsValid)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Field configuration is invalid", string.Join(" ", result.Errors.Select(error => error.ErrorMessage)));
            }

            if (request.PrimitiveType == PrimitiveType.PickList && !await HasValidPickListBindingAsync(workspaceId, request.FieldConfig, ct))
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid PickList binding", "The PickList and its pinned revision must belong to this workspace.");
            }
        }

        return null;
    }

    private async Task<bool> HasValidPickListBindingAsync(Guid workspaceId, JsonElement? fieldConfig, CancellationToken ct)
    {
        if (!TryGetPickListBinding(fieldConfig, out var picklistId, out var revisionId))
        {
            return false;
        }

        return await dbContext.PickLists.AnyAsync(picklist =>
            picklist.Id == picklistId
            && picklist.WorkspaceId == workspaceId
            && !picklist.IsDeleted
            && dbContext.PickListRevisions.Any(revision => revision.Id == revisionId && revision.PickListId == picklist.Id), ct);
    }

    private static bool TryGetPickListBinding(JsonElement? fieldConfig, out Guid picklistId, out Guid revisionId)
    {
        picklistId = Guid.Empty;
        revisionId = Guid.Empty;
        return fieldConfig is { ValueKind: JsonValueKind.Object } config
            && config.TryGetProperty("picklistId", out var picklist)
            && picklist.ValueKind == JsonValueKind.String
            && Guid.TryParse(picklist.GetString(), out picklistId)
            && config.TryGetProperty("picklistRevisionId", out var revision)
            && revision.ValueKind == JsonValueKind.String
            && Guid.TryParse(revision.GetString(), out revisionId);
    }

    private static TemplateField ToField(Guid versionId, TemplateFieldRequest request)
    {
        var field = new TemplateField { TemplateVersionId = versionId, Key = request.Key, Label = request.Label };
        ApplyField(field, request);
        foreach (var allowedType in request.AllowedTypes)
        {
            field.AllowedTypes.Add(new TemplateFieldAllowedType { FieldId = field.Id, PrimitiveType = allowedType.PrimitiveType, AllowedTemplateId = allowedType.AllowedTemplateId });
        }

        return field;
    }

    private static void ApplyField(TemplateField field, TemplateFieldRequest request)
    {
        field.SectionId = request.SectionId;
        field.Key = request.Key;
        field.Label = request.Label;
        field.HelpText = request.HelpText;
        field.Order = request.Order;
        field.IsRequired = request.IsRequired;
        field.MinOccurrences = request.MinOccurrences;
        field.MaxOccurrences = request.MaxOccurrences;
        field.IsOpen = request.IsOpen;
        field.CompositionMode = request.CompositionMode;
        field.PrimitiveType = request.PrimitiveType;
        field.TemplateId = request.TemplateId;
        field.ComponentId = request.ComponentId;
        field.FieldConfig = request.FieldConfig.Clone();
    }

    private static TemplateField CopyField(TemplateField source, Guid versionId, Guid? sectionId)
    {
        var field = new TemplateField
        {
            TemplateVersionId = versionId,
            SectionId = sectionId,
            Key = source.Key,
            Label = source.Label,
            HelpText = source.HelpText,
            Order = source.Order,
            IsRequired = source.IsRequired,
            MinOccurrences = source.MinOccurrences,
            MaxOccurrences = source.MaxOccurrences,
            IsOpen = source.IsOpen,
            CompositionMode = source.CompositionMode,
            PrimitiveType = source.PrimitiveType,
            TemplateId = source.TemplateId,
            ComponentId = source.ComponentId,
            FieldConfig = source.FieldConfig.Clone()
        };
        foreach (var allowedType in source.AllowedTypes)
        {
            field.AllowedTypes.Add(new TemplateFieldAllowedType { FieldId = field.Id, PrimitiveType = allowedType.PrimitiveType, AllowedTemplateId = allowedType.AllowedTemplateId });
        }

        return field;
    }

    private async Task<IReadOnlyList<string>> DetectCycleAsync(Guid workspaceId, Guid originTemplateId, TemplateVersion draft, CancellationToken ct)
    {
        var templates = await dbContext.Templates.AsNoTracking()
            .Where(template => template.WorkspaceId == workspaceId && !template.IsDeleted)
            .ToDictionaryAsync(template => template.Id, template => template.Name, ct);
        var versions = await dbContext.TemplateVersions.AsNoTracking()
            .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
            .Where(version => templates.Keys.Contains(version.TemplateId) && !version.IsDeleted)
            .ToListAsync(ct);
        versions.RemoveAll(version => version.Id == draft.Id);
        versions.Add(draft);

        var selected = versions
            .GroupBy(version => version.TemplateId)
            .ToDictionary(
                group => group.Key,
                group => group.FirstOrDefault(version => version.Status == TemplateVersionStatus.Draft)
                    ?? group.FirstOrDefault(version => version.Status == TemplateVersionStatus.Published)
                    ?? group.OrderByDescending(version => version.VersionNumber).First());

        var path = new List<Guid>();
        return Visit(originTemplateId) ? path.Select(id => templates.GetValueOrDefault(id, id.ToString())).ToArray() : Array.Empty<string>();

        bool Visit(Guid templateId)
        {
            if (path.Contains(templateId))
            {
                path.Add(templateId);
                return true;
            }

            if (!selected.TryGetValue(templateId, out var version))
            {
                return false;
            }

            path.Add(templateId);
            foreach (var reference in version.Fields.SelectMany(field => field.TemplateId.HasValue ? new[] { field.TemplateId.Value }.Concat(field.AllowedTypes.Where(type => type.AllowedTemplateId.HasValue).Select(type => type.AllowedTemplateId!.Value)) : field.AllowedTypes.Where(type => type.AllowedTemplateId.HasValue).Select(type => type.AllowedTemplateId!.Value)))
            {
                if (Visit(reference))
                {
                    return true;
                }
            }

            path.RemoveAt(path.Count - 1);
            return false;
        }
    }
}

public sealed record CreateTemplateRequest(string Name, string Slug, string? Description);
public sealed record UpdateTemplateRequest(string Name, string? Description);
public sealed record CreateTemplateVersionRequest(string? Notes);
public sealed record TemplateSectionRequest(string Name, string? Description, int Order, bool IsCollapsible);
public sealed record TemplateFieldAllowedTypeRequest(PrimitiveType? PrimitiveType, Guid? AllowedTemplateId);
public sealed record TemplateFieldRequest(Guid? SectionId, string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, bool IsOpen, CompositionMode CompositionMode, PrimitiveType? PrimitiveType, Guid? TemplateId, IReadOnlyList<TemplateFieldAllowedTypeRequest> AllowedTypes, JsonElement? FieldConfig, Guid? ComponentId = null);
public sealed record ReorderFieldRequest(Guid FieldId, int Order);
public sealed record TemplateSummaryResponse(Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description, Guid? CurrentVersionId);
public sealed record TemplateResponse(Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description, bool IsSystem, TemplateVersionResponse? CurrentVersion);
public sealed record TemplateVersionSummaryResponse(Guid Id, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes, int FieldCount);
public sealed record TemplateVersionResponse(Guid Id, Guid TemplateId, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes, IReadOnlyList<TemplateSectionResponse> Sections, IReadOnlyList<TemplateFieldResponse> Fields);
public sealed record TemplateSectionResponse(Guid Id, string Name, string? Description, int Order, bool IsCollapsible);
public sealed record TemplateFieldAllowedTypeResponse(Guid Id, PrimitiveType? PrimitiveType, Guid? AllowedTemplateId);
public sealed record TemplateFieldResponse(Guid Id, Guid? SectionId, string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, bool IsOpen, CompositionMode CompositionMode, PrimitiveType? PrimitiveType, Guid? TemplateId, IReadOnlyList<TemplateFieldAllowedTypeResponse> AllowedTypes, JsonElement? FieldConfig, Guid? ComponentId = null);
