using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/content")]
[RequireRole(UserRole.Reader)]
public sealed class ContentController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly IContentValidator contentValidator;
    private readonly IContentSearchVectorBuilder searchVectorBuilder;
    private readonly IContentLifecycleService lifecycleService;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IWebhookQueue webhookQueue;

    public ContentController(CmsifyDbContext dbContext, IContentValidator contentValidator, IContentSearchVectorBuilder searchVectorBuilder, IContentLifecycleService lifecycleService, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, IWebhookQueue webhookQueue)
    {
        this.dbContext = dbContext;
        this.contentValidator = contentValidator;
        this.searchVectorBuilder = searchVectorBuilder;
        this.lifecycleService = lifecycleService;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.webhookQueue = webhookQueue;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ContentItemSummaryResponse>>> List(Guid workspaceId, [FromQuery] ContentListQuery query, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return Forbid();
        }

        var items = BaseContentQuery(workspaceId).AsNoTracking();
        if (query.TemplateVersionId.HasValue)
        {
            items = items.Where(content => content.TemplateVersionId == query.TemplateVersionId.Value);
        }

        if (query.TemplateId.HasValue)
        {
            items = items.Where(content => dbContext.TemplateVersions.Any(version => version.Id == content.TemplateVersionId && version.TemplateId == query.TemplateId.Value));
        }

        if (query.Status.HasValue)
        {
            items = items.Where(content => content.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.LocaleCode))
        {
            items = items.Where(content => content.LocaleCode == query.LocaleCode);
        }

        if (query.TranslationGroupId.HasValue)
        {
            items = items.Where(content => content.TranslationGroupId == query.TranslationGroupId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Slug))
        {
            items = items.Where(content => content.Slug == query.Slug);
        }

        if (!string.IsNullOrWhiteSpace(query.Tags))
        {
            var tags = query.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NormalizeTag).ToArray();
            foreach (var tag in tags)
            {
                items = items.Where(content => dbContext.ContentItemTags.Any(join => join.ContentItemId == content.Id && dbContext.Tags.Any(candidate => candidate.Id == join.TagId && candidate.Name == tag && !candidate.IsDeleted)));
            }
        }

        if (query.CreatedAfter.HasValue)
        {
            items = items.Where(content => content.CreatedAt >= query.CreatedAfter.Value);
        }

        if (query.CreatedBefore.HasValue)
        {
            items = items.Where(content => content.CreatedAt <= query.CreatedBefore.Value);
        }

        if (query.PublishedAfter.HasValue)
        {
            items = items.Where(content => content.PublishedAt >= query.PublishedAfter.Value);
        }

        if (query.PublishedBefore.HasValue)
        {
            items = items.Where(content => content.PublishedAt <= query.PublishedBefore.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            items = items.Where(content => EF.Functions.ILike(content.Slug ?? string.Empty, $"%{query.Q}%") || content.FieldValues.Any(value => value.TextValue != null && EF.Functions.ILike(value.TextValue, $"%{query.Q}%")));
        }

        items = query.SortBy switch
        {
            "updatedAt" => query.SortDesc ? items.OrderByDescending(content => content.UpdatedAt) : items.OrderBy(content => content.UpdatedAt),
            "publishedAt" => query.SortDesc ? items.OrderByDescending(content => content.PublishedAt) : items.OrderBy(content => content.PublishedAt),
            "slug" => query.SortDesc ? items.OrderByDescending(content => content.Slug) : items.OrderBy(content => content.Slug),
            _ => query.SortDesc ? items.OrderByDescending(content => content.CreatedAt) : items.OrderBy(content => content.CreatedAt)
        };

        var total = await items.CountAsync(ct);
        var pageItems = await items.Skip(ControllerHelpers.Offset(query.Page, query.PageSize)).Take(ControllerHelpers.Limit(query.PageSize)).ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var item in pageItems)
        {
            responses.Add(await ToSummaryResponseAsync(item, ct));
        }

        return Ok(new PagedResponse<ContentItemSummaryResponse>(responses, total, Math.Max(1, query.Page), ControllerHelpers.Limit(query.PageSize)));
    }

    [HttpPost]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Create(Guid workspaceId, CreateContentItemRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return Forbid();
        }

        var version = await LoadTemplateVersionAsync(request.TemplateVersionId, ct);
        if (version is null || !await TemplateVersionBelongsToWorkspaceAsync(version.Id, workspaceId, ct))
        {
            return NotFound();
        }

        var content = new ContentItem
        {
            WorkspaceId = workspaceId,
            TemplateVersionId = request.TemplateVersionId,
            Slug = request.Slug,
            LocaleCode = request.LocaleCode,
            TranslationGroupId = request.TranslationGroupId,
            CreatedByUserId = currentActor.UserId,
            UpdatedByUserId = currentActor.UserId
        };
        ApplyFieldValues(content, request.Fields);
        var validation = contentValidator.Validate(content, version);
        if (!validation.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        content.SearchVector = searchVectorBuilder.Build(content, version);
        await ApplyTagsAsync(content, workspaceId, request.Tags, ct);
        dbContext.ContentItems.Add(content);
        await dbContext.SaveChangesAsync(ct);
        await EnqueueContentEventAsync("content.created", content, ct);
        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return CreatedAtAction(nameof(Get), new { workspaceId, id = content.Id }, await ToDetailResponseAsync(content.Id, ct: ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContentItemDetailResponse>> Get(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return Forbid();
        }

        var content = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (content is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return Ok(await ToDetailResponseAsync(id, ct: ct));
    }

    [HttpGet("by-slug/{slug}")]
    public async Task<ActionResult<ContentItemDetailResponse>> GetBySlug(Guid workspaceId, string slug, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return Forbid();
        }

        var content = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(item => item.Slug == slug, ct);
        if (content is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return Ok(await ToDetailResponseAsync(content.Id, ct: ct));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Update(Guid workspaceId, Guid id, UpdateContentItemRequest request, CancellationToken ct)
    {
        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(content.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        if (content.Status is not (ContentStatus.Draft or ContentStatus.Review))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only draft or review content can be updated");
        }

        content.Slug = request.Slug;
        content.LocaleCode = request.LocaleCode;
        content.TranslationGroupId = request.TranslationGroupId;
        content.PublishAt = request.PublishAt;
        content.UpdatedAt = DateTimeOffset.UtcNow;
        content.UpdatedByUserId = currentActor.UserId;
        dbContext.ContentFieldValues.RemoveRange(content.FieldValues);
        content.FieldValues.Clear();
        ApplyFieldValues(content, request.Fields);
        dbContext.ContentItemTags.RemoveRange(content.Tags);
        content.Tags.Clear();
        await ApplyTagsAsync(content, workspaceId, request.Tags, ct);

        var version = await LoadTemplateVersionAsync(content.TemplateVersionId, ct);
        var validation = contentValidator.Validate(content, version!);
        if (!validation.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        content.SearchVector = searchVectorBuilder.Build(content, version!);
        await dbContext.SaveChangesAsync(ct);
        await EnqueueContentEventAsync("content.updated", content, ct);
        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return Ok(await ToDetailResponseAsync(content.Id, ct: ct));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(content.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        if (content.Status is not (ContentStatus.Draft or ContentStatus.Archived))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only draft or archived content can be deleted");
        }

        var referencedBy = await ReferencingContentIdsAsync(id, onlyReferenceFields: true, ct);
        if (referencedBy.Count > 0)
        {
            return this.Error(StatusCodes.Status409Conflict, "referenced-by-other-entity", "Content item is referenced by other content", extensions: new Dictionary<string, object?> { ["referencedBy"] = referencedBy });
        }

        SoftDelete(content);
        await dbContext.SaveChangesAsync(ct);
        await EnqueueContentEventAsync("content.deleted", content, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/upgrade-version")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> UpgradeVersion(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        var currentVersion = await dbContext.TemplateVersions.AsNoTracking().FirstAsync(version => version.Id == content.TemplateVersionId, ct);
        var target = await dbContext.TemplateVersions
            .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
            .Where(version => version.TemplateId == currentVersion.TemplateId && version.Status == TemplateVersionStatus.Published && !version.IsDeleted)
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync(ct);
        if (target is null)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "No published version is available");
        }

        content.TemplateVersionId = target.Id;
        var targetFieldIds = target.Fields.Select(field => field.Id).ToHashSet();
        dbContext.ContentFieldValues.RemoveRange(content.FieldValues.Where(value => !targetFieldIds.Contains(value.FieldId)));
        var validation = contentValidator.Validate(content, target);
        if (!validation.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content does not satisfy the target template version", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        content.SearchVector = searchVectorBuilder.Build(content, target);
        content.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return Ok(await ToDetailResponseAsync(content.Id, ct: ct));
    }

    [HttpPost("{id:guid}/submit")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentItemDetailResponse>> Submit(Guid workspaceId, Guid id, CancellationToken ct) => Transition(workspaceId, id, ContentStatus.Review, null, ct);

    [HttpPost("{id:guid}/approve")]
    [RequireRole(UserRole.TemplateAdmin)]
    public Task<ActionResult<ContentItemDetailResponse>> Approve(Guid workspaceId, Guid id, CancellationToken ct) => Transition(workspaceId, id, ContentStatus.Approved, null, ct);

    [HttpPost("{id:guid}/reject")]
    [RequireRole(UserRole.TemplateAdmin)]
    public Task<ActionResult<ContentItemDetailResponse>> Reject(Guid workspaceId, Guid id, RejectContentRequest request, CancellationToken ct) => Transition(workspaceId, id, ContentStatus.Draft, request.Reason, ct);

    [HttpPost("{id:guid}/publish")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Publish(Guid workspaceId, Guid id, PublishContentRequest? request, CancellationToken ct)
    {
        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        if (request?.PublishAt is not null)
        {
            if (content.Status != ContentStatus.Approved)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Content must be approved before scheduling publication");
            }

            content.PublishAt = request.PublishAt;
            content.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(ct);
            return Ok(await ToDetailResponseAsync(content.Id, ct: ct));
        }

        return await Transition(workspaceId, id, ContentStatus.Published, null, ct);
    }

    [HttpPost("{id:guid}/archive")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentItemDetailResponse>> Archive(Guid workspaceId, Guid id, CancellationToken ct) => Transition(workspaceId, id, ContentStatus.Archived, null, ct);

    [HttpPost("{id:guid}/restore")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentItemDetailResponse>> Restore(Guid workspaceId, Guid id, CancellationToken ct) => Transition(workspaceId, id, ContentStatus.Draft, null, ct);

    [HttpPost("{id:guid}/link-translation")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<IReadOnlyList<ContentItemSummaryResponse>>> LinkTranslation(Guid workspaceId, Guid id, LinkTranslationRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return Forbid();
        }

        var source = await BaseContentQuery(workspaceId).FirstOrDefaultAsync(content => content.Id == id, ct);
        var target = await BaseContentQuery(workspaceId).FirstOrDefaultAsync(content => content.Id == request.TargetContentItemId, ct);
        if (source is null || target is null)
        {
            return NotFound();
        }

        var groupId = source.TranslationGroupId ?? target.TranslationGroupId ?? Guid.CreateVersion7();
        source.TranslationGroupId = groupId;
        target.TranslationGroupId = groupId;
        await dbContext.SaveChangesAsync(ct);
        return await GetTranslations(workspaceId, id, ct);
    }

    [HttpGet("{id:guid}/translations")]
    public async Task<ActionResult<IReadOnlyList<ContentItemSummaryResponse>>> GetTranslations(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return Forbid();
        }

        var source = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(content => content.Id == id, ct);
        if (source is null)
        {
            return NotFound();
        }

        if (!source.TranslationGroupId.HasValue)
        {
            return Ok(Array.Empty<ContentItemSummaryResponse>());
        }

        var translations = await BaseContentQuery(workspaceId).AsNoTracking().Where(content => content.TranslationGroupId == source.TranslationGroupId).OrderBy(content => content.LocaleCode).ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var translation in translations)
        {
            responses.Add(await ToSummaryResponseAsync(translation, ct));
        }

        return Ok(responses);
    }

    private async Task<ActionResult<ContentItemDetailResponse>> Transition(Guid workspaceId, Guid id, ContentStatus targetStatus, string? reason, CancellationToken ct)
    {
        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        if (!lifecycleService.CanTransition(content.Status, targetStatus))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Invalid content state transition", $"Content cannot transition from {content.Status} to {targetStatus}.");
        }

        await lifecycleService.TransitionAsync(content, targetStatus, currentActor.UserId ?? Guid.Empty);
        if (targetStatus == ContentStatus.Published)
        {
            content.PublishAt = null;
        }

        await dbContext.SaveChangesAsync(ct);
        await EnqueueContentEventAsync("content.status_changed", content, ct);
        if (targetStatus is ContentStatus.Published or ContentStatus.Archived)
        {
            await EnqueueContentEventAsync(targetStatus == ContentStatus.Published ? "content.published" : "content.archived", content, ct);
        }

        _ = reason;
        return Ok(await ToDetailResponseAsync(content.Id, ct: ct));
    }

    private IQueryable<ContentItem> BaseContentQuery(Guid workspaceId) =>
        dbContext.ContentItems.Where(content => content.WorkspaceId == workspaceId && !content.IsDeleted);

    private async Task<ContentItem?> LoadContentForEditAsync(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return null;
        }

        return await BaseContentQuery(workspaceId)
            .Include(content => content.FieldValues)
            .Include(content => content.Tags)
            .FirstOrDefaultAsync(content => content.Id == id, ct);
    }

    private async Task<TemplateVersion?> LoadTemplateVersionAsync(Guid id, CancellationToken ct) =>
        await dbContext.TemplateVersions
            .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
            .FirstOrDefaultAsync(version => version.Id == id && !version.IsDeleted, ct);

    private async Task<bool> TemplateVersionBelongsToWorkspaceAsync(Guid versionId, Guid workspaceId, CancellationToken ct) =>
        await dbContext.TemplateVersions.AnyAsync(version => version.Id == versionId && dbContext.Templates.Any(template => template.Id == version.TemplateId && template.WorkspaceId == workspaceId && !template.IsDeleted), ct);

    private static void ApplyFieldValues(ContentItem content, IEnumerable<ContentFieldValueRequest> values)
    {
        foreach (var value in values)
        {
            content.FieldValues.Add(new ContentFieldValue
            {
                ContentItemId = content.Id,
                FieldId = value.FieldId,
                Order = value.Order,
                ValueKind = value.ValueKind,
                TextValue = value.TextValue,
                BoolValue = value.BoolValue,
                MediaAssetId = value.MediaAssetId,
                FileAssetId = value.FileAssetId,
                ChildContentItemId = value.ChildContentItemId,
                JsonValue = value.JsonValue.Clone()
            });
        }
    }

    private async Task ApplyTagsAsync(ContentItem content, Guid workspaceId, IEnumerable<string> tags, CancellationToken ct)
    {
        foreach (var tagName in tags.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct())
        {
            var tag = await dbContext.Tags.FirstOrDefaultAsync(candidate => candidate.WorkspaceId == workspaceId && candidate.Name == tagName && !candidate.IsDeleted, ct);
            if (tag is null)
            {
                tag = new Tag { WorkspaceId = workspaceId, Name = tagName };
                dbContext.Tags.Add(tag);
            }

            content.Tags.Add(new ContentItemTag { ContentItemId = content.Id, TagId = tag.Id });
        }
    }

    private async Task<ContentItemSummaryResponse> ToSummaryResponseAsync(ContentItem content, CancellationToken ct)
    {
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.Id == content.TemplateVersionId)
            .Select(version => dbContext.Templates.Where(template => template.Id == version.TemplateId).Select(template => template.Name).First())
            .FirstAsync(ct);
        var tags = await GetTagNamesAsync(content.Id, ct);
        return new ContentItemSummaryResponse(content.Id, content.TemplateVersionId, template, content.Status, content.Slug, content.LocaleCode, content.TranslationGroupId, tags, content.CreatedAt, content.UpdatedAt, content.PublishedAt);
    }

    private async Task<ContentItemDetailResponse> ToDetailResponseAsync(Guid id, int depth = 0, CancellationToken ct = default)
    {
        var content = await dbContext.ContentItems.AsNoTracking().Include(item => item.FieldValues).FirstAsync(item => item.Id == id, ct);
        var summary = await ToSummaryResponseAsync(content, ct);
        var fields = new List<ContentFieldValueResponse>();
        var templateFields = await dbContext.TemplateFields.AsNoTracking().Where(field => field.TemplateVersionId == content.TemplateVersionId).ToDictionaryAsync(field => field.Id, ct);
        foreach (var value in content.FieldValues.OrderBy(value => templateFields.GetValueOrDefault(value.FieldId)?.Order ?? 0).ThenBy(value => value.Order))
        {
            templateFields.TryGetValue(value.FieldId, out var field);
            ContentItemDetailResponse? child = null;
            if (depth < 8 && value.ChildContentItemId.HasValue && await dbContext.ContentItems.AnyAsync(childContent => childContent.Id == value.ChildContentItemId && !childContent.IsDeleted, ct))
            {
                child = await ToDetailResponseAsync(value.ChildContentItemId.Value, depth + 1, ct);
            }

            fields.Add(new ContentFieldValueResponse(value.FieldId, field?.Key, field?.Label, value.Order, value.ValueKind, value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, child, value.JsonValue.Clone()));
        }

        return new ContentItemDetailResponse(summary.Id, summary.TemplateVersionId, summary.TemplateName, summary.Status, summary.Slug, summary.LocaleCode, summary.TranslationGroupId, summary.Tags, summary.CreatedAt, summary.UpdatedAt, summary.PublishedAt, fields);
    }

    private async Task<IReadOnlyList<string>> GetTagNamesAsync(Guid contentItemId, CancellationToken ct) =>
        await dbContext.ContentItemTags.AsNoTracking()
            .Where(join => join.ContentItemId == contentItemId)
            .Join(dbContext.Tags.AsNoTracking(), join => join.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .OrderBy(tag => tag)
            .ToListAsync(ct);

    private async Task<IReadOnlyList<Guid>> ReferencingContentIdsAsync(Guid id, bool onlyReferenceFields, CancellationToken ct)
    {
        var query = dbContext.ContentFieldValues.AsNoTracking().Where(value => value.ChildContentItemId == id);
        if (onlyReferenceFields)
        {
            query = query.Where(value => dbContext.TemplateFields.Any(field => field.Id == value.FieldId && field.CompositionMode == CompositionMode.Reference));
        }

        return await query.Select(value => value.ContentItemId).Distinct().ToListAsync(ct);
    }

    private void SoftDelete(ContentItem content)
    {
        content.IsDeleted = true;
        content.DeletedAt = DateTimeOffset.UtcNow;
        content.DeletedByUserId = currentActor.UserId;
        content.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task EnqueueContentEventAsync(string eventType, ContentItem content, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(new { contentItemId = content.Id, workspaceId = content.WorkspaceId, templateVersionId = content.TemplateVersionId, status = content.Status.ToString() });
        await webhookQueue.EnqueueAsync(new WebhookEvent(eventType, content.WorkspaceId, content.Id, payload, DateTimeOffset.UtcNow), ct);
    }

    private static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();
}

public sealed record ContentListQuery(string? Q, Guid? TemplateVersionId, Guid? TemplateId, ContentStatus? Status, string? LocaleCode, Guid? TranslationGroupId, string? Slug, string? Tags, DateTimeOffset? CreatedAfter, DateTimeOffset? CreatedBefore, DateTimeOffset? PublishedAfter, DateTimeOffset? PublishedBefore, string? SortBy = "createdAt", bool SortDesc = true, int Page = 1, int PageSize = 20);
public sealed record CreateContentItemRequest(Guid TemplateVersionId, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);
public sealed record UpdateContentItemRequest(string? Slug, string? LocaleCode, Guid? TranslationGroupId, DateTimeOffset? PublishAt, IReadOnlyList<string> Tags, IReadOnlyList<ContentFieldValueRequest> Fields);
public sealed record ContentFieldValueRequest(Guid FieldId, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, JsonElement? JsonValue);
public sealed record RejectContentRequest(string Reason);
public sealed record PublishContentRequest(DateTimeOffset? PublishAt);
public sealed record LinkTranslationRequest(Guid TargetContentItemId);
public sealed record ContentItemSummaryResponse(Guid Id, Guid TemplateVersionId, string TemplateName, ContentStatus Status, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);
public sealed record ContentItemDetailResponse(Guid Id, Guid TemplateVersionId, string TemplateName, ContentStatus Status, string? Slug, string? LocaleCode, Guid? TranslationGroupId, IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt, IReadOnlyList<ContentFieldValueResponse> Fields);
public sealed record ContentFieldValueResponse(Guid FieldId, string? Key, string? Label, int Order, ValueKind ValueKind, string? TextValue, bool? BoolValue, Guid? MediaAssetId, Guid? FileAssetId, Guid? ChildContentItemId, ContentItemDetailResponse? Child, JsonElement? JsonValue);
