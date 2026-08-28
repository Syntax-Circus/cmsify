using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Http.Resilience;

namespace SyntaxCircus.Cmsify.Client.Tests;

public sealed class CmsifyClientDependencyInjectionTests
{
    [Fact]
    public async Task AddCmsifyClient_MatchesDirectTransportRetryOutcome()
    {
        var directHandler = TransportThenSuccessHandler();
        var directOptions = Options(maxAttempts: 2);
        var directPipeline = new HttpRequestResiliencePipeline("cmsify-direct", PipelineOptions(directOptions));
        var direct = new CmsifyClient(new HttpClient(directHandler), directOptions, directPipeline);

        var diHandler = TransportThenSuccessHandler();
        var services = new ServiceCollection();
        services.AddCmsifyClient(options =>
        {
            options.BaseUrl = new Uri("https://cms.test");
            options.MaxRetryAttempts = 2;
            options.RequestTimeout = TimeSpan.FromSeconds(2);
        }).ConfigurePrimaryHttpMessageHandler(() => diHandler);
        using var provider = services.BuildServiceProvider();
        var fromDi = provider.GetRequiredService<CmsifyClient>();

        var directResult = await direct.GetAsync<JsonValue>("/parity", TestContext.Current.CancellationToken);
        var diResult = await fromDi.GetAsync<JsonValue>("/parity", TestContext.Current.CancellationToken);

        directResult!.Value.ShouldBe("ok");
        diResult!.Value.ShouldBe("ok");
        directHandler.SendCount.ShouldBe(2);
        diHandler.SendCount.ShouldBe(2);
    }

    [Fact]
    public async Task AddCmsifyClient_AppliesExactlyOneRetryPolicy()
    {
        var handler = new CountingHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)));
        var services = new ServiceCollection();
        services.AddCmsifyClient(options =>
        {
            options.BaseUrl = new Uri("https://cms.test");
            options.MaxRetryAttempts = 2;
            options.RequestTimeout = TimeSpan.FromSeconds(2);
        }).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<CmsifyClient>();

        await Should.ThrowAsync<CmsifyApiException>(() => client.GetAsync<JsonValue>("/exhausted", TestContext.Current.CancellationToken));

        handler.SendCount.ShouldBe(2);
    }

    [Fact]
    public async Task AddCmsifyClient_SharesOneCircuitPipelineAcrossResolvedClients()
    {
        var handler = new CountingHandler((_, _, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)));
        var services = new ServiceCollection();
        services.AddCmsifyClient(options =>
        {
            options.BaseUrl = new Uri("https://cms.test");
            options.MaxRetryAttempts = 1;
            options.RequestTimeout = TimeSpan.FromSeconds(2);
            options.CircuitFailureRatio = 0.5;
            options.CircuitMinimumThroughput = 2;
            options.CircuitSamplingDuration = TimeSpan.FromSeconds(1);
            options.CircuitBreakDuration = TimeSpan.FromSeconds(1);
        }).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<CmsifyClient>();
        var second = provider.GetRequiredService<CmsifyClient>();

        await Should.ThrowAsync<CmsifyApiException>(() => first.GetAsync<JsonValue>("/circuit-1", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<CmsifyApiException>(() => second.GetAsync<JsonValue>("/circuit-2", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<HttpCircuitOpenException>(() => first.GetAsync<JsonValue>("/circuit-open", TestContext.Current.CancellationToken));

        handler.SendCount.ShouldBe(2);
    }

    private static CountingHandler TransportThenSuccessHandler() => new((attempt, _, _) =>
        attempt == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("transport"))
            : Task.FromResult(Json(HttpStatusCode.OK, new { value = "ok" })));

    private static CmsifyClientOptions Options(int maxAttempts) => new()
    {
        BaseUrl = new Uri("https://cms.test"),
        MaxRetryAttempts = maxAttempts,
        RequestTimeout = TimeSpan.FromSeconds(2),
    };

    private static HttpRequestResilienceOptions PipelineOptions(CmsifyClientOptions options) => new()
    {
        MaxAttempts = options.MaxRetryAttempts,
        TotalRequestTimeout = options.RequestTimeout,
        BackoffBaseDelay = TimeSpan.FromMilliseconds(1),
        MaximumDelay = TimeSpan.FromMilliseconds(1),
        JitterProvider = () => 0,
    };

    private static HttpResponseMessage Response(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private sealed record JsonValue(string Value);

    private sealed class CountingHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private int sendCount;

        public int SendCount => Volatile.Read(ref sendCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref sendCount);
            return handler(attempt, request, cancellationToken);
        }
    }
}
