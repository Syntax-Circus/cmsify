using System.Net;
using System.Security.Claims;
using Cmsify.Admin.Auth;
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Cmsify;
using SyntaxCircus.Http.Resilience;

namespace Cmsify.Admin.Integration.Tests;

public sealed class AdminHttpResilienceTests
{
    [Fact]
    public void CmsifyApi_RegistersOneSharedPipelineWithoutASecondResilienceHandler()
    {
        using var factory = new AdminAuthTestFactory
        {
            OidcEnabled = true,
            UseCircuitAuthenticationStateProvider = true
        };
        _ = factory.CreateClient();

        var registeredPipelines = factory.Services.GetServices<HttpRequestResiliencePipeline>().ToArray();
        registeredPipelines.Length.ShouldBe(1);
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        firstScope.ServiceProvider.GetRequiredService<HttpRequestResiliencePipeline>()
            .ShouldBeSameAs(secondScope.ServiceProvider.GetRequiredService<HttpRequestResiliencePipeline>());

        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("CmsifyApi");
        var handlerTypes = EnumerateHandlerTypes(handler).ToArray();
        handlerTypes.Count(type => type.EndsWith("ApiAuthHandler", StringComparison.Ordinal)).ShouldBe(1);
        handlerTypes.ShouldNotContain(type =>
            type.Contains("ResilienceHandler", StringComparison.OrdinalIgnoreCase)
            || type.Contains("PolicyHttpMessageHandler", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancellingOneCircuitDuringRetryDelay_DoesNotCancelOrPoisonAnotherCircuit()
    {
        await using var factory = new AdminAuthTestFactory
        {
            UseCircuitAuthenticationStateProvider = true,
            UseRecordingApiTokenAccessor = true
        };
        var cancelledAttemptSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.AsyncResponder = (request, _) =>
        {
            var response = new HttpResponseMessage(
                request.RequestUri!.AbsolutePath == "/test/cancelled-circuit"
                    ? HttpStatusCode.ServiceUnavailable
                    : HttpStatusCode.NoContent);
            if (request.RequestUri.AbsolutePath == "/test/cancelled-circuit")
            {
                cancelledAttemptSent.TrySetResult();
            }

            return Task.FromResult(response);
        };

        _ = factory.CreateClient();
        using var cancelledScope = factory.Services.CreateScope();
        using var healthyScope = factory.Services.CreateScope();
        cancelledScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = ApiTokenUser("cancelled-token");
        healthyScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = ApiTokenUser("healthy-token");
        var cancelledClient = cancelledScope.ServiceProvider.GetRequiredService<CmsifyClient>();
        var healthyClient = healthyScope.ServiceProvider.GetRequiredService<CmsifyClient>();

        using var cancellation = new CancellationTokenSource();
        var cancelledCall = cancelledClient.GetAsync<object>("/test/cancelled-circuit", cancellation.Token);
        await cancelledAttemptSent.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => cancelledCall);

        await healthyClient.GetAsync<object>("/test/healthy-circuit");
        await healthyClient.GetAsync<object>("/test/healthy-circuit");

        var cancelledRequests = factory.ObservedApiRequests.Where(request => request.Path == "/test/cancelled-circuit").ToArray();
        var healthyRequests = factory.ObservedApiRequests.Where(request => request.Path == "/test/healthy-circuit").ToArray();
        cancelledRequests.Length.ShouldBe(1);
        cancelledRequests.ShouldAllBe(request => request.Authorization == "Bearer cancelled-token");
        healthyRequests.Length.ShouldBe(2);
        healthyRequests.ShouldAllBe(request => request.Authorization == "Bearer healthy-token");
        healthyRequests.Select(request => request.CorrelationId).Distinct().Count().ShouldBe(2);
    }

    private static IEnumerable<string> EnumerateHandlerTypes(HttpMessageHandler root)
    {
        for (var current = root; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            yield return current.GetType().FullName ?? current.GetType().Name;
            if (current is not DelegatingHandler)
            {
                yield break;
            }
        }
    }

    private static ClaimsPrincipal ApiTokenUser(string token) => new(new ClaimsIdentity(
        [new Claim(CmsifyAuthClaims.ApiToken, token)],
        "test"));
}
