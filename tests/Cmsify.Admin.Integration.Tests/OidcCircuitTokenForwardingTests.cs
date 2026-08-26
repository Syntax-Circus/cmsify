using System.Net;
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
        Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent)
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await factory.DisposeAsync();

    [Fact]
    public async Task RenderedOidcCircuit_ScopedCmsifyClientForwardsOnlyTheRenderedUsersBearer()
    {
        _ = factory.CreateClient();
        var cache = factory.Services.GetRequiredService<IServerTokenCache>();
        await cache.SetAsync("user:oidc-admin", Token("circuit-access-token"), CancellationToken.None);

        using var renderScope = factory.Services.CreateScope();
        renderScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = OidcUser("oidc-admin");
        renderScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext.ShouldBeNull();
        (await renderScope.ServiceProvider.GetRequiredService<IApiTokenAccessor>().GetTokenAsync()).ShouldBeNull();
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
    public async Task ConcurrentRenderedOidcCircuits_KeepEachScopedCmsifyClientBearerOnItsOwnUri()
    {
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        factory.AsyncResponder = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                bothArrived.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        _ = factory.CreateClient();
        var cache = factory.Services.GetRequiredService<IServerTokenCache>();
        await cache.SetAsync("user:one", Token("token-one"), CancellationToken.None);
        await cache.SetAsync("user:two", Token("token-two"), CancellationToken.None);

        using var oneScope = factory.Services.CreateScope();
        using var twoScope = factory.Services.CreateScope();
        oneScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = OidcUser("one");
        twoScope.ServiceProvider.GetRequiredService<CircuitIdentitySlot>().Principal = OidcUser("two");
        await using var oneRenderer = new HtmlRenderer(oneScope.ServiceProvider, NullLoggerFactory.Instance);
        await using var twoRenderer = new HtmlRenderer(twoScope.ServiceProvider, NullLoggerFactory.Instance);

        var oneRender = oneRenderer.Dispatcher.InvokeAsync(() => oneRenderer.RenderComponentAsync<FirstCircuitCmsifyClientProbe>());
        var twoRender = twoRenderer.Dispatcher.InvokeAsync(() => twoRenderer.RenderComponentAsync<SecondCircuitCmsifyClientProbe>());
        await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        release.TrySetResult();
        await Task.WhenAll(oneRender, twoRender);

        (factory.ObservedRequests.Single(request => request.RequestUri!.AbsolutePath == "/test/circuit-one")
            .Headers.Authorization?.ToString()).ShouldBe("Bearer token-one");
        (factory.ObservedRequests.Single(request => request.RequestUri!.AbsolutePath == "/test/circuit-two")
            .Headers.Authorization?.ToString()).ShouldBe("Bearer token-two");
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
