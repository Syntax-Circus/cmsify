using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class MediaApiTests : IAsyncLifetime
{
    private const string ApiToken = "cmsify_media_api_test_token";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    private string storagePath = string.Empty;

    public async Task InitializeAsync()
    {
        storagePath = Path.Combine(Path.GetTempPath(), "cmsify-media-tests", Guid.NewGuid().ToString("N"));
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", "admin@example.test");
        Environment.SetEnvironmentVariable("Seed__Admin__Password", "change-this-temporary-password");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
        Environment.SetEnvironmentVariable("Storage__Provider", "local");
        Environment.SetEnvironmentVariable("Storage__Local__BasePath", storagePath);
        Environment.SetEnvironmentVariable("Media__AllowedMimeTypes", "text/plain,image/");
        Environment.SetEnvironmentVariable("Media__MaxFileSizeMb", "1");
    }

    public async Task DisposeAsync()
    {
        await postgres.DisposeAsync().AsTask();
        if (Directory.Exists(storagePath))
        {
            Directory.Delete(storagePath, recursive: true);
        }

        ClearEnvironment();
    }

    [Fact]
    public async Task UploadRetrieveAndDelete_Works()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        using var upload = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes("hello media"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(file, "file", "hello.txt");
        upload.Add(new StringContent("Alt text"), "altText");

        var createResponse = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var assetId = created.GetProperty("id").GetGuid();

        var fileResponse = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{assetId}/file");
        fileResponse.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", fileResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("hello media", await fileResponse.Content.ReadAsStringAsync());

        var getResponse = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{assetId}");
        getResponse.EnsureSuccessStatusCode();
        var etag = getResponse.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/workspaces/{workspaceId}/media/{assetId}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        var deleteResponse = await client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenAssetIsReferencedByContent()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        var assetId = await SeedReferencedMediaAsync(factory, workspaceId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var getResponse = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{assetId}");
        getResponse.EnsureSuccessStatusCode();
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/workspaces/{workspaceId}/media/{assetId}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", getResponse.Headers.ETag?.ToString());
        var deleteResponse = await client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>();

    private static async Task<Guid> SeedApiClientAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var workspaceId = await dbContext.Workspaces.Select(workspace => workspace.Id).FirstAsync();
        var adminUserId = await dbContext.Users.Select(user => user.Id).FirstAsync();
        if (!await dbContext.ApiClients.AnyAsync(client => client.Name == "Media API Test"))
        {
            dbContext.ApiClients.Add(new ApiClient
            {
                Name = "Media API Test",
                TokenHash = BCrypt.Net.BCrypt.HashPassword(ApiToken, 4),
                Role = UserRole.Admin,
                WorkspaceId = workspaceId,
                CreatedByUserId = adminUserId
            });
            await dbContext.SaveChangesAsync();
        }

        return workspaceId;
    }

    private static async Task<Guid> SeedReferencedMediaAsync(WebApplicationFactory<Program> factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var template = new Template { WorkspaceId = workspaceId, Name = "Asset Holder", Slug = $"asset-holder-{Guid.NewGuid():N}" };
        var version = new TemplateVersion { TemplateId = template.Id, VersionNumber = 1, Status = TemplateVersionStatus.Published };
        var field = new TemplateField
        {
            TemplateVersionId = version.Id,
            Key = "image",
            Label = "Image",
            PrimitiveType = PrimitiveType.Media,
            CompositionMode = CompositionMode.Inline
        };
        version.Fields.Add(field);
        template.Versions.Add(version);
        var asset = new MediaAsset
        {
            WorkspaceId = workspaceId,
            FileName = "referenced.txt",
            MimeType = "text/plain",
            SizeBytes = 10,
            StorageKey = "referenced.txt",
            StorageProvider = "local"
        };
        var content = new ContentItem { WorkspaceId = workspaceId, TemplateVersionId = version.Id, Status = ContentStatus.Published };
        content.FieldValues.Add(new ContentFieldValue { ContentItemId = content.Id, FieldId = field.Id, Order = 0, ValueKind = ValueKind.Media, MediaAssetId = asset.Id });
        dbContext.Templates.Add(template);
        await dbContext.SaveChangesAsync();
        template.CurrentVersionId = version.Id;
        dbContext.MediaAssets.Add(asset);
        dbContext.ContentItems.Add(content);
        await dbContext.SaveChangesAsync();
        return asset.Id;
    }

    private static void ClearEnvironment()
    {
        foreach (var key in new[]
        {
            "ConnectionStrings__Cmsify",
            "Seed__Admin__Email",
            "Seed__Admin__Password",
            "Seed__DefaultWorkspace__Name",
            "Seed__DefaultWorkspace__Slug",
            "Storage__Provider",
            "Storage__Local__BasePath",
            "Media__AllowedMimeTypes",
            "Media__MaxFileSizeMb"
        })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }
}
