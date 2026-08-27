using System.Text;
using Cmsify.Api.Auth;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using SyntaxCircus.Cmsify.Contracts;
using UserRole = Cmsify.Core.Domain.Enums.UserRole;
using Microsoft.EntityFrameworkCore;
using SyntaxCircus.Storage;

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
            return this.Error(StatusCodes.Status400BadRequest, CmsifyError.BadRequest, "Only user sessions have account preferences.");
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
            return this.Error(StatusCodes.Status400BadRequest, CmsifyError.BadRequest, "Only user sessions have account preferences.");
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
        var key = $"cmsify/media/storage-test/{Guid.CreateVersion7()}_storage-test.txt";
        var stored = await storageProvider.StoreAsync(new StoreObjectRequest(key, stream, "text/plain"), ct);
        var exists = await storageProvider.GetMetadataAsync(stored.Key, ct) is not null;
        await storageProvider.DeleteAsync(stored.Key, ct);
        return Ok(new StorageTestResponse(provider, exists, exists ? "Storage connection test succeeded." : "Storage connection test failed."));
    }
}
