using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Repositories;
using Microsoft.AspNetCore.Mvc;
using IpBanEventQueryRequest = SyntaxCircus.Cmsify.Contracts.IpBanEventQueryRequest;
using IpBanEventResponse = SyntaxCircus.Cmsify.Contracts.IpBanEventResponse;

namespace Cmsify.Api.Controllers;

[ApiController]
[RequireRole(UserRole.Admin)]
public sealed class IpBanController : ControllerBase
{
    private readonly IIpBanEventRepository ipBanEvents;

    public IpBanController(IIpBanEventRepository ipBanEvents) => this.ipBanEvents = ipBanEvents;

    [HttpGet("api/v1/security/ip-bans")]
    public async Task<ActionResult<SyntaxCircus.Cmsify.Contracts.PagedResponse<IpBanEventResponse>>> Query([FromQuery] IpBanEventQueryRequest request, CancellationToken ct)
    {
        var pageSize = request.PageSize;
        if (!ControllerHelpers.TryOffset(request.Page, pageSize, out var offset))
        {
            var countResult = await ipBanEvents.QueryAsync(new IpBanEventQuery(request.IpAddress, new PageRequest(0, 1)), ct);
            return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<IpBanEventResponse>([], countResult.TotalCount, request.Page, pageSize));
        }

        var result = await ipBanEvents.QueryAsync(new IpBanEventQuery(request.IpAddress, new PageRequest(offset, pageSize)), ct);
        var responses = result.Items.Select(banEvent => new IpBanEventResponse(banEvent.Id, banEvent.IpAddress, banEvent.RejectionCount, banEvent.BannedAt, banEvent.BannedUntil, banEvent.RequestPath)).ToArray();

        return Ok(new SyntaxCircus.Cmsify.Contracts.PagedResponse<IpBanEventResponse>(responses, result.TotalCount, request.Page, pageSize));
    }
}
