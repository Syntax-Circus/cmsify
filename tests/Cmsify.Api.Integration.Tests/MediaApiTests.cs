using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cmsify.Api.Controllers;
using Cmsify.Core.Domain.Entities;
using Cmsify.Core.Domain.Enums;
using Cmsify.Core.Interfaces.Services;
using Cmsify.Infrastructure.BackgroundServices;
using Cmsify.Infrastructure.Persistence;
using Cmsify.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using SyntaxCircus.Storage;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class MediaApiTests : IAsyncLifetime
{
    private const string ApiToken = "cmsify_media_api_test_token";
    private const int ReconciliationRaceIterations = 20;
    private static readonly TimeSpan ReconciliationRaceWindow = TimeSpan.FromSeconds(2);

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    private string storagePath = string.Empty;

    public async ValueTask InitializeAsync()
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

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        if (Directory.Exists(storagePath))
        {
            Directory.Delete(storagePath, recursive: true);
        }

        ClearEnvironment();
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task ConfiguredUploadLimit_RejectsOneByteOverWithoutSideEffects()
    {
        const string fileName = "one-byte-over.txt";
        var storage = new TestStorageProvider();
        await using var factory = CreateFactory(storage);
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        using var upload = new MultipartFormDataContent();
        using var file = new ByteArrayContent(new byte[(1024 * 1024) + 1]);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(file, "file", fileName);

        using var response = await client.PostAsync(
            $"/api/v1/workspaces/{workspaceId}/media",
            upload,
            TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("https://cmsify.dev/errors/bad-request", problem.GetProperty("type").GetString());
        Assert.Equal("File is too large", problem.GetProperty("title").GetString());
        Assert.Equal((int)HttpStatusCode.RequestEntityTooLarge, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        var committedObjects = await storage.ListAsync(
            new ListStorageObjectsRequest(string.Empty, null, 100),
            TestContext.Current.CancellationToken);
        Assert.Empty(committedObjects.Items);
        Assert.Empty(storage.StoredKeys);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        Assert.False(await db.MediaAssets.IgnoreQueryFilters().AnyAsync(
            asset => asset.FileName == fileName,
            TestContext.Current.CancellationToken));
        Assert.False(await db.MediaDeletionIntents.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task TestStorageProvider_TracksSuccessfulStoreExistenceListingAndDelete()
    {
        const string key = "cmsify/media/stateful.bin";
        var storage = new TestStorageProvider();
        await using var content = new MemoryStream([1, 2, 3]);

        var stored = await storage.StoreAsync(
            new StoreObjectRequest(key, content, "application/octet-stream"),
            TestContext.Current.CancellationToken);
        var existsAfterStore = await storage.ExistsAsync(key, TestContext.Current.CancellationToken);
        var afterStore = await storage.ListAsync(
            new ListStorageObjectsRequest("cmsify/media/", null, 100),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, stored.SizeBytes);
        Assert.True(existsAfterStore);
        var listed = Assert.Single(afterStore.Items);
        Assert.Equal(key, listed.Key);
        Assert.Equal(3, listed.SizeBytes);
        Assert.Equal("application/octet-stream", listed.ContentType);

        await storage.DeleteAsync(key, TestContext.Current.CancellationToken);

        Assert.False(await storage.ExistsAsync(key, TestContext.Current.CancellationToken));
        Assert.Empty((await storage.ListAsync(
            new ListStorageObjectsRequest("cmsify/media/", null, 100),
            TestContext.Current.CancellationToken)).Items);
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public void DefaultUploadLimit_IsFiftyMebibytes()
    {
        var apiAssemblyDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)
            ?? throw new InvalidOperationException("Cmsify API assembly location is unavailable.");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiAssemblyDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        Assert.Equal(50, configuration.GetValue<int>("Media:MaxFileSizeMb"));
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

        var createResponse = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload, TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        var assetId = created.GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var persisted = await db.MediaAssets.SingleAsync(asset => asset.Id == assetId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(MediaBlobState.Available, persisted.BlobState);
            Assert.StartsWith($"cmsify/media/{workspaceId}/", persisted.StorageKey, StringComparison.Ordinal);
            Assert.Contains($"/{assetId}_hello.txt", persisted.StorageKey, StringComparison.Ordinal);
        }

        var fileResponse = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{assetId}/file", TestContext.Current.CancellationToken);
        fileResponse.EnsureSuccessStatusCode();
        Assert.Equal("text/plain", fileResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("hello media", await fileResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var getResponse = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{assetId}", TestContext.Current.CancellationToken);
        getResponse.EnsureSuccessStatusCode();
        var etag = getResponse.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/workspaces/{workspaceId}/media/{assetId}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        var deleteResponse = await client.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var deleted = await db.MediaAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId, cancellationToken: TestContext.Current.CancellationToken);
            var intent = await db.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == assetId, cancellationToken: TestContext.Current.CancellationToken);
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
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{workspaceId}/media?page=1&pageSize=100", cancellationToken: TestContext.Current.CancellationToken);
        var listedIds = list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.DoesNotContain(listedIds, id => ids.Contains(id));
        foreach (var id in ids)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{id}", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{id}/file", TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task AvailableAssetWithMissingBlob_ReturnsSanitizedProblemDetails()
    {
        var outcomes = await RunReconciliationIsolatedIterationsAsync(async (factory, iteration) =>
        {
            using var client = factory.CreateClient();
            var workspaceId = await SeedApiClientAsync(factory);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            Guid assetId;
            string storageKey;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
                var asset = NewAsset(workspaceId, $"missing-{iteration}.txt", MediaBlobState.Available);
                db.MediaAssets.Add(asset);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                assetId = asset.Id;
                storageKey = asset.StorageKey;
            }

            await Task.Delay(ReconciliationRaceWindow, TestContext.Current.CancellationToken);
            await using var verificationScope = factory.Services.CreateAsyncScope();
            var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var blobState = (await verification.MediaAssets.IgnoreQueryFilters().SingleAsync(
                asset => asset.Id == assetId,
                TestContext.Current.CancellationToken)).BlobState;
            using var response = await client.GetAsync(
                $"/api/v1/workspaces/{workspaceId}/media/{assetId}/file",
                TestContext.Current.CancellationToken);
            var problemBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var problem = JsonDocument.Parse(problemBody);
            return (
                BlobState: blobState,
                StatusCode: response.StatusCode,
                ProblemType: problem.RootElement.GetProperty("type").GetString(),
                ProblemBody: problemBody,
                StorageKey: storageKey);
        });

        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(MediaBlobState.Available, outcome.BlobState);
            // Regression guard: returning a generic 404 instead of media-blob-missing for a missing storage blob must fail this assertion.
            Assert.Equal(HttpStatusCode.NotFound, outcome.StatusCode);
            Assert.EndsWith("/media-blob-missing", outcome.ProblemType, StringComparison.Ordinal);
            Assert.DoesNotContain(outcome.StorageKey, outcome.ProblemBody, StringComparison.Ordinal);
        });
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

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var failed = await db.MediaAssets.SingleAsync(asset => asset.FileName == "partial.txt", cancellationToken: TestContext.Current.CancellationToken);
        var intent = await db.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == failed.Id, cancellationToken: TestContext.Current.CancellationToken);
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

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload, TestContext.Current.CancellationToken);

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

        var response = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var failed = await db.MediaAssets.SingleAsync(asset => asset.FileName == "concurrent.txt", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(MediaBlobState.UploadFailed, failed.BlobState);
        Assert.Equal("upload_failed", (await db.MediaDeletionIntents.SingleAsync(item => item.MediaAssetId == failed.Id, cancellationToken: TestContext.Current.CancellationToken)).Reason);
        Assert.Equal(failed.StorageKey, Assert.Single(storage.StoredKeys));
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task FileResponse_StreamsStorageObjectIncrementallyAndDisposesIt()
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
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            assetId = asset.Id;
        }
        var storedContent = new GuardedStorageReadStream(
            length: (512 * 1024) + 29,
            maximumReadRequest: 128 * 1024);
        storage.StreamingReadResults["default/disposal.txt"] = storedContent;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/workspaces/{workspaceId}/media/{assetId}/file");
        using (var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var destination = new GuardedStorageWriteStream(128 * 1024);
            await response.Content.CopyToAsync(destination, TestContext.Current.CancellationToken);
            Assert.Equal(storedContent.Length, destination.BytesWritten);
            Assert.True(destination.WriteOperationCount > 1);
            Assert.True(destination.MaximumObservedWriteRequest <= 128 * 1024);
        }

        Assert.Equal(storedContent.Length, storedContent.BytesRead);
        Assert.True(storedContent.ReadOperationCount > 1);
        Assert.True(storedContent.MaximumObservedReadRequest <= 128 * 1024);
        Assert.True(storage.LastReadStream!.WasDisposed);
        Assert.True(storedContent.WasDisposed);
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task GetFile_ReturnsExactStorageStreamWithoutBuffering()
    {
        var storage = new TestStorageProvider();
        await using var factory = CreateFactory(storage);
        var workspaceId = await SeedApiClientAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
        var asset = NewAsset(workspaceId, "passthrough.txt", MediaBlobState.Available);
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var storedContent = new GuardedStorageReadStream(
            length: (512 * 1024) + 31,
            maximumReadRequest: 128 * 1024);
        storage.StreamingReadResults[asset.StorageKey] = storedContent;
        var authorization = Substitute.For<IWorkspaceAuthorizationService>();
        authorization.CanReadWorkspaceAsync(workspaceId, Arg.Any<CancellationToken>()).Returns(true);
        var controller = new MediaController(
            db,
            storage,
            Substitute.For<ICurrentActor>(),
            authorization,
            scope.ServiceProvider.GetRequiredService<IConfiguration>(),
            scope.ServiceProvider.GetRequiredService<IOptions<MediaOperationalOptions>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.GetFile(workspaceId, asset.Id, TestContext.Current.CancellationToken);
        var fileResult = Assert.IsType<FileStreamResult>(result);
        await using var returnedStream = fileResult.FileStream;

        Assert.Same(storage.LastReadStream, returnedStream);
        Assert.Equal(0, storedContent.BytesRead);
        Assert.False(storage.LastReadStream!.WasDisposed);
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenAssetIsReferencedByContent()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var workspaceId = await SeedApiClientAsync(factory);
        var assetId = await SeedReferencedMediaAsync(factory, workspaceId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);

        var getResponse = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media/{assetId}", TestContext.Current.CancellationToken);
        getResponse.EnsureSuccessStatusCode();
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/workspaces/{workspaceId}/media/{assetId}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", getResponse.Headers.ETag?.ToString());
        var deleteResponse = await client.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_WithStaleEtag_ReturnsPreconditionFailedWithoutTombstone()
    {
        var outcomes = await RunReconciliationIsolatedIterationsAsync(async (factory, iteration) =>
        {
            using var client = factory.CreateClient();
            var workspaceId = await SeedApiClientAsync(factory);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            Guid assetId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
                var asset = NewAsset(workspaceId, $"etag-{iteration}.txt", MediaBlobState.Available);
                db.MediaAssets.Add(asset);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                assetId = asset.Id;
            }
            await Task.Delay(ReconciliationRaceWindow, TestContext.Current.CancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/workspaces/{workspaceId}/media/{assetId}");
            request.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var problemBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var problem = JsonDocument.Parse(problemBody);

            await using var verificationScope = factory.Services.CreateAsyncScope();
            var verification = verificationScope.ServiceProvider.GetRequiredService<CmsifyDbContext>();
            var persisted = await verification.MediaAssets.IgnoreQueryFilters().SingleAsync(
                asset => asset.Id == assetId,
                TestContext.Current.CancellationToken);
            return (
                BlobState: persisted.BlobState,
                StatusCode: response.StatusCode,
                ProblemType: problem.RootElement.GetProperty("type").GetString(),
                IsDeleted: persisted.IsDeleted,
                HasDeletionIntent: await verification.MediaDeletionIntents.AnyAsync(
                    item => item.MediaAssetId == assetId,
                    TestContext.Current.CancellationToken));
        });

        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(MediaBlobState.Available, outcome.BlobState);
            // Regression guard: performing deletion before validating If-Match must fail this assertion.
            Assert.Equal(HttpStatusCode.PreconditionFailed, outcome.StatusCode);
            Assert.EndsWith("/concurrency-mismatch", outcome.ProblemType, StringComparison.Ordinal);
            Assert.False(outcome.IsDeleted);
            Assert.False(outcome.HasDeletionIntent);
        });
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
            var adminUserId = await db.Users.Select(user => user.Id).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            db.ApiClients.Add(new ApiClient
            {
                Name = $"Media reader {Guid.NewGuid():N}",
                TokenHash = BCrypt.Net.BCrypt.HashPassword(readerToken, 4),
                Role = UserRole.Reader,
                WorkspaceId = workspaceId,
                CreatedByUserId = adminUserId
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            otherWorkspaceId = other.Id;
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readerToken);

        var assigned = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/media?page=1&pageSize=10", TestContext.Current.CancellationToken);
        using var upload = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes("forbidden"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(file, "file", "forbidden.txt");
        var write = await client.PostAsync($"/api/v1/workspaces/{workspaceId}/media", upload, TestContext.Current.CancellationToken);
        var isolated = await client.GetAsync($"/api/v1/workspaces/{otherWorkspaceId}/media?page=1&pageSize=10", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, isolated.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(
        TestStorageProvider? storage = null,
        bool enableMediaReconciliation = true)
    {
        var factory = new WebApplicationFactory<Program>();
        if (!enableMediaReconciliation)
        {
            factory = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Media:Operations:ReconciliationIntervalSeconds", "1");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMediaReconciliationRepository>();
                    services.AddScoped<IMediaReconciliationRepository, NoOpMediaReconciliationRepository>();
                });
            });
        }

        return storage is null ? factory : factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SyntaxCircus.Storage.IStorageProvider>();
            services.RemoveAll<IHostedService>();
            services.AddSingleton<SyntaxCircus.Storage.IStorageProvider>(storage);
        }));
    }

    private static void AssertUnrelatedHostedServicesRemainRegistered(WebApplicationFactory<Program> factory)
    {
        var hostedServices = factory.Services.GetServices<IHostedService>();
        Assert.Contains(hostedServices, service => service is ScheduledPublishingService);
        Assert.Contains(hostedServices, service => service is WebhookDispatchService);
    }

    private async Task<IReadOnlyList<T>> RunReconciliationIsolatedIterationsAsync<T>(
        Func<WebApplicationFactory<Program>, int, Task<T>> runIteration)
    {
        var outcomes = new List<T>(ReconciliationRaceIterations);
        for (var iteration = 0; iteration < ReconciliationRaceIterations; iteration++)
        {
            await using var factory = CreateFactory(enableMediaReconciliation: false);
            AssertUnrelatedHostedServicesRemainRegistered(factory);
            outcomes.Add(await runIteration(factory, iteration));
        }

        return outcomes;
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

    private sealed class NoOpMediaReconciliationRepository : IMediaReconciliationRepository
    {
        public Task<IReadOnlyList<MediaDeletionClaim>> ClaimDeletionIntentsAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MediaDeletionClaim>>([]);

        public Task<DeletionPreparationResult> PrepareDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default) =>
            Task.FromResult(DeletionPreparationResult.ClaimLost);

        public Task<bool> CompleteDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> RetryDeletionAsync(MediaDeletionClaim claim, DateTimeOffset now, DateTimeOffset nextAttemptAt, string error, CancellationToken ct = default) => Task.FromResult(false);

        public Task<int> FailStaleUploadsAsync(DateTimeOffset cutoff, DateTimeOffset now, int limit, CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<MediaVerificationCandidate>> GetVerificationBatchAsync(string provider, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MediaVerificationCandidate>>([]);

        public Task RecordBlobMissingAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordBlobPresentAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> StorageKeyExistsAsync(string provider, string storageKey, CancellationToken ct = default) => Task.FromResult(false);

        public Task EnqueueOrphanDeletionAsync(string provider, string storageKey, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;

        public Task<MediaCheckpointClaim?> ClaimCheckpointAsync(string provider, string prefix, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default) =>
            Task.FromResult<MediaCheckpointClaim?>(null);

        public Task<bool> CompleteCheckpointAsync(MediaCheckpointClaim claim, string? nextAfterKey, bool completedPrefix, DateTimeOffset now, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class TestStorageProvider : SyntaxCircus.Storage.IStorageProvider
    {
        private readonly Dictionary<string, StorageObjectMetadata> committedObjects = new(StringComparer.Ordinal);

        public bool FailStoreAfterCapture { get; init; }
        public Func<string, Task>? AfterStoreAsync { get; set; }
        public List<string> StoredKeys { get; } = [];
        public Dictionary<string, byte[]> ReadResults { get; } = [];
        public Dictionary<string, Stream> StreamingReadResults { get; } = [];
        public TrackingStream? LastReadStream { get; private set; }

        public async Task<StoredObject> StoreAsync(StoreObjectRequest request, CancellationToken cancellationToken = default)
        {
            StoredKeys.Add(request.Key);
            if (FailStoreAfterCapture) throw new IOException("simulated partial write");
            var sizeBytes = request.Content.CanSeek ? request.Content.Length : 0;
            committedObjects[request.Key] = new StorageObjectMetadata(
                request.Key,
                sizeBytes,
                request.ContentType,
                DateTimeOffset.UtcNow);
            if (AfterStoreAsync is not null) await AfterStoreAsync(request.Key);
            return new StoredObject(request.Key, sizeBytes);
        }

        public Task<StorageReadResult?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            if (StreamingReadResults.TryGetValue(key, out var stream))
            {
                LastReadStream = new TrackingStream(stream);
                return Task.FromResult<StorageReadResult?>(
                    new StorageReadResult(LastReadStream, "text/plain", stream.Length));
            }

            if (!ReadResults.TryGetValue(key, out var bytes)) return Task.FromResult<StorageReadResult?>(null);
            LastReadStream = new TrackingStream(new MemoryStream(bytes));
            return Task.FromResult<StorageReadResult?>(new StorageReadResult(LastReadStream, "text/plain", bytes.Length));
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                committedObjects.ContainsKey(key)
                || ReadResults.ContainsKey(key)
                || StreamingReadResults.ContainsKey(key));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            committedObjects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<StorageObjectMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(committedObjects.TryGetValue(key, out var committed)
                ? committed
                : ReadResults.TryGetValue(key, out var bytes)
                    ? new StorageObjectMetadata(key, bytes.Length, "text/plain", DateTimeOffset.UtcNow)
                    : StreamingReadResults.TryGetValue(key, out var stream)
                        ? new StorageObjectMetadata(key, stream.Length, "text/plain", DateTimeOffset.UtcNow)
                    : null);

        public Task<StorageObjectPage> ListAsync(ListStorageObjectsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ListObjects(request));

        private StorageObjectPage ListObjects(ListStorageObjectsRequest request)
        {
            var matches = committedObjects.Values
                .Where(item => item.Key.StartsWith(request.Prefix, StringComparison.Ordinal))
                .Where(item => request.AfterKey is null || string.CompareOrdinal(item.Key, request.AfterKey) > 0)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray();
            var items = matches.Take(request.PageSize).ToArray();
            var nextAfterKey = matches.Length > items.Length && items.Length > 0 ? items[^1].Key : null;
            return new StorageObjectPage(items, nextAfterKey);
        }
    }

    private sealed class TrackingStream(Stream inner) : Stream
    {
        public bool WasDisposed { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            WasDisposed = true;
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class GuardedStorageReadStream(long length, int maximumReadRequest) : Stream
    {
        private long position;

        public long BytesRead => position;
        public int ReadOperationCount { get; private set; }
        public int MaximumObservedReadRequest { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        private int ReadCore(Span<byte> buffer)
        {
            if (buffer.Length > maximumReadRequest)
            {
                throw new IOException(
                    $"Read request of {buffer.Length} bytes exceeded the {maximumReadRequest}-byte ceiling.");
            }

            MaximumObservedReadRequest = Math.Max(MaximumObservedReadRequest, buffer.Length);
            ReadOperationCount++;
            var count = (int)Math.Min(buffer.Length, length - position);
            buffer[..count].Fill(0x5a);
            position += count;
            return count;
        }
    }

    private sealed class GuardedStorageWriteStream(int maximumWriteRequest) : Stream
    {
        public long BytesWritten { get; private set; }
        public int WriteOperationCount { get; private set; }
        public int MaximumObservedWriteRequest { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => ObserveWrite(count);
        public override void Write(ReadOnlySpan<byte> buffer) => ObserveWrite(buffer.Length);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveWrite(buffer.Length);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush()
        {
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void ObserveWrite(int count)
        {
            if (count > maximumWriteRequest)
            {
                throw new IOException(
                    $"Write of {count} bytes exceeded the {maximumWriteRequest}-byte ceiling.");
            }

            BytesWritten += count;
            WriteOperationCount++;
            MaximumObservedWriteRequest = Math.Max(MaximumObservedWriteRequest, count);
        }
    }
}
