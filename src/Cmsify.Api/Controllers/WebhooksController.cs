using System.Security.Cryptography;
using System.Text.Json;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/workspaces/{workspaceId:guid}/webhooks")]
[RequireRole(UserRole.Reader)]
public sealed class WebhooksController : ControllerBase
{
    private static readonly IReadOnlySet<string> KnownEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "content.created",
        "content.updated",
        "content.status_changed",
        "content.published",
        "content.archived",
        "content.deleted",
        "template.version_published",
        "workspace.updated"
    };

    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly ISecretProtector secretProtector;
    private readonly IWebhookDestinationValidator destinationValidator;

    public WebhooksController(CmsifyDbContext dbContext, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, ISecretProtector secretProtector, IWebhookDestinationValidator destinationValidator)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.secretProtector = secretProtector;
        this.destinationValidator = destinationValidator;
    }

    [HttpGet]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<WebhookEndpointResponse>>> List(Guid workspaceId, [FromQuery] PaginationQuery pagination, CancellationToken ct = default)
    {
        if (!await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        var query = dbContext.WebhookEndpoints.AsNoTracking()
            .Include(endpoint => endpoint.Subscriptions)
            .Where(endpoint => endpoint.WorkspaceId == workspaceId && !endpoint.IsDeleted)
            .OrderBy(endpoint => endpoint.Name);
        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<WebhookEndpointResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var items = await query.Skip(offset).Take(pagination.PageSize).Select(endpoint => ToResponse(endpoint)).ToListAsync(ct);
        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<WebhookEndpointResponse>(items, total, pagination.Page, pagination.PageSize));
    }

    [HttpPost]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<CreateWebhookEndpointResponse>> Create(Guid workspaceId, CreateWebhookEndpointRequest request, CancellationToken ct)
    {
        if (!await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct))
        {
            return NotFound();
        }

        if (!ValidateEvents(request.Events, out var eventError))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Unsupported webhook event", eventError);
        }

        var destination = await destinationValidator.ValidateAsync(request.Url, ct);
        if (!destination.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid webhook URL", destination.Error);
        }

        var secret = string.IsNullOrWhiteSpace(request.Secret) ? GenerateSecret() : request.Secret;
        var createdByUserId = currentActor.UserId
            ?? await dbContext.Users.OrderBy(user => user.CreatedAt).Select(user => user.Id).FirstAsync(ct);
        var endpoint = new WebhookEndpoint
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Url = destination.NormalizedUrl!,
            Secret = secretProtector.Protect(secret),
            IsActive = true,
            CreatedByUserId = createdByUserId
        };

        foreach (var eventType in request.Events.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = eventType });
        }

        dbContext.WebhookEndpoints.Add(endpoint);
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(endpoint.UpdatedAt);
        return CreatedAtAction(nameof(Get), new { workspaceId, id = endpoint.Id }, new CreateWebhookEndpointResponse(ToResponse(endpoint), secret));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WebhookEndpointResponse>> Get(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var endpoint = await FindEndpointAsync(workspaceId, id, requireWrite: false, tracking: false, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = ControllerHelpers.ETag(endpoint.UpdatedAt);
        return Ok(ToResponse(endpoint));
    }

    [HttpPut("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<WebhookEndpointResponse>> Update(Guid workspaceId, Guid id, UpdateWebhookEndpointRequest request, CancellationToken ct)
    {
        var endpoint = await FindEndpointAsync(workspaceId, id, requireWrite: true, tracking: true, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(endpoint.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        if (!ValidateEvents(request.Events, out var eventError))
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Unsupported webhook event", eventError);
        }

        var destination = await destinationValidator.ValidateAsync(request.Url, ct);
        if (!destination.IsValid)
        {
            return this.Error(StatusCodes.Status422UnprocessableEntity, "validation-failed", "Invalid webhook URL", destination.Error);
        }

        endpoint.Name = request.Name;
        endpoint.Url = destination.NormalizedUrl!;
        endpoint.IsActive = request.IsActive;
        endpoint.UpdatedAt = DateTimeOffset.UtcNow;
        dbContext.WebhookSubscriptions.RemoveRange(endpoint.Subscriptions);
        endpoint.Subscriptions.Clear();
        foreach (var eventType in request.Events.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            endpoint.Subscriptions.Add(new WebhookSubscription { WebhookEndpointId = endpoint.Id, EventType = eventType });
        }

        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(endpoint.UpdatedAt);
        return Ok(ToResponse(endpoint));
    }

    [HttpPost("{id:guid}/rotate-secret")]
    [RequireRole(UserRole.Editor)]
    public async Task<ActionResult<RotateWebhookSecretResponse>> RotateSecret(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var endpoint = await FindEndpointAsync(workspaceId, id, requireWrite: true, tracking: true, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        var secret = GenerateSecret();
        endpoint.Secret = secretProtector.Protect(secret);
        endpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        Response.Headers.ETag = ControllerHelpers.ETag(endpoint.UpdatedAt);
        return Ok(new RotateWebhookSecretResponse(endpoint.Id, secret, "Store this secret securely - it cannot be retrieved again."));
    }

    [HttpDelete("{id:guid}")]
    [RequireRole(UserRole.Editor)]
    public async Task<IActionResult> Delete(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var endpoint = await FindEndpointAsync(workspaceId, id, requireWrite: true, tracking: true, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        if (!this.IfMatchMatches(endpoint.UpdatedAt))
        {
            return this.Error(StatusCodes.Status412PreconditionFailed, "concurrency-mismatch", "Concurrency mismatch");
        }

        dbContext.WebhookDeliveryLogs.RemoveRange(dbContext.WebhookDeliveryLogs.Where(log => log.WebhookEndpointId == id));
        endpoint.IsDeleted = true;
        endpoint.IsActive = false;
        endpoint.DeletedAt = DateTimeOffset.UtcNow;
        endpoint.DeletedByUserId = currentActor.UserId;
        endpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/deliveries")]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<WebhookDeliveryResponse>>> ListDeliveries(Guid workspaceId, Guid id, [FromQuery] PaginationQuery pagination, [FromQuery] bool? isDelivered = null, [FromQuery] bool? isFailed = null, CancellationToken ct = default)
    {
        if (!await EndpointExistsAsync(workspaceId, id, requireWrite: false, ct))
        {
            return NotFound();
        }

        var query = dbContext.WebhookDeliveryLogs.AsNoTracking().Where(log => log.WebhookEndpointId == id);
        if (isDelivered.HasValue)
        {
            query = query.Where(log => log.IsDelivered == isDelivered.Value);
        }

        if (isFailed.HasValue)
        {
            query = query.Where(log => log.IsFailed == isFailed.Value);
        }

        var total = await query.CountAsync(ct);
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<WebhookDeliveryResponse>([], total, pagination.Page, pagination.PageSize));
        }

        var items = await query.OrderByDescending(log => log.CreatedAt)
            .Skip(offset)
            .Take(pagination.PageSize)
            .Select(log => new WebhookDeliveryResponse(log.Id, log.WebhookEndpointId, log.EventType, log.Payload, log.AttemptCount, log.LastAttemptAt, log.NextRetryAt, log.StatusCode, log.IsDelivered, log.IsFailed, log.CreatedAt))
            .ToListAsync(ct);
        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<WebhookDeliveryResponse>(items, total, pagination.Page, pagination.PageSize));
    }

    [HttpPost("{id:guid}/deliveries/{deliveryId:guid}/retry")]
    [RequireRole(UserRole.Editor)]
    public async Task<IActionResult> RetryDelivery(Guid workspaceId, Guid id, Guid deliveryId, CancellationToken ct)
    {
        if (!await EndpointExistsAsync(workspaceId, id, requireWrite: true, ct))
        {
            return NotFound();
        }

        var delivery = await dbContext.WebhookDeliveryLogs.FirstOrDefaultAsync(log => log.Id == deliveryId && log.WebhookEndpointId == id, ct);
        if (delivery is null)
        {
            return NotFound();
        }

        if (delivery.IsDelivered)
        {
            return this.Error(StatusCodes.Status409Conflict, "conflict", "Delivered webhook deliveries cannot be retried");
        }

        delivery.IsFailed = false;
        delivery.NextRetryAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return Accepted();
    }

    private async Task<WebhookEndpoint?> FindEndpointAsync(Guid workspaceId, Guid id, bool requireWrite, bool tracking, CancellationToken ct)
    {
        var canAccess = requireWrite
            ? await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct)
            : await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct);
        if (!canAccess)
        {
            return null;
        }

        var query = dbContext.WebhookEndpoints.Include(endpoint => endpoint.Subscriptions).Where(endpoint => endpoint.Id == id && endpoint.WorkspaceId == workspaceId && !endpoint.IsDeleted);
        return await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private async Task<bool> EndpointExistsAsync(Guid workspaceId, Guid id, bool requireWrite, CancellationToken ct) =>
        (requireWrite
            ? await workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId, ct)
            : await workspaceAuthorization.CanReadWorkspaceAsync(workspaceId, ct))
        && await dbContext.WebhookEndpoints.AnyAsync(endpoint => endpoint.Id == id && endpoint.WorkspaceId == workspaceId && !endpoint.IsDeleted, ct);

    private static bool ValidateEvents(IReadOnlyList<string> events, out string? error)
    {
        if (events.Count == 0)
        {
            error = "At least one webhook event is required.";
            return false;
        }

        var unsupported = events.FirstOrDefault(evt => !KnownEventTypes.Contains(evt));
        if (unsupported is not null)
        {
            error = $"Unsupported webhook event '{unsupported}'.";
            return false;
        }

        error = null;
        return true;
    }

    private static string GenerateSecret() => $"whsec_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static WebhookEndpointResponse ToResponse(WebhookEndpoint endpoint) =>
        new(endpoint.Id, endpoint.WorkspaceId, endpoint.Name, endpoint.Url, endpoint.IsActive, endpoint.CreatedAt, endpoint.UpdatedAt, endpoint.Subscriptions.Select(subscription => subscription.EventType).OrderBy(evt => evt).ToArray());
}

public sealed record CreateWebhookEndpointRequest(string Name, string Url, string? Secret, IReadOnlyList<string> Events);
public sealed record UpdateWebhookEndpointRequest(string Name, string Url, bool IsActive, IReadOnlyList<string> Events);
public sealed record WebhookEndpointResponse(Guid Id, Guid WorkspaceId, string Name, string Url, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<string> Events);
public sealed record CreateWebhookEndpointResponse(WebhookEndpointResponse Endpoint, string Secret);
public sealed record RotateWebhookSecretResponse(Guid Id, string Secret, string Warning);
public sealed record WebhookDeliveryResponse(Guid Id, Guid WebhookEndpointId, string EventType, JsonElement Payload, int AttemptCount, DateTimeOffset? LastAttemptAt, DateTimeOffset? NextRetryAt, int? StatusCode, bool IsDelivered, bool IsFailed, DateTimeOffset CreatedAt);
