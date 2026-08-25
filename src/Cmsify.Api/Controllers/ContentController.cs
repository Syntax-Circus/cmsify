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
using SyntaxCircus.Cmsify.Contracts;
using CompositionMode = Cmsify.Core.Domain.Enums.CompositionMode;
using ContentStatus = Cmsify.Core.Domain.Enums.ContentStatus;
using ContentVersionStatus = Cmsify.Core.Domain.Enums.ContentVersionStatus;
using PrimitiveType = Cmsify.Core.Domain.Enums.PrimitiveType;
using TemplateVersionStatus = Cmsify.Core.Domain.Enums.TemplateVersionStatus;
using UserRole = Cmsify.Core.Domain.Enums.UserRole;
using ValueKind = Cmsify.Core.Domain.Enums.ValueKind;
using ContentListQuery = SyntaxCircus.Cmsify.Contracts.ContentListQuery;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;

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
    private readonly IContentPublishingService publishingService;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IWebhookQueue webhookQueue;

    public ContentController(CmsifyDbContext dbContext, IContentValidator contentValidator, IContentSearchVectorBuilder searchVectorBuilder, IContentLifecycleService lifecycleService, IContentPublishingService publishingService, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, IWebhookQueue webhookQueue)
    {
        this.dbContext = dbContext;
        this.contentValidator = contentValidator;
        this.searchVectorBuilder = searchVectorBuilder;
        this.lifecycleService = lifecycleService;
        this.publishingService = publishingService;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.webhookQueue = webhookQueue;
    }

    [HttpGet]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>>> List(Guid workspaceId, [FromQuery] ContentListQuery query, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        if (query.Resolve)
        {
            return await ListResolvedAsync(workspaceId, query, ct);
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
            var status = query.Status.Value.ToCore();
            items = items.Where(content => content.Status == status);
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
        if (!ControllerHelpers.TryOffset(query.Page, query.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>([], total, query.Page, query.PageSize));
        }

        var pageItems = await items.Skip(offset).Take(ControllerHelpers.Limit(query.PageSize)).ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var item in pageItems)
        {
            responses.Add(await ToSummaryResponseAsync(item, ct));
        }

        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>(responses, total, query.Page, query.PageSize));
    }

    [HttpPost]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Create(Guid workspaceId, CreateContentItemRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        if (request.Slug is not null && !SlugRules.IsValid(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", SlugRules.ValidationMessage);
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
        if (await ValidatePickListValuesAsync(content, version, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", pickListError);
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
    public async Task<ActionResult<ContentItemDetailResponse>> Get(Guid workspaceId, Guid id, [FromQuery] bool resolve = false, [FromQuery] DateTimeOffset? asOf = null, CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        if (resolve)
        {
            var resolved = await ResolvePublishedVersionAsync(workspaceId, contentItemId: id, slug: null, asOf ?? DateTimeOffset.UtcNow, ct);
            if (resolved is null)
            {
                return NotFound();
            }

            Response.Headers.ETag = ControllerHelpers.ETag(resolved.PublishedAt);
            return Ok(await ToResolvedDetailResponseAsync(resolved, asOf ?? DateTimeOffset.UtcNow, ct: ct));
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
    public async Task<ActionResult<ContentItemDetailResponse>> GetBySlug(Guid workspaceId, string slug, [FromQuery] DateTimeOffset? asOf = null, CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var resolvedAsOf = asOf ?? DateTimeOffset.UtcNow;
        var content = await ResolvePublishedVersionAsync(workspaceId, contentItemId: null, slug, resolvedAsOf, ct);
        if (content is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(content.PublishedAt);
        return Ok(await ToResolvedDetailResponseAsync(content, resolvedAsOf, ct: ct));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Update(Guid workspaceId, Guid id, UpdateContentItemRequest request, CancellationToken ct)
    {
        if (request.Slug is not null && !SlugRules.IsValid(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", SlugRules.ValidationMessage);
        }

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
        if (!FieldValuesMatch(content.FieldValues, request.Fields))
        {
            dbContext.ContentFieldValues.RemoveRange(content.FieldValues);
            content.FieldValues.Clear();
            ApplyFieldValues(content, request.Fields);
        }

        var existingTags = await GetTagNamesAsync(content.Id, ct);
        if (!TagsMatch(existingTags, request.Tags))
        {
            dbContext.ContentItemTags.RemoveRange(content.Tags);
            content.Tags.Clear();
            await ApplyTagsAsync(content, workspaceId, request.Tags, ct);
        }

        var version = await LoadTemplateVersionAsync(content.TemplateVersionId, ct);
        var validation = contentValidator.Validate(content, version!);
        if (!validation.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }
        if (await ValidatePickListValuesAsync(content, version!, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", pickListError);
        }

        content.SearchVector = searchVectorBuilder.Build(content, version!);
        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

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
        if (await ValidatePickListValuesAsync(content, target, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content does not satisfy the target template version", pickListError);
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
    public async Task<ActionResult<PublishContentResponse>> Publish(Guid workspaceId, Guid id, PublishContentRequest? request, CancellationToken ct)
    {
        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        var templateVersion = await LoadTemplateVersionAsync(content.TemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
        }
        if (await ValidatePickListValuesAsync(content, templateVersion, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", pickListError);
        }

        var effectiveRangeResult = BuildEffectiveRange(request?.EffectiveStartAt, request?.EffectiveEndAt);
        if (effectiveRangeResult.Result is not null)
        {
            return effectiveRangeResult.Result;
        }
        var effectiveRange = effectiveRangeResult.Value!;

        if (request?.PublishAt is not null)
        {
            if (content.Status != ContentStatus.Approved)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Content must be approved before scheduling publication");
            }

            content.PublishAt = request.PublishAt;
            content.PendingEffectiveStartAt = effectiveRange.StartAt;
            content.PendingEffectiveEndAt = effectiveRange.EndAt;
            content.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(ct);
            return Ok(new PublishContentResponse(await ToDetailResponseAsync(content.Id, ct: ct), []));
        }

        if (!lifecycleService.CanTransition(content.Status, ContentStatus.Published))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Invalid content state transition", $"Content cannot transition from {content.Status} to {ContentStatus.Published}.");
        }

        await lifecycleService.TransitionAsync(content, ContentStatus.Published, currentActor.UserId ?? Guid.Empty);
        content.PublishAt = null;
        content.PendingEffectiveStartAt = null;
        content.PendingEffectiveEndAt = null;
        var publishResult = await publishingService.PublishSnapshotAsync(content, effectiveRange, actorUserId: currentActor.UserId, ct: ct);
        await dbContext.SaveChangesAsync(ct);
        await EnqueueContentEventAsync("content.status_changed", content, ct);
        await EnqueueContentEventAsync("content.published", content, ct);

        return Ok(new PublishContentResponse(await ToDetailResponseAsync(content.Id, ct: ct), publishResult.Warnings));
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
            return NotFound();
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
        var translations = await BaseContentQuery(workspaceId).AsNoTracking()
            .Where(content => content.TranslationGroupId == groupId)
            .OrderBy(content => content.LocaleCode)
            .ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var translation in translations)
        {
            responses.Add(await ToSummaryResponseAsync(translation, ct));
        }

        return Ok(responses);
    }

    [HttpGet("{id:guid}/translations")]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>>> GetTranslations(Guid workspaceId, Guid id, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var source = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(content => content.Id == id, ct);
        if (source is null)
        {
            return NotFound();
        }

        if (!source.TranslationGroupId.HasValue)
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>([], 0, pagination.Page, pagination.PageSize));
        }

        var query = BaseContentQuery(workspaceId).AsNoTracking().Where(content => content.TranslationGroupId == source.TranslationGroupId).OrderBy(content => content.LocaleCode);
        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var translations = await query.Skip(offset).Take(pagination.PageSize).ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var translation in translations)
        {
            responses.Add(await ToSummaryResponseAsync(translation, ct));
        }

        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>(responses, total, pagination.Page, pagination.PageSize));
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

        if (targetStatus == ContentStatus.Published)
        {
            var templateVersion = await LoadTemplateVersionAsync(content.TemplateVersionId, ct);
            if (templateVersion is null)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
            }
            if (await ValidatePickListValuesAsync(content, templateVersion, ct) is { } pickListError)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", pickListError);
            }
        }

        await lifecycleService.TransitionAsync(content, targetStatus, currentActor.UserId ?? Guid.Empty);
        if (targetStatus == ContentStatus.Published)
        {
            content.PublishAt = null;
            content.PendingEffectiveStartAt = null;
            content.PendingEffectiveEndAt = null;
            await publishingService.PublishSnapshotAsync(content, new ContentEffectiveRange(null, null), actorUserId: currentActor.UserId, ct: ct);
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

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentVersionSummaryResponse>>> ListVersions(Guid workspaceId, Guid id, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var content = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (content is null)
        {
            return NotFound();
        }

        var query = dbContext.ContentVersions.AsNoTracking()
            .Where(version => version.ContentItemId == id)
            .OrderByDescending(version => version.VersionNumber);
        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentVersionSummaryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var versions = await query.Skip(offset).Take(pagination.PageSize).ToListAsync(ct);
        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentVersionSummaryResponse>(versions.Select(ToVersionSummary).ToList(), total, pagination.Page, pagination.PageSize));
    }

    [HttpGet("{id:guid}/versions/{versionNumber:int}")]
    public async Task<ActionResult<ContentVersionDetailResponse>> GetVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var content = await BaseContentQuery(workspaceId).AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (content is null)
        {
            return NotFound();
        }

        var version = await dbContext.ContentVersions.AsNoTracking()
            .Include(version => version.FieldValues)
            .FirstOrDefaultAsync(version => version.ContentItemId == id && version.VersionNumber == versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        return Ok(await ToVersionDetailAsync(version, ct));
    }

    [HttpPost("{id:guid}/versions/{versionNumber:int}/rollback")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Rollback(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        var content = await LoadContentForEditAsync(workspaceId, id, ct);
        if (content is null)
        {
            return NotFound();
        }

        var target = await dbContext.ContentVersions
            .Include(version => version.FieldValues)
            .FirstOrDefaultAsync(version => version.ContentItemId == id && version.VersionNumber == versionNumber, ct);
        if (target is null)
        {
            return NotFound();
        }

        if (target.Status is not (ContentVersionStatus.Published or ContentVersionStatus.Retired))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only published or retired versions can be rolled back to");
        }

        var templateVersion = await LoadTemplateVersionAsync(target.TemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status409Conflict, "template-version-unavailable", "The template version this snapshot was created against is no longer available");
        }

        content.TemplateVersionId = target.TemplateVersionId;
        content.Slug = target.Slug;
        content.LocaleCode = target.LocaleCode;
        content.TranslationGroupId = target.TranslationGroupId;
        dbContext.ContentFieldValues.RemoveRange(content.FieldValues);
        content.FieldValues.Clear();
        foreach (var value in target.FieldValues)
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
                JsonValue = value.JsonValue?.Clone()
            });
        }

        dbContext.ContentItemTags.RemoveRange(content.Tags);
        content.Tags.Clear();
        await ApplyTagsAsync(content, workspaceId, target.Tags, ct);

        var validation = contentValidator.Validate(content, templateVersion);
        if (!validation.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Snapshot does not satisfy its template version", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }
        if (await ValidatePickListValuesAsync(content, templateVersion, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Snapshot does not satisfy its template version", pickListError);
        }

        var previousActiveNumber = await dbContext.ContentVersions.AsNoTracking()
            .Where(version => version.ContentItemId == id && version.Status == ContentVersionStatus.Published)
            .Select(version => (int?)version.VersionNumber)
            .FirstOrDefaultAsync(ct);

        content.SearchVector = searchVectorBuilder.Build(content, templateVersion);
        content.UpdatedAt = DateTimeOffset.UtcNow;
        content.UpdatedByUserId = currentActor.UserId;
        content.Status = ContentStatus.Published;
        content.PublishedAt = DateTimeOffset.UtcNow;
        content.PublishAt = null;
        content.PendingEffectiveStartAt = null;
        content.PendingEffectiveEndAt = null;
        content.ArchivedAt = null;

        var snapshot = await publishingService.PublishSnapshotAsync(
            content,
            new ContentEffectiveRange(target.EffectiveStartAt, target.EffectiveEndAt),
            target.VersionNumber,
            currentActor.UserId,
            ct);
        await dbContext.SaveChangesAsync(ct);

        var payload = JsonSerializer.SerializeToElement(new
        {
            contentItemId = content.Id,
            workspaceId = content.WorkspaceId,
            fromVersionNumber = previousActiveNumber,
            toVersionNumber = target.VersionNumber,
            newVersionNumber = snapshot.Version.VersionNumber
        });
        await webhookQueue.EnqueueAsync(new WebhookEvent("content.rolled_back", content.WorkspaceId, content.Id, payload, DateTimeOffset.UtcNow), ct);
        await EnqueueContentEventAsync("content.published", content, ct);
        return Ok(await ToDetailResponseAsync(content.Id, ct: ct));
    }

    private static ContentVersionSummaryResponse ToVersionSummary(ContentVersion version) =>
        new(version.Id, version.ContentItemId, version.VersionNumber, version.Status.ToContract(), version.TemplateVersionId,
            version.Slug, version.LocaleCode, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishedAt, version.RetiredAt, version.PublishedByUserId,
            version.RolledBackFromVersionNumber, version.Tags.ToList());

    private async Task<ContentVersionDetailResponse> ToVersionDetailAsync(ContentVersion version, CancellationToken ct)
    {
        var templateName = await dbContext.TemplateVersions.AsNoTracking()
            .Where(tv => tv.Id == version.TemplateVersionId)
            .Select(tv => dbContext.Templates.Where(template => template.Id == tv.TemplateId).Select(template => template.Name).First())
            .FirstOrDefaultAsync(ct) ?? string.Empty;
        var templateFields = await dbContext.TemplateFields.AsNoTracking()
            .Where(field => field.TemplateVersionId == version.TemplateVersionId)
            .ToDictionaryAsync(field => field.Id, ct);
        var fields = version.FieldValues
            .OrderBy(value => templateFields.GetValueOrDefault(value.FieldId)?.Order ?? 0)
            .ThenBy(value => value.Order)
            .Select(value =>
            {
                templateFields.TryGetValue(value.FieldId, out var field);
                return new ContentVersionFieldValueResponse(value.FieldId, field?.Key, field?.Label, value.Order,
                    value.ValueKind.ToContract(), value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId,
                    value.ChildContentItemId, value.JsonValue?.Clone(), value.DisplayLabel);
            })
            .ToList();
        return new ContentVersionDetailResponse(version.Id, version.ContentItemId, version.VersionNumber,
            version.Status.ToContract(), version.TemplateVersionId, templateName, version.Slug, version.LocaleCode,
            version.TranslationGroupId, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishedAt, version.RetiredAt, version.PublishedByUserId,
            version.RolledBackFromVersionNumber, version.Tags.ToList(), fields);
    }

    private (ContentEffectiveRange? Value, ObjectResult? Result) BuildEffectiveRange(DateTimeOffset? effectiveStartAt, DateTimeOffset? effectiveEndAt)
    {
        if (effectiveStartAt.HasValue != effectiveEndAt.HasValue)
        {
            return (null, this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid effective range", "Provide both effectiveStartAt and effectiveEndAt, or neither."));
        }

        if (effectiveStartAt.HasValue && effectiveStartAt.Value >= effectiveEndAt!.Value)
        {
            return (null, this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid effective range", "effectiveStartAt must be before effectiveEndAt."));
        }

        return (new ContentEffectiveRange(effectiveStartAt, effectiveEndAt), null);
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
                ValueKind = value.ValueKind.ToCore(),
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

    private static bool FieldValuesMatch(IEnumerable<ContentFieldValue> existing, IEnumerable<ContentFieldValueRequest> requested)
    {
        var existingValues = existing
            .OrderBy(value => value.FieldId)
            .ThenBy(value => value.Order)
            .ToList();
        var requestedValues = requested
            .OrderBy(value => value.FieldId)
            .ThenBy(value => value.Order)
            .ToList();

        if (existingValues.Count != requestedValues.Count)
        {
            return false;
        }

        for (var index = 0; index < existingValues.Count; index++)
        {
            var current = existingValues[index];
            var next = requestedValues[index];
            if (current.FieldId != next.FieldId
                || current.Order != next.Order
                || current.ValueKind != next.ValueKind.ToCore()
                || current.TextValue != next.TextValue
                || current.BoolValue != next.BoolValue
                || current.MediaAssetId != next.MediaAssetId
                || current.FileAssetId != next.FileAssetId
                || current.ChildContentItemId != next.ChildContentItemId
                || !JsonValuesMatch(current.JsonValue, next.JsonValue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TagsMatch(IEnumerable<string> existing, IEnumerable<string> requested) =>
        existing.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct().Order()
            .SequenceEqual(requested.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct().Order());

    private static bool JsonValuesMatch(JsonElement? left, JsonElement? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue || left.Value.GetRawText() == right!.Value.GetRawText());

    private async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>>> ListResolvedAsync(Guid workspaceId, ContentListQuery query, CancellationToken ct)
    {
        if (query.Status.HasValue && query.Status.Value != SyntaxCircus.Cmsify.Contracts.ContentStatus.Published)
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>([], 0, query.Page, query.PageSize));
        }

        var asOf = query.AsOf ?? DateTimeOffset.UtcNow;
        var versions = await dbContext.ContentVersions.AsNoTracking()
            .Where(version => version.WorkspaceId == workspaceId && version.Status == ContentVersionStatus.Published)
            .Where(version =>
                (version.EffectiveStartAt == null && version.EffectiveEndAt == null)
                || (version.EffectiveStartAt <= asOf && asOf < version.EffectiveEndAt))
            .Where(version => !dbContext.ContentItems.Any(content => content.Id == version.ContentItemId && content.IsDeleted))
            .ToListAsync(ct);

        if (query.TemplateVersionId.HasValue)
        {
            versions = versions.Where(version => version.TemplateVersionId == query.TemplateVersionId.Value).ToList();
        }

        if (query.TemplateId.HasValue)
        {
            var templateVersionIds = await dbContext.TemplateVersions.AsNoTracking()
                .Where(version => version.TemplateId == query.TemplateId.Value)
                .Select(version => version.Id)
                .ToListAsync(ct);
            versions = versions.Where(version => templateVersionIds.Contains(version.TemplateVersionId)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(query.LocaleCode))
        {
            versions = versions.Where(version => version.LocaleCode == query.LocaleCode).ToList();
        }

        if (query.TranslationGroupId.HasValue)
        {
            versions = versions.Where(version => version.TranslationGroupId == query.TranslationGroupId.Value).ToList();
        }

        if (!string.IsNullOrWhiteSpace(query.Slug))
        {
            versions = versions.Where(version => version.Slug == query.Slug).ToList();
        }

        if (!string.IsNullOrWhiteSpace(query.Tags))
        {
            var tags = query.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NormalizeTag).ToArray();
            versions = versions.Where(version => tags.All(tag => version.Tags.Contains(tag))).ToList();
        }

        if (query.PublishedAfter.HasValue)
        {
            versions = versions.Where(version => version.PublishedAt >= query.PublishedAfter.Value).ToList();
        }

        if (query.PublishedBefore.HasValue)
        {
            versions = versions.Where(version => version.PublishedAt <= query.PublishedBefore.Value).ToList();
        }

        var resolved = versions
            .GroupBy(version => version.ContentItemId)
            .Select(group => SelectMostSpecific(group, asOf))
            .ToList();

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            resolved = resolved.Where(version => (version.Slug ?? string.Empty).Contains(query.Q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        resolved = query.SortBy switch
        {
            "publishedAt" => query.SortDesc ? resolved.OrderByDescending(version => version.PublishedAt).ToList() : resolved.OrderBy(version => version.PublishedAt).ToList(),
            "slug" => query.SortDesc ? resolved.OrderByDescending(version => version.Slug).ToList() : resolved.OrderBy(version => version.Slug).ToList(),
            _ => query.SortDesc ? resolved.OrderByDescending(version => version.PublishedAt).ToList() : resolved.OrderBy(version => version.PublishedAt).ToList()
        };

        var total = resolved.Count;
        if (!ControllerHelpers.TryOffset(query.Page, query.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>([], total, query.Page, query.PageSize));
        }

        var pageItems = resolved.Skip(offset).Take(ControllerHelpers.Limit(query.PageSize)).ToList();
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var version in pageItems)
        {
            responses.Add(await ToResolvedSummaryResponseAsync(version, ct));
        }

        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContentItemSummaryResponse>(responses, total, query.Page, query.PageSize));
    }

    private async Task<ContentVersion?> ResolvePublishedVersionAsync(Guid workspaceId, Guid? contentItemId, string? slug, DateTimeOffset asOf, CancellationToken ct)
    {
        var query = dbContext.ContentVersions.AsNoTracking()
            .Include(version => version.FieldValues)
            .Where(version => version.WorkspaceId == workspaceId && version.Status == ContentVersionStatus.Published)
            .Where(version =>
                (version.EffectiveStartAt == null && version.EffectiveEndAt == null)
                || (version.EffectiveStartAt <= asOf && asOf < version.EffectiveEndAt))
            .Where(version => !dbContext.ContentItems.Any(content => content.Id == version.ContentItemId && content.IsDeleted));

        if (contentItemId.HasValue)
        {
            query = query.Where(version => version.ContentItemId == contentItemId.Value);
        }

        if (!string.IsNullOrWhiteSpace(slug))
        {
            query = query.Where(version => version.Slug == slug);
        }

        var candidates = await query.ToListAsync(ct);
        return candidates.Count == 0 ? null : SelectMostSpecific(candidates, asOf);
    }

    private static ContentVersion SelectMostSpecific(IEnumerable<ContentVersion> versions, DateTimeOffset asOf) =>
        versions
            .OrderBy(version => version.EffectiveStartAt.HasValue && version.EffectiveEndAt.HasValue ? 0 : 1)
            .ThenBy(version => version.EffectiveStartAt.HasValue && version.EffectiveEndAt.HasValue ? version.EffectiveEndAt.Value - version.EffectiveStartAt.Value : TimeSpan.MaxValue)
            .ThenByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.VersionNumber)
            .First();

    private async Task<ContentItemSummaryResponse> ToResolvedSummaryResponseAsync(ContentVersion version, CancellationToken ct)
    {
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(templateVersion => templateVersion.Id == version.TemplateVersionId)
            .Select(templateVersion => dbContext.Templates.Where(template => template.Id == templateVersion.TemplateId).Select(template => template.Name).First())
            .FirstAsync(ct);
        return new ContentItemSummaryResponse(version.ContentItemId, version.TemplateVersionId, template, ContentStatus.Published.ToContract(), version.Slug, version.LocaleCode, version.TranslationGroupId, version.Tags.ToList(), version.PublishedAt, version.PublishedAt, version.PublishedAt);
    }

    private async Task<ContentItemDetailResponse> ToResolvedDetailResponseAsync(ContentVersion version, DateTimeOffset asOf, int depth = 0, CancellationToken ct = default)
    {
        var summary = await ToResolvedSummaryResponseAsync(version, ct);
        var templateFields = await dbContext.TemplateFields.AsNoTracking().Where(field => field.TemplateVersionId == version.TemplateVersionId).ToDictionaryAsync(field => field.Id, ct);
        var fields = new List<ContentFieldValueResponse>();
        foreach (var value in version.FieldValues.OrderBy(value => templateFields.GetValueOrDefault(value.FieldId)?.Order ?? 0).ThenBy(value => value.Order))
        {
            templateFields.TryGetValue(value.FieldId, out var field);
            ContentItemDetailResponse? child = null;
            if (depth < 8 && value.ChildContentItemId.HasValue)
            {
                var childVersion = await ResolvePublishedVersionAsync(version.WorkspaceId, value.ChildContentItemId.Value, slug: null, asOf, ct);
                if (childVersion is not null)
                {
                    child = await ToResolvedDetailResponseAsync(childVersion, asOf, depth + 1, ct);
                }
            }

            fields.Add(new ContentFieldValueResponse(value.FieldId, field?.Key, field?.Label, value.Order, value.ValueKind.ToContract(), value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, child, value.JsonValue.Clone(), value.DisplayLabel));
        }

        return new ContentItemDetailResponse(summary.Id, summary.TemplateVersionId, summary.TemplateName, summary.Status, summary.Slug, summary.LocaleCode, summary.TranslationGroupId, summary.Tags, summary.CreatedAt, summary.UpdatedAt, summary.PublishedAt, fields);
    }

    private async Task<ContentItemSummaryResponse> ToSummaryResponseAsync(ContentItem content, CancellationToken ct)
    {
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.Id == content.TemplateVersionId)
            .Select(version => dbContext.Templates.Where(template => template.Id == version.TemplateId).Select(template => template.Name).First())
            .FirstAsync(ct);
        var tags = await GetTagNamesAsync(content.Id, ct);
        return new ContentItemSummaryResponse(content.Id, content.TemplateVersionId, template, content.Status.ToContract(), content.Slug, content.LocaleCode, content.TranslationGroupId, tags, content.CreatedAt, content.UpdatedAt, content.PublishedAt);
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

            fields.Add(new ContentFieldValueResponse(value.FieldId, field?.Key, field?.Label, value.Order, value.ValueKind.ToContract(), value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, child, value.JsonValue.Clone()));
        }

        return new ContentItemDetailResponse(summary.Id, summary.TemplateVersionId, summary.TemplateName, summary.Status, summary.Slug, summary.LocaleCode, summary.TranslationGroupId, summary.Tags, summary.CreatedAt, summary.UpdatedAt, summary.PublishedAt, fields);
    }

    private async Task<IReadOnlyList<string>> GetTagNamesAsync(Guid contentItemId, CancellationToken ct) =>
        await dbContext.ContentItemTags.AsNoTracking()
            .Where(join => join.ContentItemId == contentItemId)
            .Join(dbContext.Tags.AsNoTracking(), join => join.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .OrderBy(tag => tag)
            .ToListAsync(ct);

    private async Task<string?> ValidatePickListValuesAsync(ContentItem content, TemplateVersion version, CancellationToken ct)
    {
        var bindings = new Dictionary<Guid, (string Key, Guid RevisionId, bool Multiple)>();
        foreach (var field in version.Fields.Where(field => field.PrimitiveType == PrimitiveType.PickList))
        {
            if (!TryGetPickListBinding(field.FieldConfig, out var revisionId, out var multiple))
            {
                return $"Field '{field.Key}' must bind a PickList revision.";
            }

            bindings[field.Id] = (field.Key, revisionId, multiple);
        }

        var valuesByRevision = new Dictionary<Guid, HashSet<string>>();
        if (bindings.Count > 0)
        {
            var optionValues = await dbContext.PickListRevisionOptions.AsNoTracking()
                .Where(option => bindings.Values.Select(binding => binding.RevisionId).Contains(option.PickListRevisionId))
                .Select(option => new { option.PickListRevisionId, option.Value })
                .ToListAsync(ct);
            valuesByRevision = optionValues
                .GroupBy(option => option.PickListRevisionId)
                .ToDictionary(group => group.Key, group => group.Select(option => option.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        foreach (var group in content.FieldValues.Where(value => bindings.ContainsKey(value.FieldId)).GroupBy(value => value.FieldId))
        {
            var binding = bindings[group.Key];
            if (!binding.Multiple && group.Count() > 1)
            {
                return $"Field '{binding.Key}' allows only one PickList selection.";
            }

            if (!valuesByRevision.TryGetValue(binding.RevisionId, out var allowedValues))
            {
                return $"Field '{binding.Key}' references an unavailable PickList revision.";
            }

            foreach (var value in group)
            {
                if (string.IsNullOrWhiteSpace(value.TextValue) || !allowedValues.Contains(value.TextValue))
                {
                    return $"Field '{binding.Key}' contains a value that is not in its PickList revision.";
                }
            }
        }

        var revisionValueCache = valuesByRevision;
        foreach (var field in version.Fields.Where(field => field.ComponentId.HasValue))
        {
            foreach (var value in content.FieldValues.Where(value => value.FieldId == field.Id && value.JsonValue is not null))
            {
                if (await ValidateComponentPickListValuesAsync(field.ComponentId!.Value, value.JsonValue!.Value, revisionValueCache, ct) is { } componentError)
                {
                    return $"Field '{field.Key}': {componentError}";
                }
            }
        }

        return null;
    }

    private async Task<string?> ValidateComponentPickListValuesAsync(Guid componentId, JsonElement value, Dictionary<Guid, HashSet<string>> revisionValueCache, CancellationToken ct)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return "component values must be JSON objects.";
        }

        var component = await dbContext.Components.AsNoTracking()
            .Include(candidate => candidate.Versions).ThenInclude(candidate => candidate.Fields)
            .FirstOrDefaultAsync(candidate => candidate.Id == componentId && !candidate.IsDeleted, ct);
        if (component is null)
        {
            return "references an unavailable component schema.";
        }

        var version = component.Versions.FirstOrDefault(candidate => candidate.Id == component.CurrentVersionId && candidate.Status == TemplateVersionStatus.Published && !candidate.IsDeleted);
        if (version is null)
        {
            return "references an unavailable component schema.";
        }

        foreach (var field in version.Fields)
        {
            if (!value.TryGetProperty(field.Key, out var property))
            {
                if (field.IsRequired)
                {
                    return $"component field '{field.Key}' is required.";
                }
                continue;
            }

            if (field.PrimitiveType == PrimitiveType.PickList)
            {
                if (!TryGetPickListBinding(field.FieldConfig, out var revisionId, out var multiple))
                {
                    return $"component field '{field.Key}' must bind a PickList revision.";
                }

                if (!revisionValueCache.TryGetValue(revisionId, out var allowedValues))
                {
                    allowedValues = (await dbContext.PickListRevisionOptions.AsNoTracking()
                        .Where(option => option.PickListRevisionId == revisionId)
                        .Select(option => option.Value)
                        .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    revisionValueCache[revisionId] = allowedValues;
                }

                var submittedValues = property.ValueKind == JsonValueKind.Array
                    ? property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).ToArray()
                    : property.ValueKind == JsonValueKind.String ? [property.GetString()] : [];
                if (submittedValues.Length == 0 || (!multiple && submittedValues.Length != 1) || submittedValues.Any(item => string.IsNullOrWhiteSpace(item) || !allowedValues.Contains(item)))
                {
                    return $"component field '{field.Key}' contains a value that is not in its PickList revision.";
                }
            }

            if (field.NestedComponentId.HasValue)
            {
                var nestedValues = property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().ToArray() : [property];
                foreach (var nestedValue in nestedValues)
                {
                    if (await ValidateComponentPickListValuesAsync(field.NestedComponentId.Value, nestedValue, revisionValueCache, ct) is { } nestedError)
                    {
                        return nestedError;
                    }
                }
            }
        }

        return null;
    }

    private static bool TryGetPickListBinding(JsonElement? fieldConfig, out Guid revisionId, out bool multiple)
    {
        revisionId = Guid.Empty;
        multiple = false;
        if (fieldConfig is not { ValueKind: JsonValueKind.Object } config
            || !config.TryGetProperty("picklistRevisionId", out var revision)
            || revision.ValueKind != JsonValueKind.String
            || !Guid.TryParse(revision.GetString(), out revisionId))
        {
            return false;
        }

        if (config.TryGetProperty("multiple", out var multipleValue) && multipleValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            multiple = multipleValue.GetBoolean();
        }

        return true;
    }

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
