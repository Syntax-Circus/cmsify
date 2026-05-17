using System.Text;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cmsify.Api.Controllers;

[ApiController]
public sealed class SettingsController : ControllerBase
{
    private readonly CmsifyDbContext dbContext;
    private readonly ICurrentActor currentActor;
    private readonly IConfiguration configuration;
    private readonly IStorageProvider storageProvider;

    public SettingsController(CmsifyDbContext dbContext, ICurrentActor currentActor, IConfiguration configuration, IStorageProvider storageProvider)
    {
        this.dbContext = dbContext;
        this.currentActor = currentActor;
        this.configuration = configuration;
        this.storageProvider = storageProvider;
    }

    [HttpGet("api/v1/account/preferences")]
    [RequireRole(UserRole.Reader)]
    public async Task<ActionResult<AccountPreferencesResponse>> GetPreferences(CancellationToken ct)
    {
        if (!currentActor.UserId.HasValue)
        {
            return BadRequest("Only user sessions have account preferences.");
        }

        var user = await dbContext.Users.AsNoTracking().FirstAsync(candidate => candidate.Id == currentActor.UserId.Value, ct);
        return Ok(new AccountPreferencesResponse(user.Id, user.DisplayName, user.Email, user.TimeZoneId, user.Theme ?? "auto"));
    }

    [HttpPut("api/v1/account/preferences")]
    [RequireRole(UserRole.Reader)]
    public async Task<ActionResult<AccountPreferencesResponse>> UpdatePreferences(UpdateAccountPreferencesRequest request, CancellationToken ct)
    {
        if (!currentActor.UserId.HasValue)
        {
            return BadRequest("Only user sessions have account preferences.");
        }

        var user = await dbContext.Users.FirstAsync(candidate => candidate.Id == currentActor.UserId.Value, ct);
        user.TimeZoneId = request.TimeZoneId;
        user.Theme = string.IsNullOrWhiteSpace(request.Theme) ? "auto" : request.Theme;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        return Ok(new AccountPreferencesResponse(user.Id, user.DisplayName, user.Email, user.TimeZoneId, user.Theme));
    }

    [HttpGet("api/v1/settings/storage")]
    [RequireRole(UserRole.Admin)]
    public ActionResult<StorageConfigResponse> GetStorage() =>
        Ok(new StorageConfigResponse(configuration["Storage:Provider"] ?? "local", true));

    [HttpPost("api/v1/settings/storage/test")]
    [RequireRole(UserRole.Admin)]
    public async Task<ActionResult<StorageTestResponse>> TestStorage(CancellationToken ct)
    {
        var provider = configuration["Storage:Provider"] ?? "local";
        var bytes = Encoding.UTF8.GetBytes("cmsify storage test");
        await using var stream = new MemoryStream(bytes);
        var stored = await storageProvider.StoreAsync(stream, "storage-test.txt", "text/plain", ct);
        var exists = await storageProvider.ExistsAsync(stored.StorageKey, ct);
        await storageProvider.DeleteAsync(stored.StorageKey, ct);
        return Ok(new StorageTestResponse(provider, exists, exists ? "Storage connection test succeeded." : "Storage connection test failed."));
    }
}

public sealed record AccountPreferencesResponse(Guid UserId, string DisplayName, string Email, string? TimeZoneId, string Theme);
public sealed record UpdateAccountPreferencesRequest(string? TimeZoneId, string Theme);
public sealed record StorageConfigResponse(string Provider, bool IsConfigured);
public sealed record StorageTestResponse(string Provider, bool Success, string Message);
