using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Api.Queries;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Domain.ValueObjects;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Cmsify.Contracts;
using CompositionMode = Cmsify.Core.Domain.Enums.CompositionMode;
using ContentStatus = Cmsify.Core.Domain.Enums.ContentStatus;
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
    private readonly IResolvedContentListQuery resolvedContentListQuery;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IWebhookOutbox webhookOutbox;

    public ContentController(CmsifyDbContext dbContext, IContentValidator contentValidator, IContentSearchVectorBuilder searchVectorBuilder, IContentLifecycleService lifecycleService, IContentPublishingService publishingService, ICurrentActor currentActor, IServiceProvider serviceProvider, IWorkspaceAuthorizationService workspaceAuthorization, IWebhookOutbox webhookOutbox)
    {
        this.dbContext = dbContext;
        this.contentValidator = contentValidator;
        this.searchVectorBuilder = searchVectorBuilder;
        this.lifecycleService = lifecycleService;
        this.publishingService = publishingService;
        this.currentActor = currentActor;
        resolvedContentListQuery = serviceProvider.GetRequiredService<IResolvedContentListQuery>();
        this.workspaceAuthorization = workspaceAuthorization;
        this.webhookOutbox = webhookOutbox;
    }

    // ---------- Item-level actions ----------

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ContentItemSummaryResponse>>> List(Guid workspaceId, [FromQuery] ContentListQuery query, CancellationToken ct)
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

        if (!string.IsNullOrWhiteSpace(query.LocaleCode))
        {
            items = items.Where(content => content.LocaleCode == query.LocaleCode);
        }

        if (query.TranslationGroupId.HasValue)
        {
            items = items.Where(content => content.TranslationGroupId == query.TranslationGroupId.Value);
        }

        // Status and publication windows live on versions, not items, so the item-level list
        // interprets these filters as "this item has at least one version matching".
        if (query.Status.HasValue)
        {
            var status = query.Status.Value.ToCore();
            items = items.Where(content => dbContext.ContentVersions.Any(version => version.ContentItemId == content.Id && version.Status == status));
        }

        if (query.PublishedAfter.HasValue)
        {
            items = items.Where(content => dbContext.ContentVersions.Any(version => version.ContentItemId == content.Id && version.PublishedAt >= query.PublishedAfter.Value));
        }

        if (query.PublishedBefore.HasValue)
        {
            items = items.Where(content => dbContext.ContentVersions.Any(version => version.ContentItemId == content.Id && version.PublishedAt <= query.PublishedBefore.Value));
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

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            items = items.Where(content => EF.Functions.ILike(content.Slug ?? string.Empty, $"%{query.Q}%")
                || dbContext.ContentVersions.Any(version => version.ContentItemId == content.Id && version.FieldValues.Any(value => value.TextValue != null && EF.Functions.ILike(value.TextValue, $"%{query.Q}%"))));
        }

        items = query.SortBy switch
        {
            "updatedAt" => query.SortDesc ? items.OrderByDescending(content => content.UpdatedAt) : items.OrderBy(content => content.UpdatedAt),
            "slug" => query.SortDesc ? items.OrderByDescending(content => content.Slug) : items.OrderBy(content => content.Slug),
            _ => query.SortDesc ? items.OrderByDescending(content => content.CreatedAt) : items.OrderBy(content => content.CreatedAt)
        };

        var total = await items.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(query.Page, query.PageSize, out var offset))
        {
            return Ok(new PagedResponse<ContentItemSummaryResponse>([], total, query.Page, query.PageSize));
        }

        var pageItems = await items.Skip(offset).Take(ControllerHelpers.Limit(query.PageSize)).ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var item in pageItems)
        {
            responses.Add(await ToItemSummaryResponseAsync(item, ct));
        }

        return Ok(new PagedResponse<ContentItemSummaryResponse>(responses, total, query.Page, query.PageSize));
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

        var templateVersion = await LoadTemplateVersionAsync(request.TemplateVersionId, ct);
        if (templateVersion is null || !await TemplateVersionBelongsToWorkspaceAsync(templateVersion.Id, workspaceId, ct))
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
        await ApplyTagsAsync(content, workspaceId, request.Tags, ct);

        var version = new ContentVersion
        {
            ContentItemId = content.Id,
            WorkspaceId = workspaceId,
            VersionNumber = 1,
            Status = ContentStatus.Draft,
            TemplateVersionId = request.TemplateVersionId,
            Slug = content.Slug,
            LocaleCode = content.LocaleCode,
            TranslationGroupId = content.TranslationGroupId,
            Tags = request.Tags.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct().ToList(),
            CreatedByUserId = currentActor.UserId,
            UpdatedByUserId = currentActor.UserId
        };
        if (await ApplyVersionFieldValuesAsync(version, templateVersion, request.Fields, ct) is { } fieldError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", fieldError);
        }

        content.SearchVector = searchVectorBuilder.Build(version, templateVersion);
        dbContext.ContentItems.Add(content);
        dbContext.ContentVersions.Add(version);
        EnqueueContentEvent("content.created", content, version);
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return CreatedAtAction(nameof(Get), new { workspaceId, id = content.Id }, await ToItemDetailResponseAsync(content.Id, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContentItemDetailResponse>> Get(Guid workspaceId, Guid id, CancellationToken ct)
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

        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return Ok(await ToItemDetailResponseAsync(id, ct));
    }

    [HttpGet("by-slug/{slug}")]
    public async Task<ActionResult<ContentVersionDetailResponse>> GetBySlug(Guid workspaceId, string slug, [FromQuery] DateTimeOffset? asOf = null, CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var resolvedAsOf = asOf ?? DateTimeOffset.UtcNow;
        var version = await ResolvePublishedVersionAsync(workspaceId, contentItemId: null, slug, resolvedAsOf, ct);
        if (version is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(version.PublishedAt ?? version.UpdatedAt);
        return Ok(await ToVersionDetailResponseAsync(version, resolvedAsOf, ct: ct));
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

        var identityChanged = content.Slug != request.Slug
            || content.LocaleCode != request.LocaleCode
            || content.TranslationGroupId != request.TranslationGroupId;

        content.Slug = request.Slug;
        content.LocaleCode = request.LocaleCode;
        content.TranslationGroupId = request.TranslationGroupId;
        content.UpdatedAt = DateTimeOffset.UtcNow;
        content.UpdatedByUserId = currentActor.UserId;

        var existingTags = await GetTagNamesAsync(content.Id, ct);
        if (!TagsMatch(existingTags, request.Tags))
        {
            dbContext.ContentItemTags.RemoveRange(content.Tags);
            content.Tags.Clear();
            await ApplyTagsAsync(content, workspaceId, request.Tags, ct);
        }

        try
        {
            EnqueueContentEvent("content.updated", content, version: null);
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        if (identityChanged)
        {
            await PropagateItemIdentityToVersionsAsync(content, ct);
        }

        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        return Ok(await ToItemDetailResponseAsync(content.Id, ct));
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

        var referencedBy = await ReferencingContentIdsAsync(id, onlyReferenceFields: true, ct);
        if (referencedBy.Count > 0)
        {
            return this.Error(StatusCodes.Status409Conflict, "referenced-by-other-entity", "Content item is referenced by other content", extensions: new Dictionary<string, object?> { ["referencedBy"] = referencedBy });
        }

        SoftDelete(content);
        EnqueueContentEvent("content.deleted", content, version: null);
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

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
        // Versions carry a denormalized copy of the item's translation group; delivery-side
        // translation filtering reads that copy, so it has to follow the item.
        await dbContext.ContentVersions
            .Where(version => version.ContentItemId == source.Id || version.ContentItemId == target.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(version => version.TranslationGroupId, groupId), ct);
        var translations = await BaseContentQuery(workspaceId).AsNoTracking()
            .Where(content => content.TranslationGroupId == groupId)
            .OrderBy(content => content.LocaleCode)
            .ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var translation in translations)
        {
            responses.Add(await ToItemSummaryResponseAsync(translation, ct));
        }

        return Ok(responses);
    }

    [HttpGet("{id:guid}/translations")]
    public async Task<ActionResult<PagedResponse<ContentItemSummaryResponse>>> GetTranslations(Guid workspaceId, Guid id, [FromQuery] PaginationQuery pagination, CancellationToken ct)
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
            return Ok(new PagedResponse<ContentItemSummaryResponse>([], 0, pagination.Page, pagination.PageSize));
        }

        var query = BaseContentQuery(workspaceId).AsNoTracking().Where(content => content.TranslationGroupId == source.TranslationGroupId).OrderBy(content => content.LocaleCode);
        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new PagedResponse<ContentItemSummaryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var translations = await query.Skip(offset).Take(pagination.PageSize).ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var translation in translations)
        {
            responses.Add(await ToItemSummaryResponseAsync(translation, ct));
        }

        return Ok(new PagedResponse<ContentItemSummaryResponse>(responses, total, pagination.Page, pagination.PageSize));
    }

    private async Task<ActionResult<PagedResponse<ContentItemSummaryResponse>>> ListResolvedAsync(Guid workspaceId, ContentListQuery query, CancellationToken ct)
    {
        var asOf = query.AsOf ?? DateTimeOffset.UtcNow;
        var page = await resolvedContentListQuery.ExecuteAsync(workspaceId, query, asOf, ct);
        var responses = page.Items
            .Select(row => new ContentItemSummaryResponse(
                row.ContentItemId, row.TemplateVersionId, row.TemplateName, row.Slug, row.LocaleCode,
                row.TranslationGroupId, row.Tags, row.PublishedAt, row.PublishedAt, 1, null))
            .ToList();
        return Ok(new PagedResponse<ContentItemSummaryResponse>(responses, page.TotalCount, query.Page, query.PageSize));
    }

    // ---------- Version-level CRUD ----------

    [HttpPost("{id:guid}/versions")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> CreateVersion(Guid workspaceId, Guid id, CreateContentVersionRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var content = await BaseContentQuery(workspaceId).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (content is null)
        {
            return NotFound();
        }

        if (ValidateEffectiveRange(request.EffectiveStartAt, request.EffectiveEndAt) is { } rangeError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid effective range", rangeError);
        }

        var templateVersion = await LoadTemplateVersionAsync(content.TemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
        }

        var nextNumber = 1 + (await dbContext.ContentVersions.Where(v => v.ContentItemId == id).Select(v => (int?)v.VersionNumber).MaxAsync(ct) ?? 0);
        var tagNames = await GetTagNamesAsync(id, ct);

        var version = new ContentVersion
        {
            ContentItemId = id,
            WorkspaceId = workspaceId,
            VersionNumber = nextNumber,
            Status = ContentStatus.Draft,
            TemplateVersionId = content.TemplateVersionId,
            Slug = content.Slug,
            LocaleCode = content.LocaleCode,
            TranslationGroupId = content.TranslationGroupId,
            Tags = tagNames.ToList(),
            EffectiveStartAt = request.EffectiveStartAt,
            EffectiveEndAt = request.EffectiveEndAt,
            CreatedByUserId = currentActor.UserId,
            UpdatedByUserId = currentActor.UserId
        };

        IReadOnlyList<ContentFieldValueRequest> fields;
        if (request.DuplicateFromVersionNumber.HasValue)
        {
            var source = await dbContext.ContentVersions.AsNoTracking()
                .Include(v => v.FieldValues)
                .FirstOrDefaultAsync(v => v.ContentItemId == id && v.VersionNumber == request.DuplicateFromVersionNumber.Value, ct);
            if (source is null)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", $"Version {request.DuplicateFromVersionNumber} does not exist.");
            }

            fields = source.FieldValues
                .Select(value => new ContentFieldValueRequest(value.FieldId, value.Order, value.ValueKind.ToContract(), value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, value.JsonValue))
                .ToList();
            version.RolledBackFromVersionNumber = source.VersionNumber;
        }
        else
        {
            fields = request.Fields ?? [];
        }

        if (await ApplyVersionFieldValuesAsync(version, templateVersion, fields, ct) is { } fieldError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", fieldError);
        }

        content.SearchVector = searchVectorBuilder.Build(version, templateVersion);
        content.UpdatedAt = DateTimeOffset.UtcNow;
        dbContext.ContentVersions.Add(version);
        EnqueueContentEvent("content.version_created", content, version);
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(version.UpdatedAt);
        return CreatedAtAction(nameof(GetVersion), new { workspaceId, id, versionNumber = version.VersionNumber }, await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<PagedResponse<ContentVersionSummaryResponse>>> ListVersions(Guid workspaceId, Guid id, [FromQuery] PaginationQuery pagination, CancellationToken ct)
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
            return Ok(new PagedResponse<ContentVersionSummaryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var versions = await query.Skip(offset).Take(pagination.PageSize).ToListAsync(ct);
        return Ok(new PagedResponse<ContentVersionSummaryResponse>(versions.Select(ToVersionSummaryResponse).ToList(), total, pagination.Page, pagination.PageSize));
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
            .Include(v => v.FieldValues)
            .FirstOrDefaultAsync(v => v.ContentItemId == id && v.VersionNumber == versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(version.UpdatedAt);
        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    [HttpPut("{id:guid}/versions/{versionNumber:int}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> UpdateVersion(Guid workspaceId, Guid id, int versionNumber, UpdateContentVersionRequest request, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (version.Status is not (ContentStatus.Draft or ContentStatus.Review or ContentStatus.Approved))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only Draft, Review, or Approved versions can be edited");
        }

        if (!this.IfMatchMatches(version.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        if (ValidateEffectiveRange(request.EffectiveStartAt, request.EffectiveEndAt) is { } rangeError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid effective range", rangeError);
        }

        var templateVersion = await LoadTemplateVersionAsync(version.TemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
        }

        version.EffectiveStartAt = request.EffectiveStartAt;
        version.EffectiveEndAt = request.EffectiveEndAt;
        if (await ApplyVersionFieldValuesAsync(version, templateVersion, request.Fields, ct) is { } fieldError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", fieldError);
        }

        // Editing the version invalidates any scheduled publish: clear it so the pre-edit content
        // can't auto-publish later without going back through review.
        version.PublishAt = null;
        version.PublishLeaseOwner = null;
        version.PublishLeaseToken = null;
        version.PublishLeaseExpiresAt = null;
        version.UpdatedAt = DateTimeOffset.UtcNow;
        version.UpdatedByUserId = currentActor.UserId;
        var item = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        item.SearchVector = searchVectorBuilder.Build(version, templateVersion);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        EnqueueContentEvent("content.version_updated", item, version);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        Response.Headers.ETag = ControllerHelpers.ETag(version.UpdatedAt);
        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    [HttpDelete("{id:guid}/versions/{versionNumber:int}")]
    [RequireRole(UserRole.Editor)]
    public async Task<IActionResult> DeleteVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(version.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        if (version.Status != ContentStatus.Draft)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only Draft versions can be deleted");
        }

        // An item must never be left with zero versions: every read path (Get, by-slug, the admin
        // version list) assumes at least one exists, and a versionless item is unrecoverable through
        // the API. Deleting the item itself is the supported way to remove the last version.
        var otherVersions = await dbContext.ContentVersions.CountAsync(candidate => candidate.ContentItemId == id && candidate.Id != version.Id, ct);
        if (otherVersions == 0)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Cannot delete the content item's only version", "Delete the content item instead.");
        }

        var item = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        dbContext.ContentVersions.Remove(version);
        EnqueueContentEvent("content.version_deleted", item, version);
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Version-level workflow ----------

    [HttpPost("{id:guid}/versions/{versionNumber:int}/submit")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentVersionDetailResponse>> Submit(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Review, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/approve")]
    [RequireRole(UserRole.TemplateAdmin)]
    public Task<ActionResult<ContentVersionDetailResponse>> Approve(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Approved, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/reject")]
    [RequireRole(UserRole.TemplateAdmin)]
    public Task<ActionResult<ContentVersionDetailResponse>> Reject(Guid workspaceId, Guid id, int versionNumber, RejectContentRequest request, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Draft, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/archive")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentVersionDetailResponse>> Archive(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Archived, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/restore")]
    [RequireRole(UserRole.Editor)]
    public Task<ActionResult<ContentVersionDetailResponse>> Restore(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct) => Transition(workspaceId, id, versionNumber, ContentStatus.Draft, ct);

    [HttpPost("{id:guid}/versions/{versionNumber:int}/publish")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<PublishContentVersionResponse>> Publish(Guid workspaceId, Guid id, int versionNumber, PublishContentVersionRequest? request, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        var templateVersion = await LoadTemplateVersionAsync(version.TemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
        }
        if (await ValidatePickListValuesAsync(version, templateVersion, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", pickListError);
        }

        var allowOverride = (request?.OverrideWorkflow ?? false) && currentActor.Role >= UserRole.Admin;

        if (request?.PublishAt is not null)
        {
            if (request.OverrideWorkflow == true)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Invalid content state transition", "Workflow override is not supported for scheduled publication.");
            }

            if (version.Status != ContentStatus.Approved)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Content must be approved before scheduling publication");
            }

            version.PublishAt = request.PublishAt;
            version.UpdatedAt = DateTimeOffset.UtcNow;
            version.UpdatedByUserId = currentActor.UserId;
            await dbContext.SaveChangesAsync(ct);
            return Ok(new PublishContentVersionResponse(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct), []));
        }

        if (!lifecycleService.CanTransition(version.Status, ContentStatus.Published, allowOverride))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Invalid content state transition", $"Content version cannot transition from {version.Status} to {ContentStatus.Published}.");
        }

        await lifecycleService.TransitionAsync(version, ContentStatus.Published, currentActor.UserId ?? Guid.Empty, allowOverride);
        version.PublishAt = null;
        version.PublishLeaseOwner = null;
        version.PublishLeaseToken = null;
        version.PublishLeaseExpiresAt = null;
        var publishResult = await publishingService.PublishAsync(version, currentActor.UserId, ct);
        var publishedItem = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        EnqueueContentEvent("content.version_published", publishedItem, publishResult.Version);
        await dbContext.SaveChangesAsync(ct);

        return Ok(new PublishContentVersionResponse(await ToVersionDetailResponseAsync(publishResult.Version, DateTimeOffset.UtcNow, ct: ct), publishResult.Warnings));
    }

    [HttpPost("{id:guid}/versions/{versionNumber:int}/upgrade-template-version")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> UpgradeTemplateVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (version.Status is not (ContentStatus.Draft or ContentStatus.Review or ContentStatus.Approved))
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Only Draft, Review, or Approved versions can be upgraded");
        }

        var currentTemplateVersion = await dbContext.TemplateVersions.AsNoTracking().FirstAsync(tv => tv.Id == version.TemplateVersionId, ct);
        var target = await dbContext.TemplateVersions
            .Include(tv => tv.Fields).ThenInclude(field => field.AllowedTypes)
            .Where(tv => tv.TemplateId == currentTemplateVersion.TemplateId && tv.Status == TemplateVersionStatus.Published && !tv.IsDeleted)
            .OrderByDescending(tv => tv.VersionNumber)
            .FirstOrDefaultAsync(ct);
        if (target is null)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "No published template version is available");
        }

        version.TemplateVersionId = target.Id;
        var targetFieldIds = target.Fields.Select(field => field.Id).ToHashSet();
        var stale = version.FieldValues.Where(value => !targetFieldIds.Contains(value.FieldId)).ToList();
        dbContext.ContentVersionFieldValues.RemoveRange(stale);
        foreach (var value in stale)
        {
            version.FieldValues.Remove(value);
        }

        var validation = contentValidator.Validate(version, target);
        if (!validation.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content does not satisfy the target template version", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }
        if (await ValidatePickListValuesAsync(version, target, ct) is { } pickListError)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content does not satisfy the target template version", pickListError);
        }

        var item = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        item.SearchVector = searchVectorBuilder.Build(version, target);
        // Bumping the template version invalidates any scheduled publish, same reasoning as UpdateVersion.
        version.PublishAt = null;
        version.PublishLeaseOwner = null;
        version.PublishLeaseToken = null;
        version.PublishLeaseExpiresAt = null;
        version.UpdatedAt = DateTimeOffset.UtcNow;
        version.UpdatedByUserId = currentActor.UserId;
        EnqueueContentEvent("content.version_template_upgraded", item, version);
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(version.UpdatedAt);
        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    private async Task<ActionResult<ContentVersionDetailResponse>> Transition(Guid workspaceId, Guid id, int versionNumber, ContentStatus targetStatus, CancellationToken ct)
    {
        var version = await LoadVersionForEditAsync(workspaceId, id, versionNumber, ct);
        if (version is null)
        {
            return NotFound();
        }

        if (!lifecycleService.CanTransition(version.Status, targetStatus))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "invalid-state-transition", "Invalid content state transition", $"Content version cannot transition from {version.Status} to {targetStatus}.");
        }

        await lifecycleService.TransitionAsync(version, targetStatus, currentActor.UserId ?? Guid.Empty);
        var item = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        EnqueueContentEvent("content.version_status_changed", item, version);
        await dbContext.SaveChangesAsync(ct);

        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, ct: ct));
    }

    private IQueryable<ContentItem> BaseContentQuery(Guid workspaceId) =>
        dbContext.ContentItems.Where(content => content.WorkspaceId == workspaceId && !content.IsDeleted);

    private async Task<TemplateVersion?> LoadTemplateVersionAsync(Guid id, CancellationToken ct) =>
        await dbContext.TemplateVersions
            .Include(version => version.Fields).ThenInclude(field => field.AllowedTypes)
            .FirstOrDefaultAsync(version => version.Id == id && !version.IsDeleted, ct);

    private async Task<bool> TemplateVersionBelongsToWorkspaceAsync(Guid versionId, Guid workspaceId, CancellationToken ct) =>
        await dbContext.TemplateVersions.AnyAsync(version => version.Id == versionId && dbContext.Templates.Any(template => template.Id == version.TemplateId && template.WorkspaceId == workspaceId && !template.IsDeleted), ct);

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

    private async Task<IReadOnlyList<string>> GetTagNamesAsync(Guid contentItemId, CancellationToken ct) =>
        await dbContext.ContentItemTags.AsNoTracking()
            .Where(join => join.ContentItemId == contentItemId)
            .Join(dbContext.Tags.AsNoTracking(), join => join.TagId, tag => tag.Id, (_, tag) => tag.Name)
            .OrderBy(tag => tag)
            .ToListAsync(ct);

    private static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();

    private static bool TagsMatch(IEnumerable<string> existing, IEnumerable<string> requested) =>
        existing.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct().Order()
            .SequenceEqual(requested.Select(NormalizeTag).Where(tag => tag.Length > 0).Distinct().Order());

    private async Task<ContentItem?> LoadContentForEditAsync(Guid workspaceId, Guid id, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return null;
        }

        return await BaseContentQuery(workspaceId)
            .Include(content => content.Tags)
            .FirstOrDefaultAsync(content => content.Id == id, ct);
    }

    /// <summary>
    /// Pushes the item's identity fields down onto every one of its versions.
    /// <see cref="ContentVersion"/> keeps denormalized copies of <c>Slug</c>, <c>LocaleCode</c> and
    /// <c>TranslationGroupId</c>, and the delivery/query paths (by-slug resolution, translation-group
    /// filtering) read the version's copy rather than the item's, so a rename that is not propagated
    /// leaves the item resolvable only at its old slug. <c>Tags</c> is deliberately excluded: it is a
    /// per-version snapshot of the tags at the time that version was created, not a live mirror.
    /// </summary>
    private Task<int> PropagateItemIdentityToVersionsAsync(ContentItem content, CancellationToken ct)
    {
        var slug = content.Slug;
        var localeCode = content.LocaleCode;
        var translationGroupId = content.TranslationGroupId;
        return dbContext.ContentVersions
            .Where(version => version.ContentItemId == content.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(version => version.Slug, slug)
                .SetProperty(version => version.LocaleCode, localeCode)
                .SetProperty(version => version.TranslationGroupId, translationGroupId), ct);
    }

    private async Task<ContentVersion?> LoadVersionForEditAsync(Guid workspaceId, Guid contentItemId, int versionNumber, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return null;
        }

        var itemExists = await BaseContentQuery(workspaceId).AnyAsync(item => item.Id == contentItemId, ct);
        if (!itemExists)
        {
            return null;
        }

        return await dbContext.ContentVersions
            .Include(version => version.FieldValues)
            .FirstOrDefaultAsync(version => version.ContentItemId == contentItemId && version.VersionNumber == versionNumber && version.WorkspaceId == workspaceId, ct);
    }

    private static string? ValidateEffectiveRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start.HasValue != end.HasValue)
        {
            return "Provide both effectiveStartAt and effectiveEndAt, or neither.";
        }

        if (start.HasValue && start.Value >= end!.Value)
        {
            return "effectiveStartAt must be before effectiveEndAt.";
        }

        return null;
    }

    private async Task<string?> ApplyVersionFieldValuesAsync(ContentVersion version, TemplateVersion templateVersion, IReadOnlyList<ContentFieldValueRequest> fields, CancellationToken ct)
    {
        // Explicit removal (rather than relying on cascade-delete orphan detection from .Clear()
        // alone) mirrors UpgradeTemplateVersion's pattern below and is required for correctness once
        // this method is called against an already-tracked version (UpdateVersion): without it, the
        // old rows are left behind as stale duplicates alongside the newly-added ones.
        if (version.FieldValues.Count > 0)
        {
            dbContext.ContentVersionFieldValues.RemoveRange(version.FieldValues);
            version.FieldValues.Clear();
        }

        var fieldPickLists = templateVersion.Fields
            .Where(field => field.PrimitiveType == PrimitiveType.PickList)
            .Select(field => (field.Id, PickListId: GetPickListId(field), RevisionId: GetPickListRevisionId(field)))
            .Where(x => x.PickListId.HasValue)
            .ToDictionary(x => x.Id);
        var pickListIds = fieldPickLists.Values.Select(x => x.PickListId!.Value).Distinct().ToArray();
        var currentLabels = await dbContext.PickLists.AsNoTracking().Include(list => list.Options)
            .Where(list => pickListIds.Contains(list.Id))
            .ToDictionaryAsync(list => list.Id, list => list.Options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase), ct);
        var revisionIds = fieldPickLists.Values.Select(x => x.RevisionId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        var revisionLabels = await dbContext.PickListRevisions.AsNoTracking().Include(revision => revision.Options)
            .Where(revision => revisionIds.Contains(revision.Id))
            .ToDictionaryAsync(revision => revision.Id, revision => revision.Options.ToDictionary(option => option.Value, option => option.Label, StringComparer.OrdinalIgnoreCase), ct);

        foreach (var input in fields)
        {
            var displayLabel = input.ValueKind == SyntaxCircus.Cmsify.Contracts.ValueKind.PickList && input.TextValue is not null && fieldPickLists.TryGetValue(input.FieldId, out var binding)
                ? (binding.RevisionId.HasValue && revisionLabels.TryGetValue(binding.RevisionId.Value, out var versionedOptions) ? versionedOptions : currentLabels.GetValueOrDefault(binding.PickListId!.Value))?.GetValueOrDefault(input.TextValue)
                : null;

            var fieldValue = new ContentVersionFieldValue
            {
                ContentVersionId = version.Id,
                FieldId = input.FieldId,
                Order = input.Order,
                ValueKind = input.ValueKind.ToCore(),
                TextValue = input.TextValue,
                DisplayLabel = displayLabel,
                BoolValue = input.BoolValue,
                MediaAssetId = input.MediaAssetId,
                FileAssetId = input.FileAssetId,
                ChildContentItemId = input.ChildContentItemId,
                JsonValue = input.JsonValue?.Clone()
            };
            // Explicitly track as Added: version may already be tracked (e.g. on UpdateVersion, where
            // the parent ContentVersion was loaded, not newly constructed), and both this entity's PK
            // and the store default are set client-side (EntityBase.Id), so EF's graph painter cannot
            // infer "Added" from navigation-collection membership alone - without this, it treats the
            // child as "Modified" and issues an UPDATE against a row that doesn't exist yet, which
            // fails with 0 rows affected (DbUpdateConcurrencyException).
            //
            // When version is already tracked, DbSet.Add below performs automatic relationship fixup
            // and appends fieldValue to version.FieldValues itself (matching ContentVersionId to the
            // tracked parent); when version is not yet tracked (Create/CreateVersion, where it's added
            // to the context later), no such fixup happens and the explicit Add is required to build
            // the graph. Guard against double-adding into the navigation collection in the former case.
            dbContext.ContentVersionFieldValues.Add(fieldValue);
            if (!version.FieldValues.Contains(fieldValue))
            {
                version.FieldValues.Add(fieldValue);
            }
        }

        var validation = contentValidator.Validate(version, templateVersion);
        if (!validation.IsValid)
        {
            return string.Join(" ", validation.Errors.Select(error => error.ErrorMessage));
        }

        return await ValidatePickListValuesAsync(version, templateVersion, ct);
    }

    private static Guid? GetPickListId(TemplateField field)
    {
        if (field.FieldConfig is not { ValueKind: JsonValueKind.Object } config || !config.TryGetProperty("picklistId", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
    }

    private static Guid? GetPickListRevisionId(TemplateField field)
    {
        if (field.FieldConfig is not { ValueKind: JsonValueKind.Object } config || !config.TryGetProperty("picklistRevisionId", out var id) || id.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
    }

    private async Task<string?> ValidatePickListValuesAsync(ContentVersion version, TemplateVersion templateVersion, CancellationToken ct)
    {
        var bindings = new Dictionary<Guid, (string Key, Guid RevisionId, bool Multiple)>();
        foreach (var field in templateVersion.Fields.Where(field => field.PrimitiveType == PrimitiveType.PickList))
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

        foreach (var group in version.FieldValues.Where(value => bindings.ContainsKey(value.FieldId)).GroupBy(value => value.FieldId))
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
        foreach (var field in templateVersion.Fields.Where(field => field.ComponentId.HasValue))
        {
            foreach (var value in version.FieldValues.Where(value => value.FieldId == field.Id && value.JsonValue is not null))
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

    private async Task<ContentItemSummaryResponse> ToItemSummaryResponseAsync(ContentItem content, CancellationToken ct)
    {
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.Id == content.TemplateVersionId)
            .Select(version => dbContext.Templates.Where(t => t.Id == version.TemplateId).Select(t => t.Name).First())
            .FirstAsync(ct);
        var tags = await GetTagNamesAsync(content.Id, ct);
        var versions = await dbContext.ContentVersions.AsNoTracking().Where(v => v.ContentItemId == content.Id).ToListAsync(ct);
        var currentlyServing = ComputeCurrentlyServing(versions, DateTimeOffset.UtcNow);
        return new ContentItemSummaryResponse(
            content.Id, content.TemplateVersionId, template, content.Slug, content.LocaleCode, content.TranslationGroupId,
            tags, content.CreatedAt, content.UpdatedAt, versions.Count,
            currentlyServing is null ? null : ToVersionSummaryResponse(currentlyServing));
    }

    private async Task<ContentItemDetailResponse> ToItemDetailResponseAsync(Guid id, CancellationToken ct)
    {
        var content = await dbContext.ContentItems.AsNoTracking().FirstAsync(item => item.Id == id, ct);
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.Id == content.TemplateVersionId)
            .Select(version => dbContext.Templates.Where(t => t.Id == version.TemplateId).Select(t => t.Name).First())
            .FirstAsync(ct);
        var tags = await GetTagNamesAsync(content.Id, ct);
        var versions = await dbContext.ContentVersions.AsNoTracking()
            .Where(v => v.ContentItemId == id)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
        var currentlyServing = ComputeCurrentlyServing(versions, DateTimeOffset.UtcNow);
        return new ContentItemDetailResponse(
            content.Id, content.TemplateVersionId, template, content.Slug, content.LocaleCode, content.TranslationGroupId,
            tags, content.CreatedAt, content.UpdatedAt,
            currentlyServing is null ? null : ToVersionSummaryResponse(currentlyServing),
            versions.Select(ToVersionSummaryResponse).ToList());
    }

    private static ContentVersion? ComputeCurrentlyServing(IReadOnlyList<ContentVersion> versions, DateTimeOffset asOf)
    {
        var candidates = versions
            .Where(version => version.Status == ContentStatus.Published)
            .Where(version => (version.EffectiveStartAt is null && version.EffectiveEndAt is null)
                || (version.EffectiveStartAt <= asOf && asOf < version.EffectiveEndAt))
            .ToList();
        return candidates.Count == 0 ? null : SelectMostSpecific(candidates, asOf);
    }

    private static ContentVersion SelectMostSpecific(IEnumerable<ContentVersion> versions, DateTimeOffset asOf) =>
        versions
            .OrderBy(version => version.EffectiveStartAt.HasValue && version.EffectiveEndAt.HasValue ? 0 : 1)
            .ThenBy(version => version.EffectiveStartAt.HasValue && version.EffectiveEndAt.HasValue ? version.EffectiveEndAt!.Value - version.EffectiveStartAt!.Value : TimeSpan.MaxValue)
            .ThenByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.VersionNumber)
            .First();

    private async Task<ContentVersion?> ResolvePublishedVersionAsync(Guid workspaceId, Guid? contentItemId, string? slug, DateTimeOffset asOf, CancellationToken ct)
    {
        var query = dbContext.ContentVersions.AsNoTracking()
            .Include(version => version.FieldValues)
            .Where(version => version.WorkspaceId == workspaceId && version.Status == ContentStatus.Published)
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

    private static ContentVersionSummaryResponse ToVersionSummaryResponse(ContentVersion version) =>
        new(version.Id, version.ContentItemId, version.VersionNumber, version.Status.ToContract(), version.TemplateVersionId,
            version.Slug, version.LocaleCode, version.EffectiveStartAt, version.EffectiveEndAt, version.PublishAt, version.PublishedAt,
            version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber, version.Tags.ToList(),
            version.CreatedAt, version.UpdatedAt);

    private async Task<ContentVersionDetailResponse> ToVersionDetailResponseAsync(ContentVersion version, DateTimeOffset asOf, int depth = 0, CancellationToken ct = default)
    {
        var templateName = await dbContext.TemplateVersions.AsNoTracking()
            .Where(tv => tv.Id == version.TemplateVersionId)
            .Select(tv => dbContext.Templates.Where(t => t.Id == tv.TemplateId).Select(t => t.Name).First())
            .FirstOrDefaultAsync(ct) ?? string.Empty;
        var templateFields = await dbContext.TemplateFields.AsNoTracking()
            .Where(field => field.TemplateVersionId == version.TemplateVersionId)
            .ToDictionaryAsync(field => field.Id, ct);
        var fieldValues = version.FieldValues.Count > 0
            ? version.FieldValues
            : await dbContext.ContentVersionFieldValues.AsNoTracking().Where(value => value.ContentVersionId == version.Id).ToListAsync(ct);

        var fields = new List<ContentVersionFieldValueResponse>();
        foreach (var value in fieldValues.OrderBy(value => templateFields.GetValueOrDefault(value.FieldId)?.Order ?? 0).ThenBy(value => value.Order))
        {
            templateFields.TryGetValue(value.FieldId, out var field);
            ContentVersionDetailResponse? child = null;
            if (depth < 8 && value.ChildContentItemId.HasValue)
            {
                var childVersion = await ResolvePublishedVersionAsync(version.WorkspaceId, value.ChildContentItemId.Value, slug: null, asOf, ct);
                if (childVersion is not null)
                {
                    child = await ToVersionDetailResponseAsync(childVersion, asOf, depth + 1, ct);
                }
            }

            fields.Add(new ContentVersionFieldValueResponse(value.FieldId, field?.Key, field?.Label, value.Order, value.ValueKind.ToContract(), value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, child, value.JsonValue?.Clone(), value.DisplayLabel));
        }

        return new ContentVersionDetailResponse(
            version.Id, version.ContentItemId, version.VersionNumber, version.Status.ToContract(), version.TemplateVersionId, templateName,
            version.Slug, version.LocaleCode, version.TranslationGroupId, version.EffectiveStartAt, version.EffectiveEndAt,
            version.PublishAt, version.PublishedAt, version.ArchivedAt, version.PublishedByUserId, version.RolledBackFromVersionNumber,
            version.Tags.ToList(), version.CreatedAt, version.UpdatedAt, fields);
    }

    private void EnqueueContentEvent(string eventType, ContentItem content, ContentVersion? version)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            contentItemId = content.Id,
            workspaceId = content.WorkspaceId,
            templateVersionId = content.TemplateVersionId,
            contentVersionId = version?.Id,
            versionNumber = version?.VersionNumber,
            status = version?.Status.ToString()
        });
        webhookOutbox.Enqueue(eventType, content.WorkspaceId, content.Id, payload, DateTimeOffset.UtcNow);
    }

    private void SoftDelete(ContentItem content)
    {
        content.IsDeleted = true;
        content.DeletedAt = DateTimeOffset.UtcNow;
        content.DeletedByUserId = currentActor.UserId;
        content.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<IReadOnlyList<Guid>> ReferencingContentIdsAsync(Guid id, bool onlyReferenceFields, CancellationToken ct)
    {
        var query = dbContext.ContentVersionFieldValues.AsNoTracking().Where(value => value.ChildContentItemId == id);
        if (onlyReferenceFields)
        {
            query = query.Where(value => dbContext.TemplateFields.Any(field => field.Id == value.FieldId && field.CompositionMode == CompositionMode.Reference));
        }

        return await query
            .Join(dbContext.ContentVersions.AsNoTracking(), value => value.ContentVersionId, version => version.Id, (value, version) => version.ContentItemId)
            .Distinct()
            .ToListAsync(ct);
    }
}
