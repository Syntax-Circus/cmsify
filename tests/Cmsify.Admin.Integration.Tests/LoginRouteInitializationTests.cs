using System.Net;
using System.Security.Claims;
using Cmsify.Admin.Components;
using Cmsify.Admin.Services;
using Cmsify.Admin.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SyntaxCircus.Cmsify;

namespace Cmsify.Admin.Integration.Tests;

public sealed class LoginRouteInitializationTests
{
    [Fact]
    public async Task UnauthenticatedLoginRoute_DoesNotRequestAccountPreferences()
    {
        var api = new RecordingApiHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new TestNavigationManager("http://localhost/login"));
        services.AddSingleton<AuthenticationStateProvider>(
            new StaticAuthenticationStateProvider(new ClaimsPrincipal(new ClaimsIdentity())));
        services.AddSingleton<IJSRuntime>(new ThemeJsRuntime());
        services.AddSingleton(new CmsifyClient(
            new HttpClient(api) { BaseAddress = new Uri("http://api.test") },
            new CmsifyClientOptions { EnableRetries = false }));
        services.AddScoped<BrowserStorage>();
        services.AddScoped<AuthState>();
        services.AddScoped<UserPreferencesState>();
        services.AddScoped<WorkspaceState>();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(GuardedRouteView.RouteData)] = new RouteData(typeof(ProbePage), new Dictionary<string, object?>())
        });

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<GuardedRouteView>(parameters));

        api.RequestCount.ShouldBe(0);
    }

    private sealed class ProbePage : ComponentBase;

    private sealed class RecordingApiHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class StaticAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string uri) => Initialize("http://localhost/", uri);

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Initialize(BaseUri, ToAbsoluteUri(uri).AbsoluteUri);
    }

    private sealed class ThemeJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object result = identifier switch
            {
                "cmsifyStorage.initTheme" => new BrowserStorage.BrowserThemeState("auto", "light"),
                "cmsifyStorage.setTheme" => "light",
                _ => throw new InvalidOperationException($"Unexpected JS invocation: {identifier}")
            };
            return ValueTask.FromResult((TValue)result);
        }
    }
}
