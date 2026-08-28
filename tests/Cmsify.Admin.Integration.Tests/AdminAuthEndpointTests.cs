using System.Net;
using System.Net.Http.Json;
using Cmsify.Admin.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SyntaxCircus.Blazor.Auth;

namespace Cmsify.Admin.Integration.Tests;

public sealed class AdminAuthEndpointTests : IAsyncLifetime
{
    private readonly AdminAuthTestFactory factory = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
    }

    private HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> FetchAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/test/antiforgery");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static LoginResponse SuccessfulLogin(bool mustChangePassword = false) => new(
        Token: "api-token-abc",
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(8),
        MustChangePassword: mustChangePassword,
        User: new UserSummary(Guid.NewGuid(), "admin@example.com", "Admin", "Admin", IsSuperAdmin: true));

    [Fact]
    public async Task Login_WithMissingCredentials_RedirectsToLoginWithError()
    {
        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/admin-auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = string.Empty,
            ["password"] = string.Empty,
            ["returnUrl"] = "/workspaces"
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldContain("error=missing-credentials");
        factory.ObservedRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_WhenApiReturns401_RedirectsToLoginWithInvalidCredentialsError()
    {
        factory.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/admin-auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "admin@example.com",
            ["password"] = "wrong",
            ["returnUrl"] = "/workspaces"
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldContain("error=invalid-credentials");
    }

    [Fact]
    public async Task Login_OnSuccess_SetsCookieAndRedirectsToReturnUrl()
    {
        factory.Responder = _ => AdminAuthTestFactory.JsonOk(SuccessfulLogin());

        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/admin-auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "admin@example.com",
            ["password"] = "correct",
            ["returnUrl"] = "/workspaces/abc"
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldBe("/workspaces/abc");

        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        setCookies.ShouldContain(c => c.StartsWith("cmsify.admin.auth=", StringComparison.Ordinal));
        setCookies.ShouldContain(c => c.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Login_WithMustChangePassword_RedirectsToChangePassword()
    {
        factory.Responder = _ => AdminAuthTestFactory.JsonOk(SuccessfulLogin(mustChangePassword: true));

        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/admin-auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "admin@example.com",
            ["password"] = "correct",
            ["returnUrl"] = "/workspaces"
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldStartWith("/account/change-password?returnUrl=");
    }

    [Fact]
    public async Task Login_RejectsExternalReturnUrl()
    {
        factory.Responder = _ => AdminAuthTestFactory.JsonOk(SuccessfulLogin());

        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/admin-auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "admin@example.com",
            ["password"] = "correct",
            ["returnUrl"] = "//evil.example.com/path"
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldBe("/workspaces");
    }

    [Fact]
    public async Task OidcLogin_WhenEnabled_ChallengesTheConfiguredProviderAndPreservesLocalReturnUrl()
    {
        factory.OidcEnabled = true;
        var client = CreateClient();

        using var response = await client.GetAsync("/admin-auth/oidc-login?returnUrl=%2Fworkspaces");

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.AbsoluteUri.ShouldStartWith("http://identity.test/");
        response.Headers.Location.Query.ShouldContain("redirect_uri=");
        response.Headers.Location.Query.ShouldContain("state=");
    }

    [Fact]
    public async Task OidcCallback_SavesTokensAndForwardsTheAccessTokenToTheApi()
    {
        factory.OidcEnabled = true;
        factory.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var client = CreateClient();

        var callback = await BeginOidcSignInAsync(client);

        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        callback.Headers.Location!.OriginalString.ShouldBe("/workspaces");
        callback.Headers.GetValues("Set-Cookie").ShouldContain(cookie =>
            cookie.StartsWith("cmsify.admin.auth=", StringComparison.Ordinal));
        factory.OidcTokenRequests.ShouldContain(request => request.GrantType == "authorization_code");

        using var apiCall = await client.GetAsync("/test/api-call");
        apiCall.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.ObservedRequests.Any(request =>
            request.Headers.Authorization is { Scheme: "Bearer", Parameter: "initial-access-token" }).ShouldBeTrue();
    }

    [Fact]
    public async Task OidcExpiredAccessToken_RefreshesAndForwardsTheReplacementToken()
    {
        factory.OidcEnabled = true;
        factory.OidcAccessTokenExpiresImmediately = true;
        factory.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var client = CreateClient();

        using (var callback = await BeginOidcSignInAsync(client))
        {
            callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        }

        using var apiCall = await client.GetAsync("/test/api-call");
        apiCall.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.OidcTokenRequests.ShouldContain(request => request.GrantType == "refresh_token" && request.RefreshToken == "refresh-token");
        factory.ObservedRequests.Any(request =>
            request.Headers.Authorization is { Scheme: "Bearer", Parameter: "refreshed-access-token" }).ShouldBeTrue();
    }

    [Fact]
    public async Task OidcRefreshFailure_ExpiresTheUsableApiSession()
    {
        factory.OidcEnabled = true;
        factory.OidcAccessTokenExpiresImmediately = true;
        factory.OidcRefreshSucceeds = false;
        factory.Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var client = CreateClient();

        using (var callback = await BeginOidcSignInAsync(client))
        {
            callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        }

        using var apiCall = await client.GetAsync("/test/api-call");
        apiCall.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.OidcTokenRequests.ShouldContain(request => request.GrantType == "refresh_token");
        factory.ObservedRequests.Last().Headers.Authorization.ShouldBeNull();
        var cache = factory.Services.GetRequiredService<IServerTokenCache>();
        (await cache.GetAsync("user:oidc-admin")).ShouldBeNull();
    }

    [Fact]
    public async Task OidcLogout_EvictsTheCachedTokenBeforeRemoteSignOut()
    {
        factory.OidcEnabled = true;
        factory.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var client = CreateClient();

        using (var callback = await BeginOidcSignInAsync(client))
        {
            callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        }

        using (var apiCall = await client.GetAsync("/test/api-call"))
        {
            apiCall.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var cache = factory.Services.GetRequiredService<IServerTokenCache>();
        (await cache.GetAsync("user:oidc-admin")).ShouldNotBeNull();

        var token = await FetchAntiforgeryTokenAsync(client);
        using var logout = await client.PostAsync("/admin-auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        }));

        (await cache.GetAsync("user:oidc-admin")).ShouldBeNull();
    }

    [Fact]
    public async Task OidcLogout_RemotelySignsOutAndClearsTheLocalCookie()
    {
        factory.OidcEnabled = true;
        var client = CreateClient();
        using (var callback = await BeginOidcSignInAsync(client))
        {
            callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        }

        var token = await FetchAntiforgeryTokenAsync(client);
        using var response = await client.PostAsync("/admin-auth/logout", new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("__RequestVerificationToken", token)]));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.AbsoluteUri.ShouldStartWith("http://identity.test/connect/logout");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location.Query);
        query["post_logout_redirect_uri"].ToString().ShouldBe("http://localhost/login");
        response.Headers.GetValues("Set-Cookie").ShouldContain(cookie =>
            cookie.StartsWith("cmsify.admin.auth=", StringComparison.Ordinal)
            && cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<HttpResponseMessage> BeginOidcSignInAsync(HttpClient client)
    {
        using var challenge = await client.GetAsync("/admin-auth/oidc-login?returnUrl=%2Fworkspaces");
        challenge.StatusCode.ShouldBe(HttpStatusCode.Found);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        var state = query["state"].ToString();
        state.ShouldNotBeNullOrWhiteSpace();
        return await client.GetAsync($"/signin-oidc?code=test-code&state={Uri.EscapeDataString(state)}");
    }

    [Fact]
    public async Task Logout_SignsOutAndRedirectsToLogin()
    {
        factory.Responder = request =>
        {
            // The logout call to the API should succeed.
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/logout", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return AdminAuthTestFactory.JsonOk(SuccessfulLogin());
        };

        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        // Establish a session first.
        using (await client.PostAsync("/admin-auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "admin@example.com",
            ["password"] = "correct",
            ["returnUrl"] = "/workspaces"
        }))) { }

        // Refresh antiforgery token (the auth cookie change may invalidate the prior token tied to the unauthenticated identity).
        var logoutToken = await FetchAntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/admin-auth/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldBe("/login");

        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        setCookies.ShouldContain(c => c.StartsWith("cmsify.admin.auth=", StringComparison.Ordinal)
            && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        factory.ObservedRequests.ShouldContain(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/auth/logout", StringComparison.Ordinal)
            && r.Headers.Authorization != null
            && r.Headers.Authorization.Scheme == "Bearer"
            && r.Headers.Authorization.Parameter == "api-token-abc");
    }

    [Fact]
    public async Task RefreshClaims_WithoutCookie_Returns401()
    {
        var client = CreateClient();

        using var response = await client.PostAsync("/admin-auth/refresh-claims", new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshClaims_AfterPasswordChange_ClearsMustChangePasswordFlag()
    {
        factory.Responder = _ => AdminAuthTestFactory.JsonOk(SuccessfulLogin(mustChangePassword: true));

        var client = CreateClient();
        var token = await FetchAntiforgeryTokenAsync(client);

        using (await client.PostAsync("/admin-auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "admin@example.com",
            ["password"] = "correct",
            ["returnUrl"] = "/workspaces"
        }))) { }

        using var refresh = await client.PostAsync("/admin-auth/refresh-claims", new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        refresh.Headers.ShouldContain(h => h.Key == "Set-Cookie"
            && h.Value.Any(v => v.StartsWith("cmsify.admin.auth=", StringComparison.Ordinal)));
    }
}
