using System.Security.Cryptography;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Cmsify.Core.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using SyntaxCircus.Cmsify.Contracts;
using ContractApiClientDto = SyntaxCircus.Cmsify.Contracts.ApiClientDto;
using UserRole = Cmsify.Core.Domain.Enums.UserRole;
using PaginationQuery = SyntaxCircus.Cmsify.Contracts.PaginationQuery;

namespace Cmsify.Api.Controllers;

[ApiController]
[Route("api/v1/clients")]
[RequireRole(UserRole.Admin)]
public sealed class ApiClientsController : ControllerBase
{
    private readonly IApiClientRepository apiClientRepository;
    private readonly ICurrentActor currentActor;
    private readonly IWorkspaceAuthorizationService workspaceAuthorization;
    private readonly IConfiguration configuration;

    public ApiClientsController(IApiClientRepository apiClientRepository, ICurrentActor currentActor, IWorkspaceAuthorizationService workspaceAuthorization, IConfiguration configuration)
    {
        this.apiClientRepository = apiClientRepository;
        this.currentActor = currentActor;
        this.workspaceAuthorization = workspaceAuthorization;
        this.configuration = configuration;
    }

    [HttpGet]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<ContractApiClientDto>>> List([FromQuery] PaginationQuery pagination, CancellationToken ct = default)
    {
        if (!ControllerHelpers.TryOffset(pagination.Page, pagination.PageSize, out var offset))
        {
            var countResult = await apiClientRepository.ListAsync(new PageRequest(0, 1), ct);
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContractApiClientDto>([], countResult.TotalCount, pagination.Page, pagination.PageSize));
        }

        var result = await apiClientRepository.ListAsync(new PageRequest(offset, pagination.PageSize), ct);
        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<ContractApiClientDto>(result.Items.Select(ContractMappings.ToContract).ToArray(), result.TotalCount, pagination.Page, pagination.PageSize));
    }

    [HttpPost]
    public async Task<ActionResult<CreateApiClientResponse>> Create(CreateApiClientRequest request, CancellationToken ct)
    {
        if (!await CanManageClientScopeAsync(request.WorkspaceId, ct))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var tokenIdentifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(12));
        var rawToken = TokenUtility.GenerateApiToken(tokenIdentifier);
        var tokenHash = BCrypt.Net.BCrypt.HashPassword(rawToken, configuration.GetValue("Auth:BcryptCost", 12));
        var command = new CreateApiClientCommand(request.Name, request.Description, request.Role.ToCore(), request.WorkspaceId, request.ExpiresAt, currentActor.UserId!.Value);
        var client = await apiClientRepository.CreateAsync(command, tokenHash, tokenIdentifier, ct);
        return CreatedAtAction(nameof(Get), new { id = client.Id }, new CreateApiClientResponse(client.ToContract(), rawToken, "Store this token securely - it cannot be retrieved again."));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContractApiClientDto>> Get(Guid id, CancellationToken ct)
    {
        var client = await apiClientRepository.GetAsync(id, ct);
        return client is null ? NotFound() : Ok(client.ToContract());
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<ActionResult<ContractApiClientDto>> Revoke(Guid id, CancellationToken ct)
    {
        var client = await apiClientRepository.GetAsync(id, ct);
        if (client is null)
        {
            return NotFound();
        }

        return Ok((await apiClientRepository.UpdateAsync(new UpdateApiClientCommand(id, client.Name, client.Description, client.Role, client.WorkspaceId, false, client.ExpiresAt), ct)).ToContract());
    }

    [HttpPost("{id:guid}/rotate")]
    public async Task<ActionResult<CreateApiClientResponse>> Rotate(Guid id, CancellationToken ct)
    {
        var client = await apiClientRepository.GetAsync(id, ct);
        if (client is null)
        {
            return NotFound();
        }

        var tokenIdentifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(12));
        var rawToken = TokenUtility.GenerateApiToken(tokenIdentifier);
        var tokenHash = BCrypt.Net.BCrypt.HashPassword(rawToken, configuration.GetValue("Auth:BcryptCost", 12));
        await apiClientRepository.SoftDeleteAsync(id, currentActor.UserId!.Value, ct);
        var created = await apiClientRepository.CreateAsync(new CreateApiClientCommand(client.Name, client.Description, client.Role, client.WorkspaceId, client.ExpiresAt, currentActor.UserId!.Value), tokenHash, tokenIdentifier, ct);
        return Ok(new CreateApiClientResponse(created.ToContract(), rawToken, "Store this token securely - it cannot be retrieved again."));
    }

    private Task<bool> CanManageClientScopeAsync(Guid? workspaceId, CancellationToken ct) =>
        workspaceId.HasValue
            ? workspaceAuthorization.CanWriteWorkspaceAsync(workspaceId.Value, ct)
            : Task.FromResult(currentActor.IsSuperAdmin);
}
