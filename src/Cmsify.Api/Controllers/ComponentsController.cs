using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Domain.ValueObjects;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/components")]
[RequireRole(UserRole.Reader)]
public sealed class ComponentsController : ControllerBase
{
    private readonly CmsifyDbContext db;
    private readonly IWorkspaceAuthorizationService authorization;
    private readonly IFieldConfigValidator fieldConfigValidator;

    public ComponentsController(CmsifyDbContext db, IWorkspaceAuthorizationService authorization, IFieldConfigValidator fieldConfigValidator)
    {
        this.db = db; this.authorization = authorization; this.fieldConfigValidator = fieldConfigValidator;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ComponentSummaryResponse>>> List(Guid workspaceId, CancellationToken ct)
    {
        if (!await authorization.CanReadWorkspaceAsync(workspaceId, ct)) return NotFound();
        return Ok(await db.Components.AsNoTracking().Where(x => x.WorkspaceId == workspaceId && !x.IsDeleted)
            .OrderBy(x => x.Name).Select(x => new ComponentSummaryResponse(x.Id, x.Name, x.Slug, x.Description, x.CurrentVersionId)).ToListAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ComponentResponse>> Get(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await authorization.CanReadWorkspaceAsync(workspaceId, ct)) return NotFound();
        var component = await LoadAsync(workspaceId, id, tracking: false, ct);
        if (component is null) return NotFound();
        Response.Headers.ETag = ControllerHelpers.ETag(component.UpdatedAt);
        return Ok(ToResponse(component));
    }

    [HttpPost]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<ComponentResponse>> Create(Guid workspaceId, ComponentRequest request, CancellationToken ct)
    {
        if (!await authorization.CanWriteWorkspaceAsync(workspaceId, ct)) return NotFound();
        if (await ValidateDefinitionAsync(workspaceId, request, null, ct) is { } error) return error;
        var component = new ComponentDefinition { WorkspaceId = workspaceId, Name = request.Name, Slug = request.Slug, Description = request.Description };
        var version = new ComponentVersion { ComponentId = component.Id, VersionNumber = 1, Notes = "Initial component version" };
        component.Versions.Add(version);
        db.Components.Add(component);
        // Component -> current version and version -> component form a database cycle.
        // Persist the pair first, then make the working draft the current version.
        await db.SaveChangesAsync(ct);
        component.CurrentVersionId = version.Id;
        await db.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(component.UpdatedAt);
        return CreatedAtAction(nameof(Get), new { workspaceId, id = component.Id }, ToResponse(component));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<ComponentResponse>> Update(Guid workspaceId, Guid id, ComponentRequest request, CancellationToken ct)
    {
        if (!await authorization.CanWriteWorkspaceAsync(workspaceId, ct)) return NotFound();
        var component = await LoadAsync(workspaceId, id, tracking: true, ct);
        if (component is null) return NotFound();
        if (!this.IfMatchMatches(component.UpdatedAt)) return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        if (await ValidateDefinitionAsync(workspaceId, request, id, ct) is { } error) return error;
        component.Name = request.Name; component.Slug = request.Slug; component.Description = request.Description; component.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); Response.Headers.ETag = ControllerHelpers.ETag(component.UpdatedAt); return Ok(ToResponse(component));
    }

    [HttpPost("{id:guid}/versions")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<ComponentVersionResponse>> CreateDraft(Guid workspaceId, Guid id, ComponentVersionRequest request, CancellationToken ct)
    {
        if (!await authorization.CanWriteWorkspaceAsync(workspaceId, ct)) return NotFound();
        var component = await LoadAsync(workspaceId, id, tracking: true, ct);
        if (component is null) return NotFound();
        var existing = component.Versions.FirstOrDefault(x => x.Status == TemplateVersionStatus.Draft && !x.IsDeleted);
        if (existing is not null) return Ok(ToResponse(existing));
        var source = component.Versions.Where(x => !x.IsDeleted).OrderByDescending(x => x.VersionNumber).First();
        var draft = new ComponentVersion { ComponentId = id, VersionNumber = source.VersionNumber + 1, Notes = request.Notes };
        foreach (var field in source.Fields) draft.Fields.Add(CopyField(field, draft.Id));
        component.Versions.Add(draft); await db.SaveChangesAsync(ct); return CreatedAtAction(nameof(GetVersion), new { workspaceId, id, versionNumber = draft.VersionNumber }, ToResponse(draft));
    }

    [HttpGet("{id:guid}/versions/{versionNumber:int}")]
    public async Task<ActionResult<ComponentVersionResponse>> GetVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        if (!await authorization.CanReadWorkspaceAsync(workspaceId, ct)) return NotFound();
        var version = await db.ComponentVersions.AsNoTracking().Include(x => x.Fields).FirstOrDefaultAsync(x => x.ComponentId == id && x.VersionNumber == versionNumber && !x.IsDeleted && db.Components.Any(c => c.Id == id && c.WorkspaceId == workspaceId && !c.IsDeleted), ct);
        return version is null ? NotFound() : Ok(ToResponse(version));
    }

    [HttpPut("{id:guid}/versions/{versionNumber:int}/fields")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<ComponentVersionResponse>> SaveFields(Guid workspaceId, Guid id, int versionNumber, IReadOnlyList<ComponentFieldRequest> fields, CancellationToken ct)
    {
        if (!await authorization.CanWriteWorkspaceAsync(workspaceId, ct)) return NotFound();
        var version = await db.ComponentVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ComponentId == id && x.VersionNumber == versionNumber && x.Status == TemplateVersionStatus.Draft && !x.IsDeleted && db.Components.Any(c => c.Id == id && c.WorkspaceId == workspaceId && !c.IsDeleted), ct);
        if (version is null) return NotFound();
        var invalid = await ValidateFieldsAsync(workspaceId, id, fields, ct); if (invalid is not null) return invalid;
        var replacementFields = fields.Select(field => ToField(version.Id, field)).ToArray();
        var candidate = new ComponentVersion { Id = version.Id, ComponentId = version.ComponentId, VersionNumber = version.VersionNumber, Status = version.Status, Notes = version.Notes };
        foreach (var field in replacementFields) candidate.Fields.Add(field);
        if (await HasCycleAsync(workspaceId, id, candidate, ct)) return this.Error(StatusCodes.Status422UnprocessableEntity, "circular-component-reference", "Circular component reference", "Nested component definitions cannot contain a cycle.");
        await db.ComponentFields.Where(field => field.ComponentVersionId == version.Id).ExecuteDeleteAsync(ct);
        db.ComponentFields.AddRange(replacementFields);
        await db.SaveChangesAsync(ct);
        return Ok(ToResponse(candidate));
    }

    [HttpPost("{id:guid}/versions/{versionNumber:int}/publish")]
    [RequireRole(UserRole.TemplateAdmin)]
    public async Task<ActionResult<ComponentResponse>> Publish(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        if (!await authorization.CanWriteWorkspaceAsync(workspaceId, ct)) return NotFound();
        var component = await LoadAsync(workspaceId, id, tracking: true, ct); if (component is null) return NotFound();
        var version = component.Versions.FirstOrDefault(x => x.VersionNumber == versionNumber && x.Status == TemplateVersionStatus.Draft && !x.IsDeleted); if (version is null) return NotFound();
        if (version.Fields.Count == 0) return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Component has no fields", "A component version must contain at least one field before publishing.");
        foreach (var prior in component.Versions.Where(x => x.Status == TemplateVersionStatus.Published)) prior.Status = TemplateVersionStatus.Archived;
        version.Status = TemplateVersionStatus.Published; version.PublishedAt = DateTimeOffset.UtcNow; component.CurrentVersionId = version.Id; component.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); Response.Headers.ETag = ControllerHelpers.ETag(component.UpdatedAt); return Ok(ToResponse(component));
    }

    private async Task<ComponentDefinition?> LoadAsync(Guid workspaceId, Guid id, bool tracking, CancellationToken ct) => await (tracking ? db.Components : db.Components.AsNoTracking()).Include(x => x.Versions).ThenInclude(x => x.Fields).FirstOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId && !x.IsDeleted, ct);
    private async Task<ObjectResult?> ValidateDefinitionAsync(Guid workspaceId, ComponentRequest request, Guid? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid component", "Name is required.");
        if (!SlugRules.IsValid(request.Slug)) return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid component", SlugRules.ValidationMessage);
        if (await db.Components.AnyAsync(x => x.WorkspaceId == workspaceId && x.Id != id && x.Slug == request.Slug && !x.IsDeleted, ct)) return this.Error(StatusCodes.Status409Conflict, "conflict", "Component slug already exists", "Component slugs are unique within a workspace.");
        return null;
    }
    private async Task<ObjectResult?> ValidateFieldsAsync(Guid workspaceId, Guid componentId, IReadOnlyList<ComponentFieldRequest> fields, CancellationToken ct)
    {
        if (fields.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count) return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Duplicate component field key", "Component field keys must be unique.");
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key) || string.IsNullOrWhiteSpace(field.Label) || field.Order < 0 || field.MinOccurrences < 0 || field.MaxOccurrences < field.MinOccurrences || (field.PrimitiveType.HasValue == field.NestedComponentId.HasValue)) return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid component field", "Each component field requires exactly one primitive type or nested component and valid occurrence limits.");
            if (field.PrimitiveType.HasValue && !fieldConfigValidator.Validate(field.PrimitiveType.Value, field.FieldConfig).IsValid) return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid component field configuration", "Field configuration is invalid for its primitive type.");
            if (field.PrimitiveType == PrimitiveType.PickList && !await HasValidPickListBindingAsync(workspaceId, field.FieldConfig, ct)) return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid PickList binding", "The PickList and its pinned revision must belong to this workspace.");
            if (field.NestedComponentId.HasValue && !await db.Components.AnyAsync(x => x.Id == field.NestedComponentId && x.WorkspaceId == workspaceId && !x.IsDeleted, ct)) return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Unknown nested component", "Nested components must belong to the workspace.");
        }
        return null;
    }

    private async Task<bool> HasValidPickListBindingAsync(Guid workspaceId, JsonElement? fieldConfig, CancellationToken ct)
    {
        if (!TryGetPickListBinding(fieldConfig, out var picklistId, out var revisionId)) return false;
        return await db.PickLists.AnyAsync(picklist => picklist.Id == picklistId && picklist.WorkspaceId == workspaceId && !picklist.IsDeleted && db.PickListRevisions.Any(revision => revision.Id == revisionId && revision.PickListId == picklist.Id), ct);
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
    private async Task<bool> HasCycleAsync(Guid workspaceId, Guid originId, ComponentVersion draft, CancellationToken ct)
    {
        var currentVersions = await db.Components.AsNoTracking()
            .Where(component => component.WorkspaceId == workspaceId && !component.IsDeleted && component.CurrentVersionId.HasValue)
            .Select(component => new { component.Id, VersionId = component.CurrentVersionId!.Value })
            .ToListAsync(ct);
        var fieldsByVersion = await db.ComponentFields.AsNoTracking()
            .Where(field => currentVersions.Select(version => version.VersionId).Contains(field.ComponentVersionId) && field.NestedComponentId.HasValue)
            .ToListAsync(ct);
        var componentByVersion = currentVersions.ToDictionary(version => version.VersionId, version => version.Id);
        var edges = fieldsByVersion.GroupBy(field => componentByVersion[field.ComponentVersionId])
            .ToDictionary(group => group.Key, group => group.Select(field => field.NestedComponentId!.Value).ToArray());
        edges[originId] = draft.Fields.Where(field => field.NestedComponentId.HasValue).Select(field => field.NestedComponentId!.Value).ToArray();

        var path = new HashSet<Guid>();
        return Visit(originId);
        bool Visit(Guid current)
        {
            if (!path.Add(current)) return true;
            var hasCycle = edges.TryGetValue(current, out var children) && children.Any(Visit);
            path.Remove(current);
            return hasCycle;
        }
    }
    private static ComponentField ToField(Guid versionId, ComponentFieldRequest field) => new() { ComponentVersionId = versionId, Key = field.Key, Label = field.Label, HelpText = field.HelpText, Order = field.Order, IsRequired = field.IsRequired, MinOccurrences = field.MinOccurrences, MaxOccurrences = field.MaxOccurrences, PrimitiveType = field.PrimitiveType, NestedComponentId = field.NestedComponentId, FieldConfig = field.FieldConfig.Clone() };
    private static ComponentField CopyField(ComponentField field, Guid versionId) => new() { ComponentVersionId = versionId, Key = field.Key, Label = field.Label, HelpText = field.HelpText, Order = field.Order, IsRequired = field.IsRequired, MinOccurrences = field.MinOccurrences, MaxOccurrences = field.MaxOccurrences, PrimitiveType = field.PrimitiveType, NestedComponentId = field.NestedComponentId, FieldConfig = field.FieldConfig.Clone() };
    private static ComponentResponse ToResponse(ComponentDefinition component) { var current = component.Versions.FirstOrDefault(x => x.Id == component.CurrentVersionId); return new(component.Id, component.WorkspaceId, component.Name, component.Slug, component.Description, current is null ? null : ToResponse(current)); }
    private static ComponentVersionResponse ToResponse(ComponentVersion version) => new(version.Id, version.ComponentId, version.VersionNumber, version.Status, version.PublishedAt, version.Notes, version.Fields.OrderBy(x => x.Order).Select(x => new ComponentFieldResponse(x.Id, x.Key, x.Label, x.HelpText, x.Order, x.IsRequired, x.MinOccurrences, x.MaxOccurrences, x.PrimitiveType, x.NestedComponentId, x.FieldConfig.Clone())).ToArray());
}

public sealed record ComponentSummaryResponse(Guid Id, string Name, string Slug, string? Description, Guid? CurrentVersionId);
public sealed record ComponentResponse(Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description, ComponentVersionResponse? CurrentVersion);
public sealed record ComponentVersionResponse(Guid Id, Guid ComponentId, int VersionNumber, TemplateVersionStatus Status, DateTimeOffset? PublishedAt, string? Notes, IReadOnlyList<ComponentFieldResponse> Fields);
public sealed record ComponentFieldResponse(Guid Id, string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, PrimitiveType? PrimitiveType, Guid? NestedComponentId, JsonElement? FieldConfig);
public sealed record ComponentRequest(string Name, string Slug, string? Description);
public sealed record ComponentVersionRequest(string? Notes);
public sealed record ComponentFieldRequest(string Key, string Label, string? HelpText, int Order, bool IsRequired, int MinOccurrences, int? MaxOccurrences, PrimitiveType? PrimitiveType, Guid? NestedComponentId, JsonElement? FieldConfig);
