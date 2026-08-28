using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SyntaxCircus.Http.Resilience;

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
    public async Task DirectClient_RebuildsTokenAndCorrelationForEverySafeRetry()
    {
        var tokens = new Queue<string>(["first", "second"]);
        var observed = new List<(string Token, string Correlation, string Accept)>();
        var client = CreateResilientClient(request =>
        {
            observed.Add((
                request.Headers.Authorization!.Parameter!,
                request.Headers.GetValues("X-Correlation-Id").Single(),
                request.Headers.Accept.Single().MediaType!));
            return observed.Count == 1
                ? Response(HttpStatusCode.ServiceUnavailable)
                : Json(HttpStatusCode.OK, new { value = "ok" });
        }, options => options.TokenProvider = _ => ValueTask.FromResult<string?>(tokens.Dequeue()));

        var result = await client.GetAsync<JsonValue>("/api/v1/test", TestContext.Current.CancellationToken);

        result!.Value.ShouldBe("ok");
        observed.Select(value => value.Token).ShouldBe(["first", "second"]);
        observed.Select(value => value.Correlation).Distinct().Count().ShouldBe(2);
        observed.Select(value => value.Accept).ShouldBe(["application/json", "application/json"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectClient_HonorsDeltaAndDateRetryAfter(bool useDate)
    {
        var attempts = 0;
        TimeSpan? observedDelay = null;
        var expectedDelta = TimeSpan.FromMilliseconds(25);
        var expectedDateDelay = TimeSpan.FromMilliseconds(250);
        var currentTime = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedUtcTimeProvider(currentTime);
        var client = CreateResilientClient(
            request =>
            {
                attempts++;
                if (attempts == 1)
                {
                    var response = Response(HttpStatusCode.ServiceUnavailable);
                    response.Headers.RetryAfter = useDate
                        ? new RetryConditionHeaderValue(currentTime.Add(expectedDateDelay))
                        : new RetryConditionHeaderValue(expectedDelta);
                    return response;
                }

                return Json(HttpStatusCode.OK, new { value = "ok" });
            },
            options =>
            {
                options.MaxRetryAttempts = 2;
                options.RequestTimeout = TimeSpan.FromSeconds(2);
            },
            options => TestPipelineOptions(
                options,
                maximumDelay: TimeSpan.FromSeconds(1),
                onRetry: telemetry => observedDelay = telemetry.Delay,
                timeProvider: timeProvider));

        (await client.GetAsync<JsonValue>("/retry-after", TestContext.Current.CancellationToken))!.Value.ShouldBe("ok");

        attempts.ShouldBe(2);
        observedDelay.ShouldNotBeNull();
        observedDelay.Value.ShouldBe(useDate ? expectedDateDelay : expectedDelta);
    }

    [Fact]
    public async Task DirectClient_RetriesTransportFailureAndReturnsSuccess()
    {
        var attempts = 0;
        var client = CreateResilientClient((_, _) =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("transport-1"))
                : Task.FromResult(Json(HttpStatusCode.OK, new { value = "ok" }));
        });

        (await client.GetAsync<JsonValue>("/transport", TestContext.Current.CancellationToken))!.Value.ShouldBe("ok");

        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task DirectClient_TotalBudgetTimesOutAnInFlightAttempt()
    {
        var attempts = 0;
        var timeout = TimeSpan.FromMilliseconds(50);
        var client = CreateResilientClient(
            async (_, attemptToken) =>
            {
                attempts++;
                await Task.Delay(Timeout.InfiniteTimeSpan, attemptToken);
                return Response(HttpStatusCode.OK);
            },
            options =>
            {
                options.MaxRetryAttempts = 3;
                options.RequestTimeout = timeout;
            });

        var exception = await Should.ThrowAsync<HttpRequestTimeoutException>(() => client.GetAsync<JsonValue>("/budget", TestContext.Current.CancellationToken));

        exception.Timeout.ShouldBe(timeout);
        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ClientOptions_ForwardTimeoutCallbackAndCallbackFailureDoesNotReplaceTimeout()
    {
        var timeout = TimeSpan.FromMilliseconds(50);
        var events = new List<HttpTimeoutTelemetry>();
        var options = new CmsifyClientOptions
        {
            BaseUrl = new Uri("https://cms.test"),
            MaxRetryAttempts = 1,
            RequestTimeout = timeout,
            OnTimeout = (telemetry, _) =>
            {
                events.Add(telemetry);
                return ValueTask.FromException(new InvalidOperationException("telemetry"));
            },
        };
        var client = new CmsifyClient(new HttpClient(new AsyncStubHandler(async (_, attemptToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, attemptToken);
            return Response(HttpStatusCode.OK);
        })), options);

        var exception = await Should.ThrowAsync<HttpRequestTimeoutException>(() =>
            client.GetAsync<JsonValue>("/timeout-telemetry", TestContext.Current.CancellationToken));

        exception.Timeout.ShouldBe(timeout);
        events.ShouldBe([
            new HttpTimeoutTelemetry(
                "CmsifyClient",
                HttpResilienceFailureCategory.Timeout,
                timeout),
        ]);
    }

    [Fact]
    public async Task DirectClient_CallerCancellationIsNotRetriedAndKeepsCallerToken()
    {
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        var client = CreateResilientClient((_, attemptToken) =>
        {
            attempts++;
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(attemptToken);
        });

        var exception = await Should.ThrowAsync<OperationCanceledException>(() => client.GetAsync<JsonValue>("/cancel", cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task DirectClient_ExhaustedTransportRetriesSurfaceFinalFailure()
    {
        var attempts = 0;
        var client = CreateResilientClient((_, _) =>
        {
            attempts++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException($"transport-{attempts}"));
        }, options => options.MaxRetryAttempts = 3);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync<JsonValue>("/transport", TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("transport-3");
        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task DirectClient_DisabledRetriesStillUsesOneBudgetedAttempt()
    {
        var attempts = 0;
        var client = CreateResilientClient(_ =>
        {
            attempts++;
            return Response(HttpStatusCode.ServiceUnavailable);
        },
        options => options.EnableRetries = false,
        options => new HttpRequestResilienceOptions
        {
            MaxAttempts = 3,
            TotalRequestTimeout = options.RequestTimeout,
            BackoffBaseDelay = TimeSpan.FromMilliseconds(1),
            MaximumDelay = TimeSpan.FromMilliseconds(1),
            JitterProvider = () => 0,
        });

        await Should.ThrowAsync<CmsifyApiException>(() => client.GetAsync<JsonValue>("/disabled", TestContext.Current.CancellationToken));

        attempts.ShouldBe(1);
    }

    [Fact]
    public void PipelineAwareConstructor_SetsHttpClientTimeoutToInfinite()
    {
        var options = new CmsifyClientOptions
        {
            BaseUrl = new Uri("https://cms.test"),
            RequestTimeout = TimeSpan.FromSeconds(2),
        };
        var httpClient = new HttpClient(new StubHandler(_ => Response(HttpStatusCode.OK)));
        var pipeline = new HttpRequestResiliencePipeline("cmsify-test", TestPipelineOptions(options));

        _ = new CmsifyClient(httpClient, options, pipeline);

        httpClient.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task InfiniteRequestTimeout_ConstructsAndSendsWithoutDeadline()
    {
        var sends = 0;
        var attemptToken = default(CancellationToken);
        var callerToken = TestContext.Current.CancellationToken;
        var options = new CmsifyClientOptions
        {
            BaseUrl = new Uri("https://cms.test"),
            MaxRetryAttempts = 1,
            RequestTimeout = Timeout.InfiniteTimeSpan,
            TokenProvider = cancellationToken =>
            {
                attemptToken = cancellationToken;
                return ValueTask.FromResult<string?>(null);
            },
        };
        var httpClient = new HttpClient(new AsyncStubHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(Json(HttpStatusCode.OK, new { value = "ok" }));
        }));

        var client = new CmsifyClient(httpClient, options);
        var result = await client.GetAsync<JsonValue>("/infinite-timeout", callerToken);

        result!.Value.ShouldBe("ok");
        sends.ShouldBe(1);
        attemptToken.ShouldBe(callerToken);
        httpClient.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
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
    public async Task ResponseObserver_SeesIntermediateAndFinalResponses()
    {
        var attempts = 0;
        var observed = new List<HttpStatusCode>();
        var client = CreateResilientClient(
            _ => ++attempts == 1
                ? Response(HttpStatusCode.ServiceUnavailable)
                : Json(HttpStatusCode.OK, new { value = "ok" }),
            options => options.ResponseObserver = (response, _) =>
            {
                observed.Add(response.StatusCode);
                return Task.CompletedTask;
            });

        await client.GetAsync<JsonValue>("/observed-retry", TestContext.Current.CancellationToken);

        observed.ShouldBe([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseObserverFailure_DoesNotRetryDisposesResponseAndPreservesException(bool timeoutFailure)
    {
        var sends = 0;
        var responseContents = new List<TrackingStringContent>();
        Exception observerException = timeoutFailure
            ? new TimeoutException("observer timeout")
            : new HttpRequestException("observer transport");
        var client = CreateResilientClient(
            _ =>
            {
                sends++;
                var content = new TrackingStringContent("{\"value\":\"ok\"}");
                responseContents.Add(content);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
            options => options.ResponseObserver = (_, _) => Task.FromException(observerException));

        var actual = await Should.ThrowAsync<Exception>(() => client.GetAsync<JsonValue>(
            "/observer-failure",
            TestContext.Current.CancellationToken));

        actual.ShouldBeSameAs(observerException);
        responseContents[0].Disposed.ShouldBeTrue();
        sends.ShouldBe(1);
    }

    [Fact]
    public async Task FinalProblemDetailsPreservesExtensionsTraceAndServerCorrelation()
    {
        var attempts = 0;
        var client = CreateResilientClient(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Response(HttpStatusCode.ServiceUnavailable);
            }

            var response = Json(HttpStatusCode.BadRequest, new
            {
                type = "https://cmsify.dev/errors/validation",
                title = "Validation failed",
                status = 400,
                traceId = "trace-final",
                detail = "Invalid value",
                category = "content",
            });
            response.Headers.TryAddWithoutValidation("X-Correlation-Id", "server-final");
            return response;
        });

        var exception = await Should.ThrowAsync<CmsifyApiException>(() => client.GetAsync<JsonValue>("/problem", TestContext.Current.CancellationToken));

        exception.TraceId.ShouldBe("trace-final");
        exception.CorrelationId.ShouldBe("server-final");
        exception.Problem.Extensions!["category"].GetString().ShouldBe("content");
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
    public async Task RetriedRead_CachesOnlyTheFinalEtagForUpdate()
    {
        var getAttempts = 0;
        string? ifMatch = null;
        var client = CreateResilientClient(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                getAttempts++;
                var response = getAttempts == 1
                    ? Response(HttpStatusCode.ServiceUnavailable)
                    : Json(HttpStatusCode.OK, new { });
                response.Headers.ETag = new EntityTagHeaderValue(getAttempts == 1 ? "\"stale\"" : "\"fresh\"");
                return response;
            }

            ifMatch = request.Headers.IfMatch.ToString();
            return Response(HttpStatusCode.NoContent);
        });
        var workspaceId = Guid.NewGuid();
        var contentId = Guid.NewGuid();

        await client.Content.GetAsync(workspaceId, contentId, ct: TestContext.Current.CancellationToken);
        await client.Content.UpdateAsync(workspaceId, contentId, new UpdateContentItemRequest(null, null, null, null, [], []), TestContext.Current.CancellationToken);

        getAttempts.ShouldBe(2);
        ifMatch.ShouldBe("\"fresh\"");
    }

    [Fact]
    public async Task NoContentResponseStillReturnsDefault()
    {
        var client = CreateResilientClient(_ => Response(HttpStatusCode.NoContent));

        var result = await client.GetAsync<JsonValue>("/no-content", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
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

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task UnsafeJsonMethods_AreNeverRetried(string method)
    {
        var calls = 0;
        var client = CreateResilientClient(_ =>
        {
            calls++;
            return Response(HttpStatusCode.ServiceUnavailable);
        });

        await Should.ThrowAsync<CmsifyApiException>(() => method switch
        {
            "POST" => client.PostAsync<object>("/unsafe", new { value = "body" }, TestContext.Current.CancellationToken),
            "PUT" => client.PutAsync<object>("/unsafe", new { value = "body" }, TestContext.Current.CancellationToken),
            _ => client.DeleteAsync<object>("/unsafe", TestContext.Current.CancellationToken),
        });

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task MultipartUpload_IsNeverRetried()
    {
        var calls = 0;
        var client = CreateResilientClient(_ =>
        {
            calls++;
            return Response(HttpStatusCode.ServiceUnavailable);
        });
        await using var stream = new MemoryStream([1, 2, 3]);

        await Should.ThrowAsync<CmsifyApiException>(() => client.Media.UploadAsync(
            Guid.NewGuid(), stream, "image.png", "image/png", ct: TestContext.Current.CancellationToken));

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task DownloadStreamCopyFailure_IsNeverRetriedOrAppended()
    {
        var calls = 0;
        var client = CreateResilientClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingCopyContent([1, 2]) };
        });
        await using var destination = new MemoryStream();

        var exception = await Should.ThrowAsync<HttpRequestException>(() => client.DownloadToAsync("/copy-failure", destination, TestContext.Current.CancellationToken));

        exception.InnerException.ShouldBeOfType<IOException>().Message.ShouldBe("copy failed");
        calls.ShouldBe(1);
        destination.ToArray().ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Download_RetriesBeforeCopyingFinalResponseWithoutDuplicateBytes()
    {
        var calls = 0;
        var client = CreateResilientClient(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([9, 9]) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        await using var destination = new MemoryStream();

        await client.DownloadToAsync("/safe-download", destination, TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
        destination.ToArray().ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task ClientOptions_ForwardCircuitAndTelemetryConfiguration()
    {
        var calls = 0;
        var retries = new List<HttpRetryTelemetry>();
        var circuitEvents = new List<HttpCircuitTelemetry>();
        var options = new CmsifyClientOptions
        {
            BaseUrl = new Uri("https://cms.test"),
            MaxRetryAttempts = 2,
            CircuitFailureRatio = 0.5,
            CircuitMinimumThroughput = 2,
            CircuitSamplingDuration = TimeSpan.FromSeconds(1),
            CircuitBreakDuration = TimeSpan.FromSeconds(1),
            OnRetry = (telemetry, _) =>
            {
                retries.Add(telemetry);
                return ValueTask.CompletedTask;
            },
            OnCircuitStateChanged = (telemetry, _) =>
            {
                circuitEvents.Add(telemetry);
                return ValueTask.CompletedTask;
            },
        };
        var client = new CmsifyClient(new HttpClient(new StubHandler(_ =>
        {
            calls++;
            var response = Response(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return response;
        })), options);

        await Should.ThrowAsync<CmsifyApiException>(() => client.GetAsync<JsonValue>("/circuit-1", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<CmsifyApiException>(() => client.GetAsync<JsonValue>("/circuit-2", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<HttpCircuitOpenException>(() => client.GetAsync<JsonValue>("/circuit-open", TestContext.Current.CancellationToken));

        calls.ShouldBe(4);
        retries.Count.ShouldBe(2);
        retries.ShouldAllBe(value => value.StatusCode == HttpStatusCode.ServiceUnavailable);
        circuitEvents.ShouldContain(value => value.State == HttpResilienceCircuitState.Open);
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

    private static CmsifyClient CreateResilientClient(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        Action<CmsifyClientOptions>? configure = null,
        Func<CmsifyClientOptions, HttpRequestResilienceOptions>? createPipelineOptions = null) =>
        CreateResilientClient((request, _) => Task.FromResult(handler(request)), configure, createPipelineOptions);

    private static CmsifyClient CreateResilientClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        Action<CmsifyClientOptions>? configure = null,
        Func<CmsifyClientOptions, HttpRequestResilienceOptions>? createPipelineOptions = null)
    {
        var options = new CmsifyClientOptions
        {
            BaseUrl = new Uri("https://cms.test"),
            EnableRetries = true,
            RequestTimeout = TimeSpan.FromSeconds(1),
        };
        configure?.Invoke(options);
        var pipeline = new HttpRequestResiliencePipeline(
            "cmsify-test",
            createPipelineOptions?.Invoke(options) ?? TestPipelineOptions(options));
        return new CmsifyClient(new HttpClient(new AsyncStubHandler(handler)), options, pipeline);
    }

    private static HttpRequestResilienceOptions TestPipelineOptions(
        CmsifyClientOptions options,
        TimeSpan? maximumDelay = null,
        Action<HttpRetryTelemetry>? onRetry = null,
        TimeProvider? timeProvider = null) => new()
        {
            MaxAttempts = options.EnableRetries ? Math.Max(1, options.MaxRetryAttempts) : 1,
            TotalRequestTimeout = options.RequestTimeout,
            BackoffBaseDelay = TimeSpan.FromMilliseconds(1),
            MaximumDelay = maximumDelay ?? TimeSpan.FromMilliseconds(1),
            TimeProvider = timeProvider ?? TimeProvider.System,
            JitterProvider = () => 0,
            OnRetry = onRetry is null
                ? null
                : (telemetry, _) =>
                {
                    onRetry(telemetry);
                    return ValueTask.CompletedTask;
                },
        };

    private static HttpResponseMessage Response(HttpStatusCode status) => new(status);

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

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class FailingCopyContent(byte[] bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes);
            throw new IOException("copy failed");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class TrackingStringContent(string value) : StringContent(value, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CallbackProgress(Action<long> callback) : IProgress<long>
    {
        public void Report(long value) => callback(value);
    }
}
