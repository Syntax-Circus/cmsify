using System.Security.Claims;
using Cmsify.Admin.Components.Auth;
using Cmsify.Admin.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SyntaxCircus.Blazor.Auth;

namespace Cmsify.Admin.Integration.Tests;

public sealed class OidcSessionExpiryRedirectorTests
{
    [Fact]
    public async Task Routes_OidcExpiryForCurrentCircuit_SubmitsLogoutOnceAndIgnoresOtherUsersAfterDisposal()
    {
        var broker = new SessionExpiryBroker();
        var js = new RecordingJsRuntime();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(broker);
        services.AddScoped<SessionStateService>();
        services.AddSingleton<IUserTokenCacheKeyProvider, UserTokenCacheKeyProvider>();
        services.AddScoped<AuthenticationStateProvider>(_ => new StaticAuthenticationStateProvider(OidcUser("oidc-admin")));
        services.AddSingleton<IJSRuntime>(js);

        await using var provider = services.BuildServiceProvider();
        var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<OidcSessionExpiryRedirector>());

        broker.Publish("user:another-user");
        await Task.Delay(50);
        js.CallCount.ShouldBe(0);

        broker.Publish("user:oidc-admin");
        await js.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        js.CallCount.ShouldBe(1);

        broker.Publish("user:oidc-admin");
        await Task.Delay(50);
        js.CallCount.ShouldBe(1);

        await renderer.DisposeAsync();
        broker.Publish("user:oidc-admin");
        await Task.Delay(50);
        js.CallCount.ShouldBe(1);
    }

    private static ClaimsPrincipal OidcUser(string subject) => new(new ClaimsIdentity(
        [new Claim("sub", subject), new Claim(CmsifyAuthClaims.OidcSession, "true")], "oidc"));

    private sealed class StaticAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public TaskCompletionSource Invoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "cmsifyAuth.submitLogout")
            {
                CallCount++;
                Invoked.TrySetResult();
            }
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
