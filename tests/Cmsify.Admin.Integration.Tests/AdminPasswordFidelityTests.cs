using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cmsify.Admin.State;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Cmsify.Admin.Integration.Tests;

/// <summary>
/// Verifies that passwords reach the API byte-for-byte on both admin paths (login form POST and
/// change-password via AuthState). The Blazor InputText binding itself is not covered here (needs bUnit).
/// </summary>
public sealed class AdminPasswordFidelityTests : IAsyncLifetime
{
    private readonly AdminAuthTestFactory factory = new() { UseCircuitAuthenticationStateProvider = true };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await factory.DisposeAsync();

    public static TheoryData<string> Passwords => new()
    {
        "!", "$", "&", "+", "%", "%20", "=", "#", "?", "\\", "\"", "'", "<>",
        " leading", "trailing ", "inter nal", "  both  ", "tab\there",
        "\U0001F600pass\U0001F512",
        "café", "café",
        new string('a', 100),
        "p@$$ w0rd!&+=%\"'<>"
    };


    private static string Bytes(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    [Theory]
    [MemberData(nameof(Passwords))]
    public async Task Login_ForwardsPasswordToApiUnaltered(string password)
    {
        string? body = null;
        factory.Responder = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/login")
            {
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return AdminAuthTestFactory.JsonOk(new LoginResponse(
                "api-token-abc", DateTimeOffset.UtcNow.AddHours(8), false,
                new UserSummary(Guid.NewGuid(), "admin@example.com", "Admin", "Admin", IsSuperAdmin: true)));
        };
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        using var tokenResponse = await client.GetAsync("/test/antiforgery", TestContext.Current.CancellationToken);
        var token = await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var response = await client.PostAsync("/admin-auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = "admin@example.com",
            ["password"] = password,
            ["returnUrl"] = "/workspaces"
        }), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location!.OriginalString.ShouldBe("/workspaces");
        body.ShouldNotBeNull();
        using var json = JsonDocument.Parse(body);
        var received = json.RootElement.GetProperty("password").GetString()!;
        string.Equals(received, password, StringComparison.Ordinal).ShouldBeTrue($"expected {Bytes(password)} but got {Bytes(received)}");
        Bytes(received).ShouldBe(Bytes(password));
    }

    [Theory]
    [MemberData(nameof(Passwords))]
    public async Task ChangePassword_ForwardsPasswordsToApiUnaltered(string password)
    {
        string? body = null;
        factory.Responder = request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/change-password")
            {
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };
        using var _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthState>();

        await auth.ChangePasswordAsync("old " + password, password, TestContext.Current.CancellationToken);

        body.ShouldNotBeNull();
        using var json = JsonDocument.Parse(body);
        var current = json.RootElement.GetProperty("currentPassword").GetString()!;
        var next = json.RootElement.GetProperty("newPassword").GetString()!;
        string.Equals(next, password, StringComparison.Ordinal).ShouldBeTrue($"expected {Bytes(password)} but got {Bytes(next)}");
        Bytes(next).ShouldBe(Bytes(password));
        Bytes(current).ShouldBe(Bytes("old " + password));
    }
}
