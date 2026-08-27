using System.Net.Mime;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyntaxCircus.Storage;
using SyntaxCircus.Cmsify.Contracts;
using UserRole = Cmsify.Core.Domain.Enums.UserRole;
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
    private readonly SyntaxCircus.Storage.IStorageProvider storageProvider;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IConfiguration configuration;
    private readonly MediaOperationalOptions mediaOperations;
    private readonly string storageProviderName;

    public MediaController(CmsifyDbContext dbContext, SyntaxCircus.Storage.IStorageProvider storageProvider, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, IConfiguration configuration, IOptions<MediaOperationalOptions> mediaOperations)
    {
        this.dbContext = dbContext;
        this.storageProvider = storageProvider;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.configuration = configuration;
        this.mediaOperations = mediaOperations.Value;
        storageProviderName = (configuration["Storage:Provider"] ?? "local").ToLowerInvariant();
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

        var now = DateTimeOffset.UtcNow;
        var assetId = Guid.CreateVersion7();
        var fileName = Path.GetFileName(file.FileName);
        var asset = new MediaAsset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            FileName = fileName,
            MimeType = file.ContentType,
            SizeBytes = file.Length,
            StorageKey = StorageKeyBuilder.Build(workspaceId, assetId, fileName, now),
            StorageProvider = storageProviderName,
            AltText = altText,
            CreatedByUserId = currentActor.UserId,
            BlobState = MediaBlobState.PendingUpload,
            BlobStateChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.MediaAssets.Add(asset);
        await dbContext.SaveChangesAsync(ct);
        try
        {
            await using var stream = file.OpenReadStream();
            var stored = await storageProvider.StoreAsync(
                new StoreObjectRequest(asset.StorageKey, stream, file.ContentType), ct);
            asset.SizeBytes = stored.SizeBytes;
            asset.TransitionBlobState(MediaBlobState.Available, DateTimeOffset.UtcNow);
            asset.UpdatedAt = asset.BlobStateChangedAt;
            await dbContext.SaveChangesAsync(ct);
        }
        catch
        {
            await BestEffortFailUploadAsync(asset.Id, CancellationToken.None);
            throw;
        }

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

        var query = dbContext.MediaAssets.AsNoTracking().Where(asset =>
            asset.WorkspaceId == workspaceId && !asset.IsDeleted && asset.BlobState == MediaBlobState.Available);
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            query = query.Where(asset => asset.MimeType.StartsWith(mimeType));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(asset => EF.Functions.ILike(asset.FileName, $"%{search}%"));
        }

        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<MediaAssetResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var items = await query.OrderBy(asset => asset.FileName)
            .Skip(offset)
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

        var stored = await storageProvider.ReadAsync(asset.StorageKey, ct);
        if (stored is null)
        {
            return this.Error(StatusCodes.Status404NotFound, "media-blob-missing", "Media blob is missing");
        }

        HttpContext.Response.RegisterForDisposeAsync(stored);
        var contentDisposition = new ContentDisposition
        {
            FileName = asset.FileName,
            Inline = asset.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        };
        Response.Headers[HeaderNames.ContentDisposition] = contentDisposition.ToString();
        return File(stored.Content, asset.MimeType);
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

        var now = DateTimeOffset.UtcNow;
        var purgeAfter = now.AddDays(mediaOperations.RetentionDays);
        asset.TransitionBlobState(MediaBlobState.DeletePending, now, purgeAfter);
        asset.IsDeleted = true;
        asset.DeletedAt = now;
        asset.DeletedByUserId = currentActor.UserId;
        asset.UpdatedAt = now;
        dbContext.MediaDeletionIntents.Add(new MediaDeletionIntent
        {
            MediaAssetId = asset.Id,
            Provider = asset.StorageProvider,
            StorageKey = asset.StorageKey,
            Reason = "user_delete",
            NotBefore = purgeAfter,
            NextAttemptAt = purgeAfter,
            CreatedAt = now
        });
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

        var query = dbContext.MediaAssets.Where(asset =>
            asset.Id == id && asset.WorkspaceId == workspaceId && !asset.IsDeleted && asset.BlobState == MediaBlobState.Available);
        return await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private async Task BestEffortFailUploadAsync(Guid assetId, CancellationToken ct)
    {
        try
        {
            dbContext.ChangeTracker.Clear();
            var failed = await dbContext.MediaAssets.SingleOrDefaultAsync(asset => asset.Id == assetId, ct);
            if (failed?.BlobState != MediaBlobState.PendingUpload) return;
            var now = DateTimeOffset.UtcNow;
            failed.TransitionBlobState(MediaBlobState.UploadFailed, now);
            failed.UpdatedAt = now;
            dbContext.MediaDeletionIntents.Add(new MediaDeletionIntent
            {
                MediaAssetId = failed.Id,
                Provider = failed.StorageProvider,
                StorageKey = failed.StorageKey,
                Reason = "upload_failed",
                NotBefore = now,
                NextAttemptAt = now,
                CreatedAt = now
            });
            await dbContext.SaveChangesAsync(ct);
        }
        catch
        {
            // A stale PendingUpload row remains authoritative for reconciliation.
        }
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
