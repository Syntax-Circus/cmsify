using System.Net;
using System.Collections.Concurrent;
using System.Security.Claims;
using Cmsify.Admin.Auth;
using Cmsify.Admin.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SyntaxCircus.Blazor.Auth;
using SyntaxCircus.Cmsify;

namespace Cmsify.Admin.Integration.Tests;

public sealed class OidcCircuitTokenForwardingTests : IAsyncLifetime
{
    private readonly AdminAuthTestFactory factory = new()
    {
        OidcEnabled = true,
        UseCircuitAuthenticationStateProvider = true,
        UseRecordingApiTokenAccessor = true,
        Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent)
    };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await factory.DisposeAsync();

    [Fact]
    public async Task RenderedOidcCircuit_ScopedCmsifyClientForwardsOnlyTheRenderedUsersBearer()
    {
        _ = factory.CreateClient();
        var cache = factory.Services.GetRequiredService<IServerTokenCache>();
        await cache.SetAsync("user:oidc-admin", Token("circuit-access-token"), CancellationToken.None);

        using var renderScope = factory.Services.CreateScope();
        renderScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = OidcUser("oidc-admin");
        renderScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext.ShouldBeNull();
        (await renderScope.ServiceProvider.GetRequiredService<IApiTokenAccessor>().GetTokenAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
        using (var handlerScope = factory.Services.CreateScope())
        {
            handlerScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal.Identity!.IsAuthenticated.ShouldBeFalse();
        }

        await using var renderer = new HtmlRenderer(renderScope.ServiceProvider, NullLoggerFactory.Instance);
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SingleCircuitCmsifyClientProbe>());

        var request = factory.ObservedRequests.Single(request => request.RequestUri!.AbsolutePath == "/test/circuit-admin");
        (request.Headers.Authorization?.ToString()).ShouldBe("Bearer circuit-access-token");
    }

    [Fact]
    public async Task ConcurrentRenderedOidcCircuits_RetryWithFreshCorrelationAndKeepBearerAndObserverScoped()
    {
        var attempts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var bothFirstAttemptsArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttemptsArrived = 0;
        var oneExpiries = new[]
        {
            new DateTimeOffset(2030, 1, 1, 0, 0, 1, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 1, 0, 0, 2, TimeSpan.Zero)
        };
        var twoExpiries = new[]
        {
            new DateTimeOffset(2040, 2, 2, 0, 0, 1, TimeSpan.Zero),
            new DateTimeOffset(2040, 2, 2, 0, 0, 2, TimeSpan.Zero)
        };
        factory.AsyncResponder = async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var attempt = attempts.AddOrUpdate(path, 1, static (_, current) => current + 1);
            if (attempt == 1)
            {
                if (Interlocked.Increment(ref firstAttemptsArrived) == 2)
                {
                    bothFirstAttemptsArrived.TrySetResult();
                }

                await bothFirstAttemptsArrived.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }

            var response = new HttpResponseMessage(attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            var expiries = path == "/test/circuit-one" ? oneExpiries : twoExpiries;
            response.Headers.TryAddWithoutValidation("X-Session-Expires-At", expiries[attempt - 1].ToString("O"));
            return response;
        };

        _ = factory.CreateClient();
        var cache = factory.Services.GetRequiredService<IServerTokenCache>();
        await cache.SetAsync("user:one", Token("token-one"), CancellationToken.None);
        await cache.SetAsync("user:two", Token("token-two"), CancellationToken.None);

        using var oneScope = factory.Services.CreateScope();
        using var twoScope = factory.Services.CreateScope();
        oneScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = OidcUser("one");
        twoScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = OidcUser("two");
        var oneObserver = oneScope.ServiceProvider.GetRequiredService<RecordingApiTokenAccessor>();
        var twoObserver = twoScope.ServiceProvider.GetRequiredService<RecordingApiTokenAccessor>();
        await using var oneRenderer = new HtmlRenderer(oneScope.ServiceProvider, NullLoggerFactory.Instance);
        await using var twoRenderer = new HtmlRenderer(twoScope.ServiceProvider, NullLoggerFactory.Instance);

        var oneRender = oneRenderer.Dispatcher.InvokeAsync(() => oneRenderer.RenderComponentAsync<FirstCircuitCmsifyClientProbe>());
        var twoRender = twoRenderer.Dispatcher.InvokeAsync(() => twoRenderer.RenderComponentAsync<SecondCircuitCmsifyClientProbe>());
        await Task.WhenAll(oneRender, twoRender);

        var oneRequests = factory.ObservedApiRequests.Where(request => request.Path == "/test/circuit-one").ToArray();
        var twoRequests = factory.ObservedApiRequests.Where(request => request.Path == "/test/circuit-two").ToArray();
        oneRequests.Length.ShouldBe(2);
        twoRequests.Length.ShouldBe(2);
        oneRequests.ShouldAllBe(request => request.Authorization == "Bearer token-one");
        twoRequests.ShouldAllBe(request => request.Authorization == "Bearer token-two");
        oneRequests.Concat(twoRequests).Select(request => request.CorrelationId).ShouldAllBe(correlationId => !string.IsNullOrWhiteSpace(correlationId));
        oneRequests.Concat(twoRequests).Select(request => request.CorrelationId).Distinct().Count().ShouldBe(4);
        oneObserver.Expiries.ShouldBe(oneExpiries);
        twoObserver.Expiries.ShouldBe(twoExpiries);
    }

    private static ServerTokenCacheEntry Token(string accessToken) => new(
        accessToken,
        "refresh-token",
        null,
        DateTimeOffset.UtcNow.AddHours(1));

    private static ClaimsPrincipal OidcUser(string subject) => new(new ClaimsIdentity(
        [new Claim("sub", subject), new Claim(CmsifyAuthClaims.OidcSession, "true")],
        "oidc"));

    private sealed class SingleCircuitCmsifyClientProbe : CircuitCmsifyClientProbe
    {
        protected override string Path => "/test/circuit-admin";
    }

    private sealed class FirstCircuitCmsifyClientProbe : CircuitCmsifyClientProbe
    {
        protected override string Path => "/test/circuit-one";
    }

    private sealed class SecondCircuitCmsifyClientProbe : CircuitCmsifyClientProbe
    {
        protected override string Path => "/test/circuit-two";
    }

    private abstract class CircuitCmsifyClientProbe : ComponentBase
    {
        [Inject] private CmsifyClient Cmsify { get; set; } = null!;

        protected abstract string Path { get; }

        protected override async Task OnInitializedAsync()
        {
            await Cmsify.GetAsync<object>(Path, CancellationToken.None);
        }
    }
}

internal sealed class CircuitIdentitySlot
{
    public ClaimsPrincipal Principal { get; set; } = new(new ClaimsIdentity());
}

internal sealed class CircuitAuthenticationStateProvider(CircuitIdentitySlot slot) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(slot.Principal));
}
