using System.Net.Mime;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;
using Microsoft.Net.Http.Headers;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/media")]
[RequireRole(UserRole.Reader)]
public sealed class MediaController : ControllerBase
{
    private static readonly string[] DefaultAllowedMimeTypes =
    [
        "image/",
        "audio/",
        "video/",
        "application/pdf",
        "text/plain",
        "application/json",
        "application/msword",
        "application/vnd.openxmlformats-officedocument"
    ];

    private readonly CmsifyDbContext dbContext;
    private readonly IStorageProvider storageProvider;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IConfiguration configuration;

    public MediaController(CmsifyDbContext dbContext, IStorageProvider storageProvider, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, IConfiguration configuration)
    {
        this.dbContext = dbContext;
        this.storageProvider = storageProvider;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.configuration = configuration;
    }

    [HttpPost]
    [RequireRole(UserRole.Editor)]
    [RequestSizeLimit(1_073_741_824)]
    public async Task<ActionResult<MediaAssetResponse>> Upload(Guid workspaceId, IFormFile file, [FromForm] string? altText, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        if (file.Length == 0)
        {
            return this.Error(StatusCodes.Status400BadRequest, "bad-request", "File is required");
        }

        var maxBytes = configuration.GetValue("Media:MaxFileSizeMb", 50) * 1024L * 1024L;
        if (file.Length > maxBytes)
        {
            return this.Error(StatusCodes.Status413PayloadTooLarge, "bad-request", "File is too large", $"The uploaded file exceeds the configured limit of {maxBytes} bytes.");
        }

        if (!IsAllowedMimeType(file.ContentType))
        {
            return this.Error(StatusCodes.Status415UnsupportedMediaType, "bad-request", "MIME type is not allowed", $"The MIME type '{file.ContentType}' is not allowed.");
        }

        await using var stream = file.OpenReadStream();
        var stored = await storageProvider.StoreAsync(stream, Path.GetFileName(file.FileName), file.ContentType, ct);
        var asset = new MediaAsset
        {
            WorkspaceId = workspaceId,
            FileName = Path.GetFileName(file.FileName),
            MimeType = file.ContentType,
            SizeBytes = stored.SizeBytes,
            StorageKey = stored.StorageKey,
            StorageProvider = stored.Provider,
            AltText = altText,
            CreatedByUserId = currentActor.UserId
        };

        dbContext.MediaAssets.Add(asset);
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(asset.UpdatedAt);
        return CreatedAtAction(nameof(Get), new { workspaceId, id = asset.Id }, ToResponse(asset));
    }

    [HttpGet]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<MediaAssetResponse>>> List(Guid workspaceId, [FromQuery] PaginationQuery pagination, [FromQuery] string? mimeType = null, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var query = dbContext.MediaAssets.AsNoTracking().Where(asset => asset.WorkspaceId == workspaceId && !asset.IsDeleted);
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            query = query.Where(asset => asset.MimeType.StartsWith(mimeType));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(asset => EF.Functions.ILike(asset.FileName, $"%{search}%"));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(asset => asset.FileName)
            .Skip(ControllerHelpers.Offset(pagination.Page, pagination.PageSize))
            .Take(pagination.PageSize)
            .Select(asset => ToResponse(asset))
            .ToListAsync(ct);

        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<MediaAssetResponse>(items, total, pagination.Page, pagination.PageSize));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MediaAssetResponse>> Get(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var asset = await FindAssetAsync(workspaceId, id, requireWrite: false, tracking: false, ct);
        if (asset is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(asset.UpdatedAt);
        return Ok(ToResponse(asset));
    }

    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> GetFile(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var asset = await FindAssetAsync(workspaceId, id, requireWrite: false, tracking: false, ct);
        if (asset is null)
        {
            return NotFound();
        }

        var stream = await storageProvider.RetrieveAsync(asset.StorageKey, ct);
        var contentDisposition = new ContentDisposition
        {
            FileName = asset.FileName,
            Inline = asset.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        };
        Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
        return File(stream, asset.MimeType);
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<MediaAssetResponse>> Update(Guid workspaceId, Guid id, UpdateMediaAssetRequest request, CancellationToken ct)
    {
        var asset = await FindAssetAsync(workspaceId, id, requireWrite: true, tracking: true, ct);
        if (asset is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(asset.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        asset.AltText = request.AltText;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(asset.UpdatedAt);
        return Ok(ToResponse(asset));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var asset = await FindAssetAsync(workspaceId, id, requireWrite: true, tracking: true, ct);
        if (asset is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(asset.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        var referencedBy = await dbContext.ContentFieldValues.AsNoTracking()
            .Where(value => value.MediaAssetId == id || value.FileAssetId == id)
            .Where(value => dbContext.ContentItems.Any(content => content.Id == value.ContentItemId && !content.IsDeleted))
            .Select(value => value.ContentItemId)
            .Distinct()
            .ToListAsync(ct);
        if (referencedBy.Count > 0)
        {
            return this.Error(StatusCodes.Status409Conflict, "referenced-by-other-entity", "Media asset is referenced by content", extensions: new Dictionary<string, object?> { ["referencedBy"] = referencedBy });
        }

        asset.IsDeleted = true;
        asset.DeletedAt = DateTimeOffset.UtcNow;
        asset.DeletedByUserId = currentActor.UserId;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<MediaAsset?> FindAssetAsync(Guid workspaceId, Guid id, bool requireWrite, bool tracking, CancellationToken ct)
    {
        var canAccess = requireWrite
            ? await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct)
            : await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct);
        if (!canAccess)
        {
            return null;
        }

        var query = dbContext.MediaAssets.Where(asset => asset.Id == id && asset.WorkspaceId == workspaceId && !asset.IsDeleted);
        return await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private bool IsAllowedMimeType(string mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        var allowed = configuration.GetSection("Media:AllowedMimeTypes").Get<string[]>()
            ?? configuration["Media:AllowedMimeTypes"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? DefaultAllowedMimeTypes;
        return allowed.Any(allowedType => allowedType.EndsWith("/", StringComparison.Ordinal)
            ? mimeType.StartsWith(allowedType, StringComparison.OrdinalIgnoreCase)
            : mimeType.Equals(allowedType, StringComparison.OrdinalIgnoreCase) || mimeType.StartsWith($"{allowedType}.", StringComparison.OrdinalIgnoreCase));
    }

    private static MediaAssetResponse ToResponse(MediaAsset asset) =>
        new(asset.Id, asset.FileName, asset.MimeType, asset.SizeBytes, asset.AltText, $"/api/v1/workspaces/{asset.WorkspaceId}/media/{asset.Id}/file", asset.CreatedAt, asset.UpdatedAt);
}

public sealed record UpdateMediaAssetRequest(string? AltText);
public sealed record MediaAssetResponse(Guid Id, string FileName, string MimeType, long SizeBytes, string? AltText, string Url, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
