using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Core.ContentQueries;
using Cmsify.Core.ContentWrites;
using SyntaxCircus.Common;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Domain.ValueObjects;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.ContentWrites;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
using UpdateContentVersionRequest = SyntaxCircus.Cmsify.Contracts.UpdateContentVersionRequest;

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
    private readonly IWebhookOutbox webhookOutbox;
    private readonly ContentVersionFieldWriter fieldWriter;
    private readonly ContentVersionDetailProjector detailProjector;

    public ContentController(CmsifyDbContext dbContext, IContentValidator contentValidator, IContentSearchVectorBuilder searchVectorBuilder, IContentLifecycleService lifecycleService, IContentPublishingService publishingService, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, IWebhookOutbox webhookOutbox, ContentVersionFieldWriter? fieldWriter = null, ContentVersionDetailProjector? detailProjector = null)
    {
        this.dbContext = dbContext;
        this.contentValidator = contentValidator;
        this.searchVectorBuilder = searchVectorBuilder;
        this.lifecycleService = lifecycleService;
        this.publishingService = publishingService;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.webhookOutbox = webhookOutbox;
        this.fieldWriter = fieldWriter ?? new ContentVersionFieldWriter(dbContext, contentValidator);
        this.detailProjector = detailProjector ?? new ContentVersionDetailProjector(dbContext);
    }

    // ---------- Item-level actions ----------

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ContentItemSummaryResponse>>> List(Guid workspaceId, [FromQuery] ContentListQuery query,
        [FromServices] IListContentRequestHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(query.ToRequest(workspaceId), ct);
        if (result.IsFailure)
        {
            return result.Errors[0].Kind switch
            {
                ResultErrorKind.Unauthenticated => StatusCode(StatusCodes.Status401Unauthorized),
                ResultErrorKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden),
                ResultErrorKind.NotFound => NotFound(),
                ResultErrorKind.Validation => this.Error(StatusCodes.Status400BadRequest, CmsifyError.ValidationFailed, "Invalid pagination", result.Errors[0].Message),
                ResultErrorKind.Conflict => this.Error(StatusCodes.Status409Conflict, CmsifyError.Conflict, "Conflict", result.Errors[0].Message),
                _ => this.Error(StatusCodes.Status500InternalServerError, CmsifyError.InternalServerError, "Internal Server Error")
            };
        }
        return Ok(result.Value.ToResponse());
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
    public async Task<ActionResult<ContentVersionDetailResponse>> GetBySlug(Guid workspaceId, string slug, [FromQuery] DateTimeOffset? asOf = null, [FromQuery] bool expandChildren = true, CancellationToken ct = default)
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
        return Ok(await ToVersionDetailResponseAsync(version, resolvedAsOf, expandChildren, ct));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentItemDetailResponse>> Update(Guid workspaceId, Guid id, UpdateContentItemRequest request, CancellationToken ct)
    {
        if (request.Slug is not null && !SlugRules.IsValid(request.Slug))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", SlugRules.ValidationMessage);
        }

        await using var writeScope = await ContentIdentityWriteScope.BeginAsync(dbContext, ct);
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
            await dbContext.PropagateContentIdentityAsync(content, ct);
        }

        Response.Headers.ETag = ControllerHelpers.ETag(content.UpdatedAt);
        var response = await ToItemDetailResponseAsync(content.Id, ct);
        await writeScope.CompleteAsync(ct);
        return Ok(response);
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

        await using var writeScope = await ContentIdentityWriteScope.BeginAsync(dbContext, ct);
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
        await dbContext.LinkContentVersionTranslationsAsync(source.Id, target.Id, groupId, ct);
        var translations = await BaseContentQuery(workspaceId).AsNoTracking()
            .Where(content => content.TranslationGroupId == groupId)
            .OrderBy(content => content.LocaleCode)
            .ToListAsync(ct);
        var responses = new List<ContentItemSummaryResponse>();
        foreach (var translation in translations)
        {
            responses.Add(await ToItemSummaryResponseAsync(translation, ct));
        }

        await writeScope.CompleteAsync(ct);
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

    // ---------- Version-level CRUD ----------

    [HttpPost("{id:guid}/versions")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> CreateVersion(Guid workspaceId, Guid id, CreateContentVersionRequest request, CancellationToken ct, [FromQuery] bool expandChildren = true)
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

        // The item-level ContentItem.TemplateVersionId is set once at item creation and is never
        // updated by UpgradeTemplateVersion (which only ever mutates a single version's own
        // TemplateVersionId), so it goes stale the moment any version is upgraded to a newer
        // template version. Validating a new version against it - rather than against whichever
        // template version the new version is actually built from - rejects perfectly valid field
        // values with "targets a field not present on the template version" (see the
        // CreateVersion_* tests below). Instead:
        //   - when duplicating, the new version must be validated against the SOURCE version's own
        //     TemplateVersionId, since that's the template version its copied field values actually
        //     belong to;
        //   - otherwise, it follows the item's latest existing version's TemplateVersionId, i.e.
        //     whatever template version the content is already sitting on - matching what
        //     duplicating the latest version would have done, and never silently jumping content to
        //     an unrelated newer template version the caller never asked to upgrade to.
        var latestVersion = await dbContext.ContentVersions.AsNoTracking()
            .Where(v => v.ContentItemId == id)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new { v.VersionNumber, v.TemplateVersionId })
            .FirstAsync(ct);
        var nextNumber = latestVersion.VersionNumber + 1;
        var tagNames = await GetTagNamesAsync(id, ct);

        ContentVersion? duplicateSource = null;
        var baseTemplateVersionId = latestVersion.TemplateVersionId;
        if (request.DuplicateFromVersionNumber.HasValue)
        {
            duplicateSource = await dbContext.ContentVersions.AsNoTracking()
                .Include(v => v.FieldValues)
                .FirstOrDefaultAsync(v => v.ContentItemId == id && v.VersionNumber == request.DuplicateFromVersionNumber.Value, ct);
            if (duplicateSource is null)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", $"Version {request.DuplicateFromVersionNumber} does not exist.");
            }

            baseTemplateVersionId = duplicateSource.TemplateVersionId;
        }

        var templateVersion = await LoadTemplateVersionAsync(baseTemplateVersionId, ct);
        if (templateVersion is null)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content validation failed", "Template version is unavailable.");
        }

        var version = new ContentVersion
        {
            ContentItemId = id,
            WorkspaceId = workspaceId,
            VersionNumber = nextNumber,
            Status = ContentStatus.Draft,
            TemplateVersionId = baseTemplateVersionId,
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
        if (duplicateSource is not null)
        {
            fields = duplicateSource.FieldValues
                .OrderBy(value => value.Order).ThenBy(value => value.Id)
                .Select(value => new ContentFieldValueRequest(value.FieldId, value.Order, value.ValueKind.ToContract(), value.TextValue, value.BoolValue, value.MediaAssetId, value.FileAssetId, value.ChildContentItemId, value.JsonValue))
                .ToList();
            version.RolledBackFromVersionNumber = duplicateSource.VersionNumber;
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
        return CreatedAtAction(nameof(GetVersion), new { workspaceId, id, versionNumber = version.VersionNumber }, await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, expandChildren, ct));
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
    public async Task<ActionResult<ContentVersionDetailResponse>> GetVersion(Guid workspaceId, Guid id, int versionNumber, CancellationToken ct, [FromQuery] bool expandChildren = true)
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
        return Ok(await ToVersionDetailResponseAsync(version, DateTimeOffset.UtcNow, expandChildren, ct));
    }

    [HttpPut("{id:guid}/versions/{versionNumber:int}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> UpdateVersion(Guid workspaceId, Guid id, int versionNumber, UpdateContentVersionRequest request,
        [FromServices] IUpdateContentVersionRequestHandler handler, CancellationToken ct, [FromQuery] bool expandChildren = true)
    {
        var result = await handler.HandleAsync(request.ToWriteRequest(workspaceId, id, versionNumber,
            Request.Headers.IfMatch.ToString(), expandChildren), ct);
        if (result.IsFailure)
        {
            var error = result.Errors[0];
            return error.Code switch
            {
                ContentVersionWriteErrors.NotEditable => this.Error(StatusCodes.Status409Conflict, CmsifyError.Conflict, "Only Draft, Review, or Approved versions can be edited"),
                ContentVersionWriteErrors.ConcurrencyMismatch => this.Error(StatusCodes.Status412PreconditionFailed, CmsifyError.ConcurrencyMismatch, "Concurrency mismatch"),
                ContentVersionWriteErrors.InvalidEffectiveRange => this.Error(StatusCodes.Status422UnprocessableEntity, CmsifyError.ValidationFailed, "Invalid effective range", error.Message),
                ContentVersionWriteErrors.ContentValidationFailed => this.Error(StatusCodes.Status422UnprocessableEntity, CmsifyError.ValidationFailed, "Content validation failed", error.Message),
                _ => error.Kind switch
                {
                    ResultErrorKind.Unauthenticated => StatusCode(StatusCodes.Status401Unauthorized),
                    ResultErrorKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden),
                    ResultErrorKind.NotFound => NotFound(),
                    _ => this.Error(StatusCodes.Status500InternalServerError, CmsifyError.InternalServerError, "Internal Server Error")
                }
            };
        }
        Response.Headers.ETag = ControllerHelpers.ETag(result.Value.Version.UpdatedAt);
        return Ok(ContentVersionWriteMappings.ToResponse(result.Value.Version));
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

        // Note: the actual Status/PublishedAt/PublishedByUserId/UpdatedAt/UpdatedByUserId mutation for the
        // transition to Published is applied by publishingService.PublishAsync below, not lifecycleService
        // here - CanTransition above already validated the transition is allowed. Setting version.Status
        // ahead of PublishAsync (as this used to do via lifecycleService.TransitionAsync) would let it flush
        // as Published together with the prior-version archival in whichever order EF happens to batch them,
        // reopening the ix_content_versions_content_item_id race PublishAsync's internal ordering fix exists
        // to prevent.
        version.PublishAt = null;
        version.PublishLeaseOwner = null;
        version.PublishLeaseToken = null;
        version.PublishLeaseExpiresAt = null;

        // PublishAsync may need to flush an intermediate SaveChanges (to archive a prior default-published
        // version before this version's own Status becomes Published - see ContentPublishingService) before
        // the final SaveChanges below persists this version's Published status and the outbox event. Both
        // saves must commit or roll back as one unit, so wrap them in an explicit transaction.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        var publishResult = await publishingService.PublishAsync(version, currentActor.UserId, ct);
        var publishedItem = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        EnqueueContentEvent("content.version_published", publishedItem, publishResult.Version);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(new PublishContentVersionResponse(await ToVersionDetailResponseAsync(publishResult.Version, DateTimeOffset.UtcNow, ct: ct), publishResult.Warnings));
    }

    [HttpPost("{id:guid}/versions/{versionNumber:int}/upgrade-template-version")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<ContentVersionDetailResponse>> UpgradeTemplateVersion(Guid workspaceId, Guid id, int versionNumber, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpgradeTemplateVersionRequest? request, CancellationToken ct)
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

        var currentTemplateVersion = await dbContext.TemplateVersions.AsNoTracking()
            .Include(tv => tv.Fields)
            .FirstAsync(tv => tv.Id == version.TemplateVersionId, ct);
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

        if (request?.Fields is { } fields)
        {
            // Caller supplied explicit values for the target template version: this is a full
            // replacement of the version's field values (ApplyVersionFieldValuesAsync removes every
            // existing value first, matching UpdateVersion's semantics), validated against the
            // finished result as one atomic step - not the key-remap below. This is what lets an
            // upgrade onto a template version that added a required field succeed: the remap has no
            // old value to carry over for a brand-new field, but the caller can supply one directly.
            // Field ids in `fields` refer to the TARGET template version's fields.
            if (await ApplyVersionFieldValuesAsync(version, target, fields, ct) is { } fieldError)
            {
                return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Content does not satisfy the target template version", fieldError);
            }
        }
        else
        {
            // No body, or a body with Fields null: exactly today's behaviour, unchanged. Every
            // existing caller (including the .NET SDK's original UpgradeTemplateVersionAsync overload)
            // sends no body and must keep hitting this path byte for byte.
            //
            // A package re-import always mints brand-new TemplateField rows for every field in a
            // template version - even one whose key didn't change at all - so field identity across
            // versions lives in the key, not the row's own Id (see PackagesController.ToField, which
            // never reuses a prior field's Id). Remap each value onto the target field with the same
            // key instead of matching by Id, so upgrading doesn't wipe every value the instant the
            // schema changes at all; only a value whose key genuinely no longer exists is dropped.
            var oldFieldKeyById = currentTemplateVersion.Fields.ToDictionary(field => field.Id, field => field.Key);
            var targetFieldIdByKey = target.Fields
                .GroupBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
            var stale = new List<ContentVersionFieldValue>();
            foreach (var value in version.FieldValues)
            {
                if (oldFieldKeyById.TryGetValue(value.FieldId, out var key) && targetFieldIdByKey.TryGetValue(key, out var newFieldId))
                {
                    value.FieldId = newFieldId;
                }
                else
                {
                    stale.Add(value);
                }
            }
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
        }

        var item = await dbContext.ContentItems.FirstAsync(candidate => candidate.Id == id, ct);
        item.SearchVector = searchVectorBuilder.Build(version, target);

        // Keep the item-level TemplateVersionId (an informational field read by List's
        // templateVersionId/templateId filters and by the item summary/detail "template name")
        // in step with whichever template version the content is actually built on, but only when
        // the version just upgraded is the item's LATEST one. Upgrading an older, non-latest draft
        // must not make the item's own field regress past a newer version that's already ahead of
        // it - CreateVersion above no longer depends on this field for validation (it now always
        // resolves the correct template version itself), so this is purely about keeping the
        // item-level view accurate for callers that display or filter by it.
        var isLatestVersion = !await dbContext.ContentVersions.AnyAsync(v => v.ContentItemId == id && v.VersionNumber > version.VersionNumber, ct);
        if (isLatestVersion)
        {
            item.TemplateVersionId = target.Id;
        }

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

    private Task<string?> ApplyVersionFieldValuesAsync(ContentVersion version, TemplateVersion templateVersion, IReadOnlyList<ContentFieldValueRequest> fields, CancellationToken ct) =>
        fieldWriter.ApplyAsync(version, templateVersion, fields.Select(ContentVersionWriteMappings.ToFieldInput).ToList(), ct);

    private Task<string?> ValidatePickListValuesAsync(ContentVersion version, TemplateVersion templateVersion, CancellationToken ct) =>
        fieldWriter.ValidateAsync(version, templateVersion, ct);

    private async Task<ContentItemSummaryResponse> ToItemSummaryResponseAsync(ContentItem content, CancellationToken ct)
    {
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.Id == content.TemplateVersionId)
            .Select(version => dbContext.Templates.Where(t => t.Id == version.TemplateId).Select(t => new { t.Name, t.Slug }).First())
            .FirstAsync(ct);
        var tags = await GetTagNamesAsync(content.Id, ct);
        var versions = await dbContext.ContentVersions.AsNoTracking().Where(v => v.ContentItemId == content.Id).ToListAsync(ct);
        var currentlyServing = ComputeCurrentlyServing(versions, DateTimeOffset.UtcNow);
        return new ContentItemSummaryResponse(
            content.Id, content.TemplateVersionId, template.Name, content.Slug, content.LocaleCode, content.TranslationGroupId,
            tags, content.CreatedAt, content.UpdatedAt, versions.Count,
            currentlyServing is null ? null : ToVersionSummaryResponse(currentlyServing),
            template.Slug);
    }

    private async Task<ContentItemDetailResponse> ToItemDetailResponseAsync(Guid id, CancellationToken ct)
    {
        var content = await dbContext.ContentItems.AsNoTracking().FirstAsync(item => item.Id == id, ct);
        var template = await dbContext.TemplateVersions.AsNoTracking()
            .Where(version => version.Id == content.TemplateVersionId)
            .Select(version => dbContext.Templates.Where(t => t.Id == version.TemplateId).Select(t => new { t.Name, t.Slug }).First())
            .FirstAsync(ct);
        var tags = await GetTagNamesAsync(content.Id, ct);
        var versions = await dbContext.ContentVersions.AsNoTracking()
            .Where(v => v.ContentItemId == id)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(ct);
        var currentlyServing = ComputeCurrentlyServing(versions, DateTimeOffset.UtcNow);
        return new ContentItemDetailResponse(
            content.Id, content.TemplateVersionId, template.Name, content.Slug, content.LocaleCode, content.TranslationGroupId,
            tags, content.CreatedAt, content.UpdatedAt,
            currentlyServing is null ? null : ToVersionSummaryResponse(currentlyServing),
            versions.Select(ToVersionSummaryResponse).ToList(),
            template.Slug);
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
        ContentVersionDetailProjector.SelectMostSpecific(versions, asOf);

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

    private async Task<ContentVersionDetailResponse> ToVersionDetailResponseAsync(ContentVersion version, DateTimeOffset asOf, bool expandChildren = true, CancellationToken ct = default) =>
        ContentVersionWriteMappings.ToResponse(await detailProjector.ProjectAsync(version, asOf, expandChildren, ct));

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
