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

        var result = await client.GetAsync<JsonValue>("/api/v1/test");

        result!.Value.ShouldBe("ok");
        captured!.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        captured.Headers.Authorization.Parameter.ShouldBe("cmsify_test");
        captured.Headers.Contains("X-Correlation-Id").ShouldBeTrue();
    }

    [Fact]
    public async Task ErrorResponse_MapsProblemDetails()
    {
        var client = CreateClient(_ => Json(HttpStatusCode.NotFound, new { type = "https://cmsify.dev/errors/not-found", title = "Not found", status = 404, traceId = "trace-1", detail = "Missing" }));

        var exception = await Should.ThrowAsync<CmsifyApiException>(() => client.GetAsync<object>("/missing"));

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

        (await client.GetAsync<JsonValue>("/retry"))!.Value.ShouldBe("ok");
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

        await client.GetAsync<JsonValue>("/one");
        await client.GetAsync<JsonValue>("/two");
        tokenCalls.ShouldBe(2);
    }

    [Fact]
    public async Task ListAllAsync_TraversesPages()
    {
        var page = 0;
        var item = new { id = Guid.NewGuid(), templateVersionId = Guid.NewGuid(), templateName = "blog", status = "Published", slug = "post", localeCode = (string?)null, translationGroupId = (Guid?)null, tags = Array.Empty<string>(), createdAt = DateTimeOffset.UtcNow, updatedAt = DateTimeOffset.UtcNow, publishedAt = (DateTimeOffset?)null };
        var client = CreateClient(_ => Json(HttpStatusCode.OK, new PagedResponse<object>([item], 2, ++page, 1)));

        var values = new List<ContentItemSummaryResponse>();
        await foreach (var value in client.Content.ListAllAsync(Guid.NewGuid(), new ContentListQuery(null, null, null, null, null, null, null, null, null, null, null, null, false, null, "createdAt", true, 1, 1)))
        {
            values.Add(value);
        }

        values.Count.ShouldBe(2);
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
}
