using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Cmsify.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyntaxCircus.Cmsify.Contracts;
using Testcontainers.PostgreSql;

namespace Cmsify.Api.Integration.Tests;

public sealed class UnauthorizedResponseLoggingTests : IAsyncLifetime
{
    private const string AdminEmail = "admin@example.test";
    private const string AdminPassword = "change-this-temporary-password";

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("cmsify")
        .WithUsername("cmsify")
        .WithPassword("cmsify")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Seed__Admin__Email", AdminEmail);
        Environment.SetEnvironmentVariable("Seed__Admin__Password", AdminPassword);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", "Default");
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", "default");
    }

    public async ValueTask DisposeAsync()
    {
        await postgres.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__Cmsify", null);
        Environment.SetEnvironmentVariable("Seed__Admin__Email", null);
        Environment.SetEnvironmentVariable("Seed__Admin__Password", null);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Name", null);
        Environment.SetEnvironmentVariable("Seed__DefaultWorkspace__Slug", null);
    }

    [Fact]
    public async Task UnauthenticatedCall_ToRequireRoleEndpoint_LogsWarningWithoutBearer()
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Message)>();
        await using var factory = CreateFactory(logs);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = Assert.Single(logs, log => log.Message.StartsWith("Unauthorized response.", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Method=GET", entry.Message);
        Assert.Contains("Path=/api/v1/auth/me", entry.Message);
        Assert.Contains("BearerKind=none", entry.Message);
        Assert.Contains("ActorAuthenticated=False", entry.Message);
        Assert.Contains("Source=RequireRole filter", entry.Message);
        Assert.Contains("CorrelationId=", entry.Message);
        Assert.Contains("RemoteIp=", entry.Message);
    }

    [Theory]
    [InlineData("dead-session-token-value", "opaque")]
    [InlineData("cmsify_deadbeefdeadbeef-secret-part", "api-client")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnLXNlY3JldA", "jwt")]
    public async Task RejectedBearer_IsLoggedByKind_WithoutTheToken(string token, string expectedKind)
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Message)>();
        await using var factory = CreateFactory(logs);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = Assert.Single(logs, log => log.Message.StartsWith("Unauthorized response.", StringComparison.Ordinal));
        Assert.Contains($"BearerKind={expectedKind}", entry.Message);
        Assert.Contains("bearer-not-resolved-to-actor", entry.Message);
        Assert.DoesNotContain(logs, log => log.Message.Contains(token, StringComparison.Ordinal) || log.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BadLogin_Logs401WithEndpointSource_WithoutPassword()
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Message)>();
        await using var factory = CreateFactory(logs);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, "wrong-password-value"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entry = Assert.Single(logs, log => log.Message.StartsWith("Unauthorized response.", StringComparison.Ordinal));
        Assert.Contains("Method=POST", entry.Message);
        Assert.Contains("Path=/api/v1/auth/login", entry.Message);
        Assert.Contains("BearerKind=none", entry.Message);
        Assert.Contains("Source=endpoint", entry.Message);
        Assert.DoesNotContain(logs, log => log.Message.Contains("wrong-password-value", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulLogin_DoesNotLogUnauthorizedWarning()
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Message)>();
        await using var factory = CreateFactory(logs);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.DoesNotContain(logs, log => log.Message.StartsWith("Unauthorized response.", StringComparison.Ordinal));
    }


    [Theory]
    [InlineData("/api/v1/auth/login", "POST")]
    [InlineData("/api/v1/auth/me", "GET")]
    public async Task OidcEnabled_WithUnreachableAuthority_AndJwtShapedBearer_DoesNotBlockAnonymousLogin(string path, string method)
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Message)>();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Auth:Oidc:Enabled", "true");
            builder.UseSetting("Auth:Oidc:Authority", "http://127.0.0.1:1");
            builder.UseSetting("Auth:Oidc:RequireHttpsMetadata", "false");
            builder.UseSetting("Auth:Oidc:Audiences:0", "cmsify");
            builder.UseSetting("Secrets:ActiveKeyId", "integration");
            builder.UseSetting("Secrets:EncryptionKeys:integration", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("TrustedProxy:RequireTrustedProxiesInProduction", "false");
            builder.ConfigureServices(services => services.AddSingleton<ILogger<UnauthorizedResponseLoggingMiddleware>>(new CapturingLogger(logs)));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new LoginRequest(AdminEmail, AdminPassword));
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Reproduction attempt for the "401 on login with no controller log" mystery: an unvalidatable JWT does not
        // reject the [AllowAnonymous] login (the composite handler fails soft and CmsifyAuthMiddleware maps it to
        // anonymous), while an authenticated endpoint still returns a logged 401.
        Assert.Equal(method == "POST" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(method != "POST", logs.Any(log => log.Message.Contains("BearerKind=jwt", StringComparison.Ordinal)));
    }

    private static WebApplicationFactory<Program> CreateFactory(ConcurrentQueue<(LogLevel Level, string Message)> logs)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILogger<UnauthorizedResponseLoggingMiddleware>>(new CapturingLogger(logs))));

    private sealed class CapturingLogger(ConcurrentQueue<(LogLevel Level, string Message)> logs) : ILogger<UnauthorizedResponseLoggingMiddleware>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => logs.Enqueue((logLevel, formatter(state, exception)));
    }
}
