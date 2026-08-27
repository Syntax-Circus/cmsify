using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SyntaxCircus.Storage;
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

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var persisted = await db.MediaAssets.SingleAsync(asset => asset.Id == assetId);
            Assert.Equal(MediaBlobState.Available, persisted.BlobState);
            Assert.StartsWith($"cmsify/media/{workspaceId}/", persisted.StorageKey, StringComparison.Ordinal);
            Assert.Contains($"/{assetId}_hello.txt", persisted.StorageKey, StringComparison.Ordinal);
        }

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
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var deleted = await db.MediaAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            var intent = await db.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == assetId);
            Assert.Equal(MediaBlobState.DeletePending, deleted.BlobState);
            Assert.True(deleted.IsDeleted);
            Assert.Equal("user_delete", intent.Reason);
            Assert.Equal(deleted.PurgeAfter, intent.NotBefore);
            Assert.InRange(intent.NotBefore, DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30).AddMinutes(1));
        }
    }

    [Fact]
    public async Task NonAvailableAssets_AreHiddenFromListGetAndFile()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        var ids = new List<Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            foreach (var state in new[] { MediaBlobState.PendingUpload, MediaBlobState.UploadFailed, MediaBlobState.Missing, MediaBlobState.DeletePending, MediaBlobState.Deleted })
            {
                var asset = NewAsset(workspaceId, $"{state}.txt", state);
                if (state is MediaBlobState.DeletePending or MediaBlobState.Deleted) asset.IsDeleted = true;
                db.MediaAssets.Add(asset);
                ids.Add(asset.Id);
            }
            await db.SaveChangesAsync();
        }

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{workspaceId}/media?page=1&pageSize=100");
        var listedIds = list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.DoesNotContain(listedIds, id => ids.Contains(id));
        foreach (var id in ids)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{id}/file")).StatusCode);
        }
    }

    [Fact]
    public async Task AvailableAssetWithMissingBlob_ReturnsSanitizedProblemDetails()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        Guid assetId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var asset = NewAsset(workspaceId, "missing.txt", MediaBlobState.Available);
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{assetId}/file");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.EndsWith("/media-blob-missing", problem.GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("default/missing", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialStorageFailure_MarksUploadFailedAndQueuesImmediateCleanup()
    {
        var storage = new TestStorageProvider { FailStoreAfterCapture = true };
        await using var factory = CreateFactory(storage);
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        using var upload = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes("partial"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(file, "file", "partial.txt");

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var failed = await db.MediaAssets.SingleAsync(asset => asset.FileName == "partial.txt");
        var intent = await db.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == failed.Id);
        Assert.Equal(MediaBlobState.UploadFailed, failed.BlobState);
        Assert.Equal("upload_failed", intent.Reason);
        Assert.True(intent.NextAttemptAt <= DateTimeOffset.UtcNow);
        Assert.Equal(failed.StorageKey, Assert.Single(storage.StoredKeys));
    }

    [Fact]
    public async Task DatabaseFailureBeforeStorage_DoesNotWriteBlob()
    {
        var storage = new TestStorageProvider();
        await using var factory = CreateFactory(storage);
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        using var upload = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes("too long"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(file, "file", $"{new string('x', 256)}.txt");

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(storage.StoredKeys);
    }

    [Fact]
    public async Task DatabaseFailureAfterStorage_MarksUploadFailedAndQueuesCleanup()
    {
        var storage = new TestStorageProvider();
        await using var factory = CreateFactory(storage);
        storage.AfterStoreAsync = async key =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var pending = await db.MediaAssets.SingleAsync(asset => asset.StorageKey == key);
            pending.UpdatedAt = pending.UpdatedAt.AddTicks(1);
            await db.SaveChangesAsync();
        };
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        using var upload = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes("stored first"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(file, "file", "concurrent.txt");

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var failed = await db.MediaAssets.SingleAsync(asset => asset.FileName == "concurrent.txt");
        Assert.Equal(MediaBlobState.UploadFailed, failed.BlobState);
        Assert.Equal("upload_failed", (await db.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == failed.Id)).Reason);
        Assert.Equal(failed.StorageKey, Assert.Single(storage.StoredKeys));
    }

    [Fact]
    public async Task FileResponse_DisposesSharedStorageReadResult()
    {
        var storage = new TestStorageProvider();
        await using var factory = CreateFactory(storage);
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        Guid assetId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var asset = NewAsset(workspaceId, "disposal.txt", MediaBlobState.Available);
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }
        storage.ReadResults["default/disposal.txt"] = Encoding.UTF8.GetBytes("dispose me");

        using (var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{assetId}/file"))
        {
            response.EnsureSuccessStatusCode();
            Assert.Equal("dispose me", await response.Content.ReadAsStringAsync());
        }

        Assert.True(storage.LastReadStream!.WasDisposed);
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

    [Fact]
    public async Task Delete_WithStaleEtag_ReturnsPreconditionFailedWithoutTombstone()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        Guid assetId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var asset = NewAsset(workspaceId, "etag.txt", MediaBlobState.Available);
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/workspaces/{workspaceId}/media/{assetId}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        Assert.False((await verification.MediaAssets.SingleAsync(asset => asset.Id == assetId)).IsDeleted);
        Assert.False(await verification.MediaDeletionIntents.AnyAsync(item => item.MediaAssetId == assetId));
    }

    [Fact]
    public async Task Reader_CanReadAssignedWorkspace_ButCannotWriteOrDiscoverAnotherWorkspace()
    {
        const string readerToken = "cmsify_media_reader_test_token";
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        Guid otherWorkspaceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var other = new Workspace { Name = "Other media", Slug = $"other-media-{Guid.NewGuid():N}" };
            db.Workspaces.Add(other);
            var adminUserId = await db.Users.Select(user => user.Id).FirstAsync();
            db.ApiClients.Add(new ApiClient
            {
                Name = $"Media reader {Guid.NewGuid():N}",
                TokenHash = BCrypt.Net.BCrypt.HashPassword(readerToken, 4),
                Role = UserRole.Reader,
                WorkspaceId = workspaceId,
                CreatedByUserId = adminUserId
            });
            await db.SaveChangesAsync();
            otherWorkspaceId = other.Id;
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readerToken);

        var assigned = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media?page=1&pageSize=10");
        using var upload = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes("forbidden"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(file, "file", "forbidden.txt");
        var write = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload);
        var isolated = await client.GetAsync($"/api/v1/workspaces/{otherWorkspaceId}/media?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, isolated.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(TestStorageProvider? storage = null)
    {
        var factory = new WebApplicationFactory<Program>();
        return storage is null ? factory : factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SyntaxCircus.Storage.IStorageProvider>();
            services.RemoveAll<IHostedService>();
            services.AddSingleton<SyntaxCircus.Storage.IStorageProvider>(storage);
        }));
    }

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
            StorageProvider = "local",
            BlobState = MediaBlobState.Available
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

    private static MediaAsset NewAsset(Guid workspaceId, string fileName, MediaBlobState state) => new()
    {
        WorkspaceId = workspaceId,
        FileName = fileName,
        MimeType = "text/plain",
        SizeBytes = 1,
        StorageKey = $"default/{fileName}",
        StorageProvider = "local",
        BlobState = state
    };

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

    private sealed class TestStorageProvider : SyntaxCircus.Storage.IStorageProvider
    {
        public bool FailStoreAfterCapture { get; init; }
        public Func<string, Task>? AfterStoreAsync { get; set; }
        public List<string> StoredKeys { get; } = [];
        public Dictionary<string, byte[]> ReadResults { get; } = [];
        public TrackingStream? LastReadStream { get; private set; }

        public async Task<StoredObject> StoreAsync(StoreObjectRequest request, CancellationToken cancellationToken = default)
        {
            StoredKeys.Add(request.Key);
            if (FailStoreAfterCapture) throw new IOException("simulated partial write");
            if (AfterStoreAsync is not null) await AfterStoreAsync(request.Key);
            return new StoredObject(request.Key, request.Content.CanSeek ? request.Content.Length : 0);
        }

        public Task<StorageReadResult?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            if (!ReadResults.TryGetValue(key, out var bytes)) return Task.FromResult<StorageReadResult?>(null);
            LastReadStream = new TrackingStream(bytes);
            return Task.FromResult<StorageReadResult?>(new StorageReadResult(LastReadStream, "text/plain", bytes.Length));
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadResults.ContainsKey(key));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadResults.TryGetValue(key, out var bytes)
                ? new StorageObjectMetadata(key, bytes.Length, "text/plain", DateTimeOffset.UtcNow)
                : null);

        public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageObjectPage([], null));
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
