using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SyntaxCircus.Cmsify.Client.Tests;

public sealed class CmsifyClientTests
{
    [Fact]
    public async Task GetAsync_AddsBearerAndCorrelationHeaders()
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, new { value = "ok" });
        }, "cmsify_test");

        var result = await client.GetAsync<JsonValue>("/api/v1/test", TestContext.Current.CancellationToken);

        result!.Value.ShouldBe("ok");
        captured!.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        captured.Headers.Authorization.Parameter.ShouldBe("cmsify_test");
        captured.Headers.Contains("X-Correlation-Id").ShouldBeTrue();
    }

    [Fact]
    public async Task GetAsync_PreservesOpaqueApiTokenFormat()
    {
        HttpRequestMessage? captured = null;
        var token = "cmsify_identifier_secret";
        var client = CreateClient(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, new { value = "ok" });
        }, token);

        await client.GetAsync<JsonValue>("/api/v1/test", TestContext.Current.CancellationToken);

        captured!.Headers.Authorization!.Parameter.ShouldBe(token);
    }

    [Theory]
    [InlineData("http://8.8.8.8/hooks")]
    [InlineData("https://localhost/hooks")]
    [InlineData("https://127.0.0.1/hooks")]
    [InlineData("https://10.0.0.1/hooks")]
    [InlineData("https://[::1]/hooks")]
    [InlineData("https://user:password@8.8.8.8/hooks")]
    public async Task WebhookMutations_RejectUnsafeUrlsBeforeSending(string url)
    {
        var requests = 0;
        var client = CreateClient(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var workspaceId = Guid.NewGuid();

        await Should.ThrowAsync<ArgumentException>(() => client.Webhooks.CreateAsync(workspaceId, new CreateWebhookEndpointRequest("Unsafe", url, null, ["content.published"]), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => client.Webhooks.UpdateAsync(workspaceId, Guid.NewGuid(), new UpdateWebhookEndpointRequest("Unsafe", url, true, ["content.published"]), TestContext.Current.CancellationToken));

        requests.ShouldBe(0);
    }

    [Fact]
    public async Task WebhookMutations_AllowPublicHttpsDestinations()
    {
        var requests = 0;
        var client = CreateClient(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await client.Webhooks.CreateAsync(Guid.NewGuid(), new CreateWebhookEndpointRequest("Public", "https://8.8.8.8/hooks", null, ["content.published"]), TestContext.Current.CancellationToken);

        requests.ShouldBe(1);
    }

    [Fact]
    public async Task ErrorResponse_MapsProblemDetails()
    {
        var client = CreateClient(_ => Json(HttpStatusCode.NotFound, new { type = "https://cmsify.dev/errors/not-found", title = "Not found", status = 404, traceId = "trace-1", detail = "Missing" }));

        var exception = await Should.ThrowAsync<CmsifyApiException>(() => client.GetAsync<object>("/missing", TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        exception.Problem.Detail.ShouldBe("Missing");
        exception.TraceId.ShouldBe("trace-1");
    }

    [Fact]
    public async Task RetryAfter_IsHonoredForRateLimits()
    {
        var attempts = 0;
        var client = CreateClient(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            }

            return Json(HttpStatusCode.OK, new { value = "ok" });
        });

        (await client.GetAsync<JsonValue>("/retry", TestContext.Current.CancellationToken))!.Value.ShouldBe("ok");
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task TokenProvider_IsUsedForEachRequest()
    {
        var tokenCalls = 0;
        var client = CreateClient(_ => Json(HttpStatusCode.OK, new { value = "ok" }), configure: options =>
        {
            options.TokenProvider = _ => ValueTask.FromResult<string?>("dynamic-" + ++tokenCalls);
        });

        await client.GetAsync<JsonValue>("/one", TestContext.Current.CancellationToken);
        await client.GetAsync<JsonValue>("/two", TestContext.Current.CancellationToken);
        tokenCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ResponseObserver_SeesEveryResponseBeforeDeserialization()
    {
        HttpStatusCode? observedStatus = null;
        var client = CreateClient(_ => Json(HttpStatusCode.OK, new { value = "ok" }), configure: options =>
        {
            options.ResponseObserver = (response, _) =>
            {
                observedStatus = response.StatusCode;
                return Task.CompletedTask;
            };
        });

        await client.GetAsync<JsonValue>("/observed", TestContext.Current.CancellationToken);

        observedStatus.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DownloadWithMetadataAsync_PreservesContentHeaders()
    {
        var client = CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-cmsify-package");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "starter.ctp" };
            return response;
        });

        var download = await client.DownloadWithMetadataAsync("/package", TestContext.Current.CancellationToken);

        download.FileName.ShouldBe("starter.ctp");
        download.ContentType.ShouldBe("application/x-cmsify-package");
        download.Content.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task PickListDelete_UsesDocumentedRoute()
    {
        string? route = null;
        var client = CreateClient(request =>
        {
            route = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var workspaceId = Guid.NewGuid();
        var pickListId = Guid.NewGuid();

        await client.PickLists.DeleteAsync(workspaceId, pickListId, TestContext.Current.CancellationToken);

        route.ShouldBe($"DELETE /api/v1/workspaces/{workspaceId}/picklists/{pickListId}");
    }

    [Fact]
    public async Task MediaUpload_ReportsSentBytes()
    {
        long reported = 0;
        var client = CreateClient(request =>
        {
            request.Content!.LoadIntoBufferAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, new { });
        });
        await using var stream = new MemoryStream([1, 2, 3, 4]);

        await client.Media.UploadAsync(Guid.NewGuid(), stream, "image.png", "image/png", progress: new CallbackProgress(value => reported = value), ct: TestContext.Current.CancellationToken);

        reported.ShouldBe(4);
    }

    [Fact]
    public async Task ListAllAsync_TraversesPages()
    {
        var page = 0;
        var item = new { id = Guid.NewGuid(), templateVersionId = Guid.NewGuid(), templateName = "blog", status = "Published", slug = "post", localeCode = (string?)null, translationGroupId = (Guid?)null, tags = Array.Empty<string>(), createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow, publishedAt = (DateTimeOffset?)null };
        var client = CreateClient(_ => Json(HttpStatusCode.OK, new PagedResponse<object>([item], 2, ++page, 1)));

        var values = new List<ContentItemSummaryResponse>();
        await foreach (var value in client.Content.ListAllAsync(Guid.NewGuid(), new ContentListQuery(null, null, null, null, null, null, null, null, null, null, null, null, false, null, "createdAt", true, 1, 1), ct: TestContext.Current.CancellationToken).WithCancellation(TestContext.Current.CancellationToken))
        {
            values.Add(value);
        }

        values.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ComponentListAllAsync_TraversesEveryPage()
    {
        var requests = new List<string>();
        var workspaceId = Guid.NewGuid();
        var component = new { id = Guid.NewGuid(), name = "Hero", slug = "hero", description = (string?)null, currentVersionId = (Guid?)null };
        var client = CreateClient(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            var page = requests.Count;
            return Json(HttpStatusCode.OK, new PagedResponse<object>([component], 101, page, 100));
        });

        var components = await client.Components.ListAllAsync(workspaceId, TestContext.Current.CancellationToken);

        components.Count.ShouldBe(2);
        requests.ShouldBe([
            $"/api/v1/workspaces/{workspaceId}/components?page=1&pageSize=100",
            $"/api/v1/workspaces/{workspaceId}/components?page=2&pageSize=100"
        ]);
    }

    [Fact]
    public async Task ComponentListAsync_UsesThePagedPublicEnvelope()
    {
        string? route = null;
        var workspaceId = Guid.NewGuid();
        var client = CreateClient(request =>
        {
            route = request.RequestUri!.PathAndQuery;
            return Json(HttpStatusCode.OK, new PagedResponse<object>([], 11, 2, 10));
        });

        var response = await client.Components.ListAsync(workspaceId, page: 2, pageSize: 10, ct: TestContext.Current.CancellationToken);

        response!.Page.ShouldBe(2);
        response.PageSize.ShouldBe(10);
        response.TotalCount.ShouldBe(11);
        response.TotalPages.ShouldBe(2);
        route.ShouldBe($"/api/v1/workspaces/{workspaceId}/components?page=2&pageSize=10");
    }

    [Fact]
    public async Task ComponentListAllAsync_HonorsCancellationBeforeLoadingAPage()
    {
        var requests = 0;
        var client = CreateClient(_ =>
        {
            requests++;
            return Json(HttpStatusCode.OK, new PagedResponse<object>([], 0, 1, 100));
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => client.Components.ListAllAsync(Guid.NewGuid(), cancellation.Token));

        requests.ShouldBe(0);
    }

    [Fact]
    public async Task ContentReadThenUpdate_UsesTheTrackedEtag()
    {
        string? ifMatch = null;
        var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = Json(HttpStatusCode.OK, new { });
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return response;
            }

            ifMatch = request.Headers.IfMatch.ToString();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        await client.Content.GetAsync(workspaceId, contentId, ct: TestContext.Current.CancellationToken);
        await client.Content.UpdateAsync(workspaceId, contentId, new UpdateContentItemRequest(null, null, null, null, [], []), TestContext.Current.CancellationToken);

        ifMatch.ShouldBe("\"v1\"");
    }

    [Fact]
    public async Task DownloadFailure_PreservesApiExceptionAndCorrelationId()
    {
        HttpRequestMessage? captured = null;
        var client = CreateClient(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var exception = await Should.ThrowAsync<CmsifyApiException>(() => client.DownloadAsync("/missing", TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        exception.CorrelationId.ShouldBe(captured!.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task PostRequests_AreNotRetriedWithoutAnIdempotencyKey()
    {
        var calls = 0;
        var client = CreateClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        await Should.ThrowAsync<CmsifyApiException>(() => client.PostAsync<object>("/create", cancellationToken: TestContext.Current.CancellationToken));

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task AddedFacadeOperations_UseTheirDocumentedRoutes()
    {
        var routes = new List<string>();
        var client = CreateClient(request =>
        {
            routes.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var workspaceId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var version = 2;

        await client.Content.UpgradeVersionAsync(workspaceId, resourceId, TestContext.Current.CancellationToken);
        await client.Content.LinkTranslationAsync(workspaceId, resourceId, new LinkTranslationRequest(Guid.NewGuid()), TestContext.Current.CancellationToken);
        await client.Content.GetVersionAsync(workspaceId, resourceId, version, TestContext.Current.CancellationToken);
        await client.Content.RollbackVersionAsync(workspaceId, resourceId, version, TestContext.Current.CancellationToken);
        await client.Templates.DeleteSectionAsync(workspaceId, resourceId, version, Guid.NewGuid(), TestContext.Current.CancellationToken);
        await client.Templates.DeleteFieldAsync(workspaceId, resourceId, version, Guid.NewGuid(), TestContext.Current.CancellationToken);
        await client.Templates.ReorderFieldsAsync(workspaceId, resourceId, version, [], TestContext.Current.CancellationToken);
        await client.Components.GetVersionAsync(workspaceId, resourceId, version, TestContext.Current.CancellationToken);
        await client.Webhooks.DeliveriesAsync(workspaceId, resourceId, isDelivered: true, isFailed: false, ct: TestContext.Current.CancellationToken);
        await client.Webhooks.RetryDeliveryAsync(workspaceId, resourceId, Guid.NewGuid(), TestContext.Current.CancellationToken);
        await client.Packages.ImportAsync(workspaceId, new { cmsifyPackage = "1.0" }, TestContext.Current.CancellationToken);
        await client.Packages.PreviewAsync(workspaceId, new { cmsifyPackage = "1.0" }, TestContext.Current.CancellationToken);
        await client.Packages.ExportAsync(workspaceId, [Guid.NewGuid()], ct: TestContext.Current.CancellationToken);

        routes.ShouldContain(route => route.Contains($"POST /api/v1/workspaces/{workspaceId}/content/{resourceId}/upgrade-version"));
        routes.ShouldContain(route => route.Contains($"GET /api/v1/workspaces/{workspaceId}/components/{resourceId}/versions/{version}"));
        routes.ShouldContain(route => route.Contains($"POST /api/v1/workspaces/{workspaceId}/webhooks/{resourceId}/deliveries/"));
        routes.ShouldContain(route => route.StartsWith($"GET /api/v1/workspaces/{workspaceId}/packages/export?templateIds="));
    }

    private static CmsifyClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler, string? token = null, Action<CmsifyClientOptions>? configure = null)
    {
        var options = new CmsifyClientOptions { BaseUrl = new Uri("https://cms.test"), ApiToken = token, EnableRetries = true };
        configure?.Invoke(options);
        return new CmsifyClient(new HttpClient(new StubHandler(handler)), options);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
        return response;
    }

    private sealed record JsonValue(string Value);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    private sealed class CallbackProgress(Action<long> callback) : IProgress<long>
    {
        public void Report(long value) => callback(value);
    }
}
