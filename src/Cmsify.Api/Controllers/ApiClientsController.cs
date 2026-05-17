using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/clients")]
[RequireRole(UserRole.Admin)]
public sealed class ApiClientsController : ControllerBase
{
    private readonly IApiClientRepository apiClientRepository;
    private readonly ICurrentActor currentActor;
    private readonly IConfiguration configuration;

    public ApiClientsController(IApiClientRepository apiClientRepository, ICurrentActor currentActor, IConfiguration configuration)
    {
        this.apiClientRepository = apiClientRepository;
        this.currentActor = currentActor;
        this.configuration = configuration;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<ApiClientDto>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var result = await apiClientRepository.ListAsync(new PageRequest((Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 200), Math.Clamp(pageSize, 1, 200)), ct);
        return Ok(new PagedResponse<ApiClientDto>(result.Items, result.TotalCount, Math.Max(1, page), result.Limit));
    }

    [HttpPost]
    public async Task<ActionResult<CreateApiClientResponse>> Create(CreateApiClientRequest request, CancellationToken ct)
    {
        var rawToken = TokenUtility.GenerateApiToken();
        var tokenHash = BCrypt.Net.BCrypt.HashPassword(rawToken, configuration.GetValue("Auth:BcryptCost", 12));
        var command = new CreateApiClientCommand(request.Name, request.Description, request.Role, request.WorkspaceId, request.ExpiresAt, currentActor.UserId!.Value);
        var client = await apiClientRepository.CreateAsync(command, tokenHash, ct);
        return CreatedAtAction(nameof(Get), new { id = client.Id }, new CreateApiClientResponse(client, rawToken, "Store this token securely - it cannot be retrieved again."));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiClientDto>> Get(Guid id, CancellationToken ct)
    {
        var client = await apiClientRepository.GetAsync(id, ct);
        return client is null ? NotFound() : Ok(client);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<ActionResult<ApiClientDto>> Revoke(Guid id, CancellationToken ct)
    {
        var client = await apiClientRepository.GetAsync(id, ct);
        if (client is null)
        {
            return NotFound();
        }

        return Ok(await apiClientRepository.UpdateAsync(new UpdateApiClientCommand(id, client.Name, client.Description, client.Role, client.WorkspaceId, false, client.ExpiresAt), ct));
    }

    [HttpPost("{id:guid}/rotate")]
    public async Task<ActionResult<CreateApiClientResponse>> Rotate(Guid id, CancellationToken ct)
    {
        var client = await apiClientRepository.GetAsync(id, ct);
        if (client is null)
        {
            return NotFound();
        }

        var rawToken = TokenUtility.GenerateApiToken();
        var tokenHash = BCrypt.Net.BCrypt.HashPassword(rawToken, configuration.GetValue("Auth:BcryptCost", 12));
        await apiClientRepository.SoftDeleteAsync(id, currentActor.UserId!.Value, ct);
        var created = await apiClientRepository.CreateAsync(new CreateApiClientCommand(client.Name, client.Description, client.Role, client.WorkspaceId, client.ExpiresAt, currentActor.UserId!.Value), tokenHash, ct);
        return Ok(new CreateApiClientResponse(created, rawToken, "Store this token securely - it cannot be retrieved again."));
    }
}

public sealed record CreateApiClientRequest(string Name, string? Description, UserRole Role, Guid? WorkspaceId, DateTimeOffset? ExpiresAt);

public sealed record CreateApiClientResponse(ApiClientDto Client, string Token, string Warning);
