using System.Net;
using Cmsify.Admin.Auth;
using Cmsify.Admin.State;
using Microsoft.AspNetCore.Components;
using SyntaxCircus.Cmsify;
using SyntaxCircus.Cmsify.Contracts;

namespace Cmsify.Admin.Integration.Tests;

public sealed class SessionExpiryHandlerTests
{
    [Theory]
    [InlineData("/api/v1/workspaces", true)]
    [InlineData("/api/v1/auth/me", true)]
    [InlineData("/api/v1/auth/refresh", true)]
    [InlineData("/api/v1/auth/change-password", false)]
    [InlineData("/api/v1/auth/Change-Password/", false)]
    [InlineData("/api/v1/auth/login", false)]
    public void IsSessionExpiryResponse_401_DependsOnPath(string path, bool expected)
    {
        SessionExpiryHandler.IsSessionExpiryResponse(Response(HttpStatusCode.Unauthorized, path)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public void IsSessionExpiryResponse_NonUnauthorized_IsFalse(HttpStatusCode status)
    {
        SessionExpiryHandler.IsSessionExpiryResponse(Response(status, "/api/v1/workspaces")).ShouldBeFalse();
    }

    [Fact]
    public void IsSessionExpiryResponse_401WithoutToken_IsFalse()
    {
        SessionExpiryHandler.IsSessionExpiryResponse(Response(HttpStatusCode.Unauthorized, "/api/v1/workspaces", withToken: false))
            .ShouldBeFalse();
    }

    [Fact]
    public void TryHandleResponse_InCircuit_RedirectsOnceWithEscapedReturnUrl()
    {
        var nav = new RecordingNavigationManager("http://localhost/workspaces/abc/content?q=a b&x=1#frag");
        var handler = Open(nav);

        handler.TryHandleResponse(Response(HttpStatusCode.Unauthorized, "/api/v1/workspaces")).ShouldBeTrue();
        handler.TryHandleResponse(Response(HttpStatusCode.Unauthorized, "/api/v1/workspaces")).ShouldBeTrue();

        handler.HasFired.ShouldBeTrue();
        nav.Navigations.Count.ShouldBe(1);
        var (uri, options) = nav.Navigations[0];
        uri.ShouldBe("/admin-auth/session-expired?returnUrl=" + Uri.EscapeDataString("/workspaces/abc/content?q=a b&x=1"));
        options.ForceLoad.ShouldBeTrue();
        options.ReplaceHistoryEntry.ShouldBeTrue();
    }

    [Fact]
    public void TryHandleResponse_ExcludedPathsAndOtherStatuses_DoNotRedirect()
    {
        var nav = new RecordingNavigationManager("http://localhost/account/change-password");
        var handler = Open(nav);

        handler.TryHandleResponse(Response(HttpStatusCode.Unauthorized, "/api/v1/auth/change-password")).ShouldBeFalse();
        handler.TryHandleResponse(Response(HttpStatusCode.Unauthorized, "/api/v1/auth/login")).ShouldBeFalse();
        handler.TryHandleResponse(Response(HttpStatusCode.Forbidden, "/api/v1/workspaces")).ShouldBeFalse();

        handler.HasFired.ShouldBeFalse();
        nav.Navigations.ShouldBeEmpty();
    }

    [Fact]
    public void TryHandleResponse_OutsideCircuit_IsNoOp()
    {
        var nav = new RecordingNavigationManager("http://localhost/login");
        var handler = new SessionExpiryHandler(nav); // circuit never opened: login/logout endpoints and prerender

        handler.TryHandleResponse(Response(HttpStatusCode.Unauthorized, "/api/v1/workspaces")).ShouldBeFalse();

        handler.HasFired.ShouldBeFalse();
        nav.Navigations.ShouldBeEmpty();
    }

    [Fact]
    public void Redirect_AfterGuardRedirect_DoesNotNavigateTwice()
    {
        var nav = new RecordingNavigationManager("http://localhost/workspaces");
        var handler = Open(nav);

        handler.Redirect("/workspaces");
        handler.TryRedirectInCircuit().ShouldBeTrue();

        nav.Navigations.Count.ShouldBe(1);
    }

    [Fact]
    public void TryHandleException_Only401Redirects()
    {
        var nav = new RecordingNavigationManager("http://localhost/templates");
        var handler = Open(nav);

        handler.TryHandleException(new CmsifyApiException(HttpStatusCode.Forbidden, new ProblemDetailsModel(null, null, null, null, null, null, null, null), "c")).ShouldBeFalse();
        handler.TryHandleException(new InvalidOperationException()).ShouldBeFalse();
        nav.Navigations.ShouldBeEmpty();

        handler.TryHandleException(new CmsifyApiException(HttpStatusCode.Unauthorized, new ProblemDetailsModel(null, null, null, null, null, null, null, null), "c")).ShouldBeTrue();
        nav.Navigations.Count.ShouldBe(1);
    }

    [Fact]
    public void ToastDanger_IsSuppressedAfterSessionExpiry()
    {
        var handler = Open(new RecordingNavigationManager("http://localhost/x"));
        var toasts = new ToastState(handler);

        toasts.Danger("before");
        toasts.Message.ShouldBe("before");

        handler.Redirect("/x");
        toasts.Danger("after");

        toasts.Message.ShouldBe("before");
    }

    [Fact]
    public async Task EndToEnd_ClientObserver_RedirectsBeforeExceptionReachesCaller()
    {
        var nav = new RecordingNavigationManager("http://localhost/workspaces");
        var handler = Open(nav);
        var client = new CmsifyClient(
            new HttpClient(new StatusHandler(HttpStatusCode.Unauthorized)) { BaseAddress = new Uri("http://api.test") },
            new CmsifyClientOptions
            {
                EnableRetries = false,
                TokenProvider = _ => ValueTask.FromResult<string?>("dead-token"),
                ResponseObserver = (response, _) =>
                {
                    handler.TryHandleResponse(response);
                    return Task.CompletedTask;
                }
            });

        var ex = await Should.ThrowAsync<CmsifyApiException>(() => client.Workspaces.ListAsync(1, 20, TestContext.Current.CancellationToken));
        SessionExpiryHandler.IsUnauthorized(ex).ShouldBeTrue();
        nav.Navigations.Count.ShouldBe(1);

        // change-password with a wrong current password must not trigger.
        var nav2 = new RecordingNavigationManager("http://localhost/account/change-password");
        var handler2 = Open(nav2);
        var client2 = new CmsifyClient(
            new HttpClient(new StatusHandler(HttpStatusCode.Unauthorized)) { BaseAddress = new Uri("http://api.test") },
            new CmsifyClientOptions
            {
                EnableRetries = false,
                TokenProvider = _ => ValueTask.FromResult<string?>("live-token"),
                ResponseObserver = (response, _) =>
                {
                    handler2.TryHandleResponse(response);
                    return Task.CompletedTask;
                }
            });
        await Should.ThrowAsync<CmsifyApiException>(() =>
            client2.Auth.ChangePasswordAsync(new ChangePasswordRequest("wrong", "NewPassw0rd!xyz"), TestContext.Current.CancellationToken));
        nav2.Navigations.ShouldBeEmpty();
    }

    private static SessionExpiryHandler Open(NavigationManager navigation)
    {
        var handler = new SessionExpiryHandler(navigation);
        handler.OnCircuitOpenedAsync(null!, CancellationToken.None).GetAwaiter().GetResult();
        return handler;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string path, bool withToken = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://api.test" + path);
        if (withToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");
        }

        return new HttpResponseMessage(status) { RequestMessage = request };
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public RecordingNavigationManager(string uri) => Initialize("http://localhost/", uri);

        public List<(string Uri, NavigationOptions Options)> Navigations { get; } = [];

        protected override void NavigateToCore(string uri, NavigationOptions options) => Navigations.Add((uri, options));
    }
}
